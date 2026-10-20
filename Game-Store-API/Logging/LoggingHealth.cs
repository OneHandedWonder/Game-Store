using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Game_Store_API.Logging;

public sealed class LoggingHealth : IHealthCheck
{
    private int auditFailed;
    private int fileFailed;
    public void AuditSucceeded() => Interlocked.Exchange(ref auditFailed, 0);
    public void AuditFailed() => Interlocked.Exchange(ref auditFailed, 1);
    public void FileFailed() => Interlocked.Exchange(ref fileFailed, 1);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Volatile.Read(ref auditFailed) == 0 && Volatile.Read(ref fileFailed) == 0
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Logging storage failed; inspect server logs and storage permissions."));
}
