using Game_Store_API.Data;
using Npgsql;
using Game_Store_API.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using Microsoft.OpenApi.Models;
using Game_Store_API.Logging;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
var loggingHealth = new LoggingHealth();
var logDirectory = builder.Configuration["ApplicationLogging:Directory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameStore", "logs");
logDirectory = Path.GetFullPath(logDirectory);
var webRoot = Path.GetFullPath(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));
if (logDirectory.Equals(webRoot, StringComparison.OrdinalIgnoreCase)
    || logDirectory.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Application logs must be outside the public web directory.");
Directory.CreateDirectory(logDirectory);
var probe = Path.Combine(logDirectory, $"{Guid.NewGuid():N}.probe");
File.WriteAllText(probe, "logging-write-check");
File.Delete(probe);
Serilog.Debugging.SelfLog.Enable(_ =>
{
    loggingHealth.FileFailed();
    Console.Error.WriteLine("Application logging sink failed. Inspect log storage permissions and free space.");
});
builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, configuration) => configuration
    .MinimumLevel.Information().MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new SafeLogFormatter())
    .WriteTo.File(new SafeLogFormatter(), Path.Combine(logDirectory, "application-.jsonl"),
        rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 25 * 1024 * 1024,
        rollOnFileSizeLimit: true, retainedFileCountLimit: 31, shared: true));
builder.Services.AddSingleton(loggingHealth);
builder.Services.AddSingleton<AuditStore>();
builder.Services.AddHostedService<AuditRetentionWorker>();
builder.Services.AddHttpContextAccessor();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Log in using /api/auth/login, then paste the accessToken here (without the Bearer prefix)."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security = [new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                }] = []
            }];
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddControllers();
var connectionString = builder.Configuration.GetConnectionString("GameStore")
    ?? throw new InvalidOperationException("Connection string 'GameStore' is required.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres");
builder.Services.AddHealthChecks().AddCheck<LoggingHealth>("logging");
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException("JWT configuration is required.");
var signingKey = jwt.GetSigningKey();
builder.Services.AddSingleton(jwt);
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<AccountRecoveryService>();
builder.Services.AddOptions<EmailSettings>().Bind(builder.Configuration.GetSection("Email"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port == 587
        && !string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.From)
        && !string.IsNullOrWhiteSpace(options.Password), "Email SMTP settings and password are required (STARTTLS port 587).")
    .ValidateOnStart();
builder.Services.AddSingleton<IAccountEmailSender, AccountEmailSender>();
builder.Services.AddHostedService<EmailOutboxWorker>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(15)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var userId)
                || !Guid.TryParse(context.Principal?.FindFirst("security_version")?.Value, out var version)
                || !AppRoles.IsValid(context.Principal?.FindFirst("role")?.Value))
            {
                context.Fail("Invalid session.");
                return;
            }
            var source = context.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using var command = source.CreateCommand("""
                SELECT EXISTS (SELECT 1 FROM users
                    WHERE id = $1 AND security_version = $2 AND email_verified AND role = $3)
                """);
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(version);
            command.Parameters.AddWithValue(context.Principal!.FindFirst("role")!.Value);
            if (await command.ExecuteScalarAsync(context.HttpContext.RequestAborted) is not true)
                context.Fail("Invalid session.");
        }
    };
});
builder.Services.AddAuthorization(options => options.FallbackPolicy =
    new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();
var lifecycleLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Game_Store_API.Application");
app.Lifetime.ApplicationStarted.Register(() => lifecycleLogger.LogInformation("{Event}", "ApplicationStarted"));
app.Lifetime.ApplicationStopping.Register(() => lifecycleLogger.LogInformation("{Event}", "ApplicationStopping"));
app.UseRouting();
app.UseMiddleware<RequestLoggingMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Game Store API v1");
    });
}

app.UseHttpsRedirection();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health/db").AllowAnonymous();
app.Run();
