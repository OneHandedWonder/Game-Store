using Npgsql;

namespace Game_Store_API.Logging;

public sealed class AuditRetentionWorker(NpgsqlDataSource source, ILogger<AuditRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var command = source.CreateCommand("DELETE FROM audit_events WHERE occurred_at < now() - interval '90 days'");
                await command.ExecuteNonQueryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Audit retention failed ({ErrorType}). Investigate database access.", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
