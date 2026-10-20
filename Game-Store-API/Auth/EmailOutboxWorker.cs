using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Game_Store_API.Auth;

public sealed class EmailOutboxWorker(NpgsqlDataSource dataSource, IDataProtectionProvider protection,
    IAccountEmailSender sender, ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    private readonly IDataProtector protector = protection.CreateProtector("GameStore.AccountEmail.v1");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DeliverOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Email outbox processing failed ({ErrorType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<bool> DeliverOneAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT id, protected_message, attempts FROM email_outbox
            WHERE next_attempt_at <= now() AND attempts < 5 AND expires_at > now()
            ORDER BY next_attempt_at FOR UPDATE SKIP LOCKED LIMIT 1
            """, connection, transaction);
        Guid id;
        string payload;
        int attempts;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return false;
            id = reader.GetGuid(0);
            payload = reader.GetString(1);
            attempts = reader.GetInt32(2);
        }
        try
        {
            var message = JsonSerializer.Deserialize<AccountEmail>(protector.Unprotect(payload))
                ?? throw new InvalidOperationException("Invalid email payload.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await sender.SendAsync(message, deadline.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await using var retry = new NpgsqlCommand("""
                UPDATE email_outbox SET attempts = attempts + 1, next_attempt_at = now() + interval '1 minute'
                WHERE id = $1
                """, connection, transaction);
            retry.Parameters.AddWithValue(id);
            await retry.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogError("Email {MessageId} delivery failed ({ErrorType}); attempt {Attempt}/5. {Action}",
                id, exception.GetType().Name, attempts + 1,
                attempts + 1 == 5 ? "Automatic retries stopped; investigate SMTP and request a new code." : "Retry scheduled.");
            return true;
        }
        await using var delete = new NpgsqlCommand("DELETE FROM email_outbox WHERE id = $1", connection, transaction);
        delete.Parameters.AddWithValue(id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
