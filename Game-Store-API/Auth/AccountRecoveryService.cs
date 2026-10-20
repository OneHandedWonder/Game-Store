using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Game_Store_API.Auth;

public sealed class AccountRecoveryService(
    NpgsqlDataSource dataSource, IPasswordHasher<string> hasher, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("GameStore.AccountEmail.v1");

    public async Task RegisterAsync(string email, string password, CancellationToken cancellationToken)
        => await CreateUserAsync(email, password, AppRoles.Default, cancellationToken);

    public Task<Guid?> CreateStaffAsync(string email, string password, CancellationToken cancellationToken)
        => CreateUserAsync(email, password, AppRoles.Employee, cancellationToken);

    private async Task<Guid?> CreateUserAsync(string email, string password, string role, CancellationToken cancellationToken)
    {
        var hash = hasher.HashPassword(email, password);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO users (id, email, password_hash, role) VALUES ($1, $2, $3, $4)
            ON CONFLICT (email) DO NOTHING RETURNING id
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(hash);
        command.Parameters.AddWithValue(role);
        var id = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        if (id.HasValue)
            await QueueCodeAsync(connection, transaction, id.Value, email, "verify", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task RequestCodeAsync(string email, bool reset, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM users WHERE email = $1 AND email_verified = $2 FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(reset);
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid id)
            await QueueCodeAsync(connection, transaction, id, email, reset ? "reset" : "verify", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task QueueCodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, string email, string purpose, CancellationToken cancellationToken)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = DateTime.UtcNow.AddMinutes(purpose == "verify" ? 1440 : 30);
        await using var token = new NpgsqlCommand("""
            INSERT INTO account_tokens (user_id, purpose, token_hash, expires_at)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (user_id, purpose) DO UPDATE
            SET token_hash = EXCLUDED.token_hash, expires_at = EXCLUDED.expires_at, issued_at = now()
            WHERE account_tokens.issued_at < now() - interval '1 minute'
            RETURNING user_id
            """, connection, transaction);
        token.Parameters.AddWithValue(id);
        token.Parameters.AddWithValue(purpose);
        token.Parameters.AddWithValue(HashCode(code));
        token.Parameters.AddWithValue(expires);
        if (await token.ExecuteScalarAsync(cancellationToken) is null) return;

        var subject = purpose == "verify" ? "Verify your Game Store email" : "Reset your Game Store password";
        var instruction = purpose == "verify"
            ? "Enter this code to verify the email for the account you created. It expires in 24 hours."
            : "Enter this code with your new password to reset your password. It expires in 30 minutes.";
        var message = new AccountEmail(email, subject,
            $"{instruction}\n\n{code}\n\nIf you did not request this, ignore this email. Do not share this code.");
        await using var queue = new NpgsqlCommand("""
            INSERT INTO email_outbox (id, user_id, purpose, protected_message, expires_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (user_id, purpose) DO UPDATE SET
                id = EXCLUDED.id, protected_message = EXCLUDED.protected_message,
                expires_at = EXCLUDED.expires_at, attempts = 0, next_attempt_at = now()
            """, connection, transaction);
        queue.Parameters.AddWithValue(Guid.NewGuid());
        queue.Parameters.AddWithValue(id);
        queue.Parameters.AddWithValue(purpose);
        queue.Parameters.AddWithValue(protector.Protect(JsonSerializer.Serialize(message)));
        queue.Parameters.AddWithValue(expires);
        await queue.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RedeemAsync(string email, string code, string? newPassword,
        CancellationToken cancellationToken)
    {
        var purpose = newPassword is null ? "verify" : "reset";
        var passwordHash = newPassword is null ? null : hasher.HashPassword(email, newPassword);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var user = new NpgsqlCommand("SELECT id FROM users WHERE email = $1 FOR UPDATE", connection, transaction);
        user.Parameters.AddWithValue(email);
        if (await user.ExecuteScalarAsync(cancellationToken) is not Guid id) return false;

        await using var redeem = new NpgsqlCommand("""
            DELETE FROM account_tokens
            WHERE user_id = $1 AND purpose = $2 AND token_hash = $3 AND expires_at > clock_timestamp()
            RETURNING user_id
            """, connection, transaction);
        redeem.Parameters.AddWithValue(id);
        redeem.Parameters.AddWithValue(purpose);
        redeem.Parameters.AddWithValue(HashCode(code));
        if (await redeem.ExecuteScalarAsync(cancellationToken) is null) return false;

        await using var update = new NpgsqlCommand(newPassword is null
            ? "UPDATE users SET email_verified = true WHERE id = $1"
            : """
                UPDATE users SET password_hash = $2, security_version = gen_random_uuid(),
                    failed_attempts = 0, locked_until = NULL WHERE id = $1 AND email_verified = true
                """, connection, transaction);
        update.Parameters.AddWithValue(id);
        if (passwordHash is not null) update.Parameters.AddWithValue(passwordHash);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await using var removeMail = new NpgsqlCommand(
            "DELETE FROM email_outbox WHERE user_id = $1 AND purpose = $2", connection, transaction);
        removeMail.Parameters.AddWithValue(id);
        removeMail.Parameters.AddWithValue(purpose);
        await removeMail.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}
