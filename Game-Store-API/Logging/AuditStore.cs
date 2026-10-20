using Npgsql;
using NpgsqlTypes;

namespace Game_Store_API.Logging;

public sealed record AuditEntry(long Id, DateTime OccurredAt, string Event, string Severity,
    Guid? ActorId, Guid? TargetId, string? RemoteIp, string TraceId, string Method, string Route, int StatusCode, double ElapsedMs);

public sealed class AuditStore(NpgsqlDataSource source)
{
    public async Task WriteAsync(string eventName, string severity, HttpContext context, int status, double elapsed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var command = source.CreateCommand("""
            INSERT INTO audit_events (event, severity, actor_id, target_id, remote_ip, trace_id, method, route, status_code, elapsed_ms)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """);
        command.Parameters.AddWithValue(eventName);
        command.Parameters.AddWithValue(severity);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)Actor(context) ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)Target(context) ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Inet, (object?)context.Connection.RemoteIpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue(context.TraceIdentifier);
        command.Parameters.AddWithValue(Method(context));
        command.Parameters.AddWithValue(Route(context));
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(elapsed);
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    public static string Route(HttpContext context) => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "<unmatched>";
    public static string Method(HttpContext context) => context.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS"
        ? context.Request.Method : "OTHER";
    public static Guid? Actor(HttpContext context) => context.Items["AuditActorId"] is Guid id ? id
        : Guid.TryParse(context.User.FindFirst("sub")?.Value, out var actor) ? actor : null;
    public static Guid? Target(HttpContext context)
    {
        if (context.Items["AuditTargetId"] is Guid target) return target;
        foreach (var key in new[] { "userId", "productId", "id" })
            if (Guid.TryParse(context.Request.RouteValues.GetValueOrDefault(key)?.ToString(), out var id)) return id;
        return null;
    }
}
