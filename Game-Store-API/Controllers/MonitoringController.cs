using System.ComponentModel.DataAnnotations;
using Game_Store_API.Auth;
using Game_Store_API.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Game_Store_API.Controllers;

[ApiController]
[Authorize(Roles = AppRoles.Admin)]
[Route("api/monitoring")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MonitoringController(NpgsqlDataSource source, LoggingHealth health) : ControllerBase
{
    [HttpGet("events")]
    public async Task<ActionResult<List<AuditEntry>>> Events([Range(1, 100000)] int page = 1,
        [Range(1, 100)] int pageSize = 50, [Range(1, 168)] int hours = 24,
        [MaxLength(80)] string? eventName = null, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand("""
            SELECT id, occurred_at, event, severity, actor_id, target_id, host(remote_ip), trace_id, method, route, status_code, elapsed_ms
            FROM audit_events WHERE occurred_at >= now() - make_interval(hours => $1) AND ($2 = '' OR event = $2)
            ORDER BY occurred_at DESC, id DESC LIMIT $3 OFFSET $4
            """);
        command.Parameters.AddWithValue(hours);
        command.Parameters.AddWithValue(eventName ?? "");
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new AuditEntry(reader.GetInt64(0), reader.GetDateTime(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt32(10), reader.GetDouble(11)));
        return Ok(entries);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("""
            SELECT event, coalesce(host(remote_ip), 'unknown'), count(*) FROM audit_events
            WHERE occurred_at >= now() - interval '15 minutes' GROUP BY event, remote_ip
            """);
        var counts = new Dictionary<string, long>();
        var alerts = new List<object>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0); var ip = reader.GetString(1); var count = reader.GetInt64(2);
                counts[name] = counts.GetValueOrDefault(name) + count;
                var threshold = name switch
                {
                    "LoginFailed" or "LoginBlocked" or "AuthenticationRejected" or "AccessDenied" => 5,
                    "AccountLocked" or "RateLimited" or "ServerError" => 1,
                    _ => int.MaxValue
                };
                if (count >= threshold) alerts.Add(new { eventName = name, remoteIp = ip, count, threshold });
            }
        }
        await using var mail = source.CreateCommand("SELECT count(*) FROM email_outbox WHERE attempts >= 5 OR expires_at <= now()");
        var failedEmails = (long)(await mail.ExecuteScalarAsync(cancellationToken))!;
        var loggingHealth = await health.CheckHealthAsync(new HealthCheckContext(), cancellationToken);
        return Ok(new { windowMinutes = 15, counts, alerts, failedEmails, loggingHealthy = loggingHealth.Status == HealthStatus.Healthy });
    }
}
