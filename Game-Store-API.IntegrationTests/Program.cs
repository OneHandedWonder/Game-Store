using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game_Store_API.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var directory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "../../../../Game-Store-API"));
var adminString = Environment.GetEnvironmentVariable("AUTH_TEST_ADMIN_CONNECTION") ?? "Host=localhost;Database=game_store;Username=game_store;Password=game_store_dev";
await using var admin = NpgsqlDataSource.Create(adminString);
var database = "auth_test_" + Guid.NewGuid().ToString("N");
await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
try { await RunAsync(); }
finally { NpgsqlConnection.ClearAllPools(); await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)"); await drop.ExecuteNonQueryAsync(); }
async Task RunAsync()
{
    await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(adminString) { Database = database }.ConnectionString);
    async Task Execute(string sql) { await using var cmd = source.CreateCommand(sql); await cmd.ExecuteNonQueryAsync(); }
    foreach (var file in new[] { "001_users.sql", "002_account_recovery.sql", "003_roles.sql", "004_products.sql", "005_product_versions.sql", "006_cart.sql", "007_audit_events.sql" }) await Execute(await File.ReadAllTextAsync(Path.Combine(directory, "Data", file)));
    var protection = new EphemeralDataProtectionProvider();
    var protector = protection.CreateProtector("GameStore.AccountEmail.v1");
    var jwt = new JwtSettings { Issuer = "Tests", Audience = "Tests", SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
    var hasher = new PasswordHasher<string>();
    var auth = new AuthService(source, hasher, jwt);
    var recovery = new AccountRecoveryService(source, hasher, protection);
    const string email = "ACCOUNT@EXAMPLE.COM", password = "Original password!", replacement = "Replacement password!";
    var ct = CancellationToken.None;
    async Task<string> Code(string purpose)
    {
        await using var cmd = source.CreateCommand("SELECT protected_message FROM email_outbox WHERE purpose = $1"); cmd.Parameters.AddWithValue(purpose);
        var payload = (string)(await cmd.ExecuteScalarAsync())!;
        Check(!payload.Contains(email), "encrypted payload");
        return Regex.Match(JsonSerializer.Deserialize<AccountEmail>(protector.Unprotect(payload))!.Body, "[A-F0-9]{64}").Value;
    }
    await recovery.RegisterAsync(email, password, ct);
    Check(await auth.LoginAsync(email, password, ct) is null, "unverified login blocked");
    var original = await Code("verify");
    await recovery.RegisterAsync(email, "Attacker password!", ct);
    await recovery.RequestCodeAsync(email, false, ct);
    Check(await Code("verify") == original, "duplicate signup and cooldown");
    Check(!await recovery.RedeemAsync(email, original, replacement, ct), "purpose binding");
    Check(!await recovery.RedeemAsync("OTHER@EXAMPLE.COM", original, null, ct), "user binding");
    await Execute("UPDATE account_tokens SET expires_at = now() - interval '1 second'");
    Check(!await recovery.RedeemAsync(email, original, null, ct), "verification expiry");
    await Execute("UPDATE account_tokens SET issued_at = now() - interval '2 minutes'");
    await recovery.RequestCodeAsync(email, false, ct);
    var current = await Code("verify");
    Check(current != original && !await recovery.RedeemAsync(email, original, null, ct), "code rotation");
    Check(await recovery.RedeemAsync(email, current, null, ct), "verification");
    Check(!await recovery.RedeemAsync(email, current, null, ct), "verification single use");
    var oldToken = await auth.LoginAsync(email, password, ct); Check(oldToken is not null, "verified login");
    for (var attempt = 0; attempt < 5; attempt++)
        Check(await auth.LoginAsync(email, "Incorrect password!", ct) is null, "incorrect password rejected");
    Check(await auth.LoginAsync(email, password, ct) is null, "locked account rejects correct password");
    await Execute("UPDATE users SET locked_until = now() - interval '1 second'");
    Check(await auth.LoginAsync(email, password, ct) is not null, "login recovers after lockout expiry");
    await recovery.RequestCodeAsync("MISSING@EXAMPLE.COM", true, ct);
    await recovery.RequestCodeAsync(email, true, ct);
    var reset = await Code("reset");
    Check(!await recovery.RedeemAsync(email, reset, null, ct), "reset purpose binding");
    await Execute("UPDATE account_tokens SET expires_at = now() - interval '1 second'");
    Check(!await recovery.RedeemAsync(email, reset, replacement, ct), "reset expiry");
    await Execute("UPDATE account_tokens SET issued_at = now() - interval '2 minutes'");
    await recovery.RequestCodeAsync(email, true, ct); reset = await Code("reset");
    await Execute("UPDATE users SET failed_attempts = 4, locked_until = now() + interval '15 minutes'");
    var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => recovery.RedeemAsync(email, reset, replacement, ct)));
    Check(results.Count(value => value) == 1, "concurrent single use");
    Check(await auth.LoginAsync(email, password, ct) is null, "old password rejected");
    var newToken = await auth.LoginAsync(email, replacement, ct); Check(newToken is not null, "new password and unlock");
    string Version(TokenResponse token) => new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken).Claims.First(c => c.Type == "security_version").Value;
    Check(Version(oldToken!) != Version(newToken!), "session version rotated");
    await recovery.RequestCodeAsync(email, true, ct);
    using (var failingWorker = new EmailOutboxWorker(source, protection, new FailureSender(), NullLogger<EmailOutboxWorker>.Instance))
    {
        await failingWorker.StartAsync(ct);
        var retried = false;
        for (var i = 0; i < 100; i++)
        {
            await using var attempt = source.CreateCommand("SELECT attempts FROM email_outbox");
            retried = await attempt.ExecuteScalarAsync() is 1;
            if (retried) break;
            await Task.Delay(50);
        }
        await failingWorker.StopAsync(ct);
        Check(retried, "delivery failure retained and retry scheduled");
    }
    await Execute("UPDATE email_outbox SET next_attempt_at = now()");
    var sender = new CaptureSender();
    using var worker = new EmailOutboxWorker(source, protection, sender, NullLogger<EmailOutboxWorker>.Instance);
    await worker.StartAsync(ct);
    for (var i = 0; i < 100 && sender.Count == 0; i++) await Task.Delay(50);
    await worker.StopAsync(ct); Check(sender.Count == 1, "outbox delivery");
    await Execute("DELETE FROM email_outbox");
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { typeof(AuthService).Assembly.Location, "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(arg);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__GameStore"] = new NpgsqlConnectionStringBuilder(adminString) { Database = database }.ConnectionString;
    start.Environment["Jwt__SigningKey"] = jwt.SigningKey;
    start.Environment["Jwt__Issuer"] = jwt.Issuer;
    start.Environment["Jwt__Audience"] = jwt.Audience;
    start.Environment["Email__Password"] = "unused-test-secret";
    start.Environment["Email__Host"] = "127.0.0.1";
    var logDirectory = Path.Combine(Path.GetTempPath(), database + "_logs");
    start.Environment["ApplicationLogging__Directory"] = logDirectory;
    using var process = System.Diagnostics.Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    try
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        var ready = false;
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new Exception("API process exited during startup.");
            try { ready = (await client.GetAsync("/health/db")).IsSuccessStatusCode; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (ready) break;
            await Task.Delay(100);
        }
        Check(ready, "API startup");
        await TokenValidationChecks.RunAsync(client, jwt, newToken!.AccessToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", oldToken!.AccessToken);
        Check((await client.GetAsync("/api/auth/me")).StatusCode == System.Net.HttpStatusCode.Unauthorized, "old JWT revoked");
        client.DefaultRequestHeaders.Authorization = new("Bearer", newToken!.AccessToken);
        Check((await client.GetAsync("/api/auth/me")).IsSuccessStatusCode, "new JWT accepted");
        var claims = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(newToken.AccessToken).Claims.ToArray();
        Check(claims.Single(c => c.Type == "role").Value == AppRoles.Default, "signup receives default role");
        var userId = Guid.Parse(claims.Single(c => c.Type == "sub").Value);
        async Task<HttpResponseMessage> SetRole(Guid id, string role)
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { role }), System.Text.Encoding.UTF8, "application/json");
            return await client.PutAsync($"/api/auth/users/{id}/role", body);
        }
        async Task<HttpResponseMessage> AddStaff(string address = "STAFF@EXAMPLE.COM", string initialPassword = "Initial staff password!")
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { email = address, password = initialPassword, role = "admin" }), System.Text.Encoding.UTF8, "application/json");
            return await client.PostAsync("/api/auth/users/staff", body);
        }
        Check((await AddStaff()).StatusCode == System.Net.HttpStatusCode.Forbidden, "default cannot create staff");
        Check((await SetRole(userId, "admin")).StatusCode == System.Net.HttpStatusCode.Forbidden, "default cannot self-promote");
        client.DefaultRequestHeaders.Authorization = null;
        Check((await SetRole(userId, "admin")).StatusCode == System.Net.HttpStatusCode.Unauthorized, "anonymous role changes denied");
        Check((await AddStaff()).StatusCode == System.Net.HttpStatusCode.Unauthorized, "anonymous staff creation denied");
        const string adminEmail = "ADMIN@EXAMPLE.COM";
        await using (var seed = source.CreateCommand("INSERT INTO users (id, email, password_hash, email_verified, role) VALUES ($1, $2, $3, true, 'admin')"))
        {
            seed.Parameters.AddWithValue(Guid.NewGuid()); seed.Parameters.AddWithValue(adminEmail);
            seed.Parameters.AddWithValue(hasher.HashPassword(adminEmail, replacement));
            await seed.ExecuteNonQueryAsync();
        }
        var adminToken = await auth.LoginAsync(adminEmail, replacement, ct);
        const string catalogEmployeeEmail = "CATALOG-EMPLOYEE@EXAMPLE.COM";
        await using (var seed = source.CreateCommand("INSERT INTO users (id, email, password_hash, email_verified, role) VALUES ($1, $2, $3, true, 'employee')"))
        {
            seed.Parameters.AddWithValue(Guid.NewGuid()); seed.Parameters.AddWithValue(catalogEmployeeEmail);
            seed.Parameters.AddWithValue(hasher.HashPassword(catalogEmployeeEmail, replacement));
            await seed.ExecuteNonQueryAsync();
        }
        var catalogEmployeeToken = await auth.LoginAsync(catalogEmployeeEmail, replacement, ct);
        await CatalogChecks.RunAsync(client, newToken.AccessToken, catalogEmployeeToken!.AccessToken, adminToken!.AccessToken);
        await CartChecks.RunAsync(client, newToken.AccessToken, adminToken!.AccessToken);
        await LoggingChecks.RunAsync(client, source, newToken.AccessToken, catalogEmployeeToken!.AccessToken, adminToken.AccessToken, logDirectory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken!.AccessToken);
        Check((await AddStaff(initialPassword: "short!")).StatusCode == System.Net.HttpStatusCode.BadRequest, "staff password policy");
        Check((await AddStaff(address: "not-an-email")).StatusCode == System.Net.HttpStatusCode.BadRequest, "staff email validation");
        using (var added = await AddStaff())
        {
            Check(added.StatusCode == System.Net.HttpStatusCode.Created, "admin creates staff");
            using var created = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
            Check(created.RootElement.GetProperty("role").GetString() == "employee", "staff cannot request admin role");
        }
        Check((await AddStaff(address: "staff@example.com")).StatusCode == System.Net.HttpStatusCode.Conflict, "duplicate staff case normalization");
        await using (var staff = source.CreateCommand("SELECT role, email_verified, password_hash, EXISTS (SELECT 1 FROM account_tokens t WHERE t.user_id = users.id AND purpose = 'verify'), EXISTS (SELECT 1 FROM email_outbox o WHERE o.user_id = users.id AND purpose = 'verify') FROM users WHERE email = 'STAFF@EXAMPLE.COM'"))
        await using (var reader = await staff.ExecuteReaderAsync())
        {
            Check(await reader.ReadAsync() && reader.GetString(0) == "employee" && !reader.GetBoolean(1), "staff role and unverified state");
            Check(hasher.VerifyHashedPassword("STAFF@EXAMPLE.COM", reader.GetString(2), "Initial staff password!") != PasswordVerificationResult.Failed, "staff password hashed");
            Check(reader.GetBoolean(3) && reader.GetBoolean(4), "staff verification code queued");
        }
        Check(await auth.LoginAsync("STAFF@EXAMPLE.COM", "Initial staff password!", ct) is null, "staff must verify email");
        Check((await SetRole(userId, "superadmin")).StatusCode == System.Net.HttpStatusCode.BadRequest, "unknown role rejected");
        Check((await SetRole(Guid.NewGuid(), "employee")).StatusCode == System.Net.HttpStatusCode.NotFound, "unknown user rejected");
        Check((await SetRole(userId, "employee")).StatusCode == System.Net.HttpStatusCode.NoContent, "admin assigns employee");
        client.DefaultRequestHeaders.Authorization = new("Bearer", newToken.AccessToken);
        Check((await client.GetAsync("/api/auth/me")).StatusCode == System.Net.HttpStatusCode.Unauthorized, "role change revokes old token");
        var employeeToken = await auth.LoginAsync(email, replacement, ct);
        client.DefaultRequestHeaders.Authorization = new("Bearer", employeeToken!.AccessToken);
        Check((await AddStaff()).StatusCode == System.Net.HttpStatusCode.Forbidden, "employee cannot create staff");
        Check((await SetRole(userId, "admin")).StatusCode == System.Net.HttpStatusCode.Forbidden, "employee cannot self-promote");
        using (var me = JsonDocument.Parse(await client.GetStringAsync("/api/auth/me")))
            Check(me.RootElement.GetProperty("role").GetString() == "employee", "employee role exposed");
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
        Check((await SetRole(userId, "admin")).StatusCode == System.Net.HttpStatusCode.NoContent, "admin assigns admin");
        var promoted = await auth.LoginAsync(email, replacement, ct);
        client.DefaultRequestHeaders.Authorization = new("Bearer", promoted!.AccessToken);
        Check((await SetRole(userId, "default")).StatusCode == System.Net.HttpStatusCode.NoContent, "promoted admin can assign default");
        Check((await client.GetAsync("/api/auth/me")).StatusCode == System.Net.HttpStatusCode.Unauthorized, "demotion revokes admin token");
        client.DefaultRequestHeaders.Authorization = null;
        foreach (var invalid in new[] { "short!", "NoSpecialCharactersHere" })
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { email, code = reset, newPassword = invalid }), System.Text.Encoding.UTF8, "application/json");
            Check((await client.PostAsync("/api/auth/reset-password", body)).StatusCode == System.Net.HttpStatusCode.BadRequest, "reset password policy");
        }
        foreach (var path in new[] { "forgot-password", "resend-verification" })
        {
            using var missingBody = new StringContent("{\"email\":\"missing@example.com\"}", System.Text.Encoding.UTF8, "application/json");
            using var missingResponse = await client.PostAsync("/api/auth/" + path, missingBody);
            Check(missingResponse.StatusCode == System.Net.HttpStatusCode.Accepted, "generic request response");
        }
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        foreach (var path in new[] { "verify-email", "resend-verification", "forgot-password", "reset-password" })
            Check(doc.RootElement.GetProperty("paths").TryGetProperty("/api/auth/" + path, out _), "Swagger recovery endpoint");
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); await Task.WhenAll(output, error);
    }
    Console.WriteLine("PASS: account lifecycle, mail retries, JWT revocation, HTTP validation, role defaults, admin-only role assignment, privilege escalation rejection and role-change token revocation. Isolated test database cleaned up on exit.");
}
static void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); }
sealed class CaptureSender : IAccountEmailSender
{
    public int Count;
    public Task SendAsync(AccountEmail message, CancellationToken cancellationToken) { Interlocked.Increment(ref Count); return Task.CompletedTask; }
}

sealed class FailureSender : IAccountEmailSender
{
    public Task SendAsync(AccountEmail message, CancellationToken cancellationToken) => throw new IOException("Simulated delivery failure.");
}
