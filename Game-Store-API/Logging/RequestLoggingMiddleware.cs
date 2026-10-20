using System.Diagnostics;

namespace Game_Store_API.Logging;

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AuditStore audit, LoggingHealth health)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        var timer = Stopwatch.StartNew();
        var status = 500;
        try { await next(context); status = context.Response.StatusCode; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            status = 499;
            if (!context.Response.HasStarted) context.Response.StatusCode = status;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request failure {TraceId} {Route}", context.TraceIdentifier, AuditStore.Route(context));
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { title = "The server could not complete the request.", status = 500, traceId = context.TraceIdentifier });
            }
            else context.Abort();
        }
        finally
        {
            var eventName = Event(context, status);
            var level = status >= 500 ? LogLevel.Error : status is 400 or 401 or 403 or 429 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "{Event} {Method} {Route} returned {StatusCode} in {ElapsedMs} ms; trace {TraceId}, actor {ActorId}, target {TargetId}, IP {RemoteIp}",
                eventName, AuditStore.Method(context), AuditStore.Route(context), status, timer.Elapsed.TotalMilliseconds,
                context.TraceIdentifier, AuditStore.Actor(context), AuditStore.Target(context), context.Connection.RemoteIpAddress);
            if (eventName != "RequestCompleted")
            {
                try
                {
                    await audit.WriteAsync(eventName, level.ToString(), context, status, timer.Elapsed.TotalMilliseconds);
                    health.AuditSucceeded();
                }
                catch (Exception exception)
                {
                    health.AuditFailed();
                    logger.LogCritical("Audit storage failed for {Event}, trace {TraceId} ({ErrorType}). Request may already be committed.",
                        eventName, context.TraceIdentifier, exception.GetType().Name);
                }
            }
        }
    }

    private static string Event(HttpContext context, int status)
    {
        if (status >= 500) return "ServerError";
        if (status == 429) return "RateLimited";
        if (status == 403) return "AccessDenied";
        var route = AuditStore.Route(context).TrimStart('/');
        if (route == "api/auth/login") return context.Items["AuditEvent"] as string ?? (status == 200 ? "LoginSucceeded" : "LoginFailed");
        if (status == 401) return "AuthenticationRejected";
        if (route.StartsWith("api/monitoring", StringComparison.Ordinal)) return "AuditViewed";
        if (AuditStore.Method(context) is "GET" or "HEAD" or "OPTIONS" || !route.StartsWith("api/", StringComparison.Ordinal)) return "RequestCompleted";
        var action = route switch
        {
            "api/auth/register" => "Registration",
            "api/auth/users/staff" => "StaffCreation",
            "api/auth/users/{userId:guid}/role" => "RoleChange",
            "api/auth/verify-email" => "EmailVerification",
            "api/auth/resend-verification" => "VerificationRequested",
            "api/auth/forgot-password" => "PasswordResetRequested",
            "api/auth/reset-password" => "PasswordReset",
            _ when route.StartsWith("api/products", StringComparison.Ordinal) => "ProductChange",
            _ when route.StartsWith("api/cart", StringComparison.Ordinal) => "CartChange",
            _ => "ApiWrite"
        };
        return action + (status < 400 ? "Succeeded" : "Failed");
    }
}
