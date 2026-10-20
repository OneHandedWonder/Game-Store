using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Game_Store_API.Auth;

public sealed class AuthService
{
    private readonly NpgsqlDataSource dataSource;
    private readonly IPasswordHasher<string> hasher;
    private readonly JwtSettings jwt;
    private readonly string dummyHash;
    private readonly IHttpContextAccessor? http;

    public AuthService(NpgsqlDataSource dataSource, IPasswordHasher<string> hasher, JwtSettings jwt, IHttpContextAccessor? http = null)
    {
        this.dataSource = dataSource;
        this.hasher = hasher;
        this.jwt = jwt;
        this.http = http;
        dummyHash = hasher.HashPassword("", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public async Task<TokenResponse?> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT id, password_hash, locked_until > now(), email_verified, security_version, role
            FROM users WHERE email = $1 FOR UPDATE
            """, connection, transaction);
        query.Parameters.AddWithValue(email);
        Guid id;
        string hash;
        bool locked;
        bool verified;
        Guid securityVersion;
        string role;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                hasher.VerifyHashedPassword(email, dummyHash, password);
                return null;
            }
            id = reader.GetGuid(0);
            hash = reader.GetString(1);
            locked = !reader.IsDBNull(2) && reader.GetBoolean(2);
            verified = reader.GetBoolean(3);
            securityVersion = reader.GetGuid(4);
            role = reader.GetString(5);
        }
        if (http?.HttpContext is { } context) context.Items["AuditActorId"] = id;

        var result = hasher.VerifyHashedPassword(email, hash, password);
        if (locked || !verified)
        {
            if (locked && http?.HttpContext is { } blocked) blocked.Items["AuditEvent"] = "LoginBlocked";
            return null;
        }
        if (result == PasswordVerificationResult.Failed)
        {
            await using var failure = new NpgsqlCommand("""
                UPDATE users SET
                    failed_attempts = CASE WHEN failed_attempts + 1 >= 5 THEN 0 ELSE failed_attempts + 1 END,
                    locked_until = CASE WHEN failed_attempts + 1 >= 5 THEN now() + interval '15 minutes' ELSE NULL END
                WHERE id = $1 RETURNING locked_until IS NOT NULL
                """, connection, transaction);
            failure.Parameters.AddWithValue(id);
            var newlyLocked = await failure.ExecuteScalarAsync(cancellationToken) is true;
            await transaction.CommitAsync(cancellationToken);
            if (newlyLocked && http?.HttpContext is { } lockedContext) lockedContext.Items["AuditEvent"] = "AccountLocked";
            return null;
        }

        await using var success = new NpgsqlCommand("""
            UPDATE users SET failed_attempts = 0, locked_until = NULL, password_hash = $2 WHERE id = $1
            """, connection, transaction);
        success.Parameters.AddWithValue(id);
        success.Parameters.AddWithValue(result == PasswordVerificationResult.SuccessRehashNeeded
            ? hasher.HashPassword(email, password) : hash);
        await success.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(15);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, id.ToString()),
             new Claim("security_version", securityVersion.ToString()),
             new Claim("role", role),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            now, expires, new SigningCredentials(jwt.GetSigningKey(), SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expires);
    }

    public async Task<bool> SetRoleAsync(Guid userId, string role, CancellationToken cancellationToken)
    {
        if (!AppRoles.IsValid(role)) throw new ArgumentException("Invalid role.", nameof(role));
        await using var command = dataSource.CreateCommand("""
            UPDATE users SET
                security_version = CASE WHEN role <> $2 THEN gen_random_uuid() ELSE security_version END,
                role = $2
            WHERE id = $1
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(role);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}

public sealed record TokenResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc);
