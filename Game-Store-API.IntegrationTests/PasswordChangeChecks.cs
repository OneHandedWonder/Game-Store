using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using Game_Store_API.Auth;
using Npgsql;

static class PasswordChangeChecks
{
    public static async Task RunAsync(HttpClient client, NpgsqlDataSource source, AuthService auth, AccountRecoveryService recovery, string email, string password)
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Password change: " + name); }
        var original = client.DefaultRequestHeaders.Authorization;
        const string next = "Changed secure password!";
        try
        {
            client.DefaultRequestHeaders.Authorization = null;
            Check((await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = password, newPassword = next })).StatusCode == HttpStatusCode.Unauthorized, "anonymous denied");
            var token = (await auth.LoginAsync(email, password, default))!;
            var second = (await auth.LoginAsync(email, password, default))!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
            foreach (var invalid in new[] { "short!", "NoSpecialCharactersHere", password })
                Check((await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = password, newPassword = invalid })).StatusCode == HttpStatusCode.BadRequest, "new password policy and reuse");
            for (var i = 0; i < 5; i++)
                Check((await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Wrong password!", newPassword = next })).StatusCode == HttpStatusCode.BadRequest, "wrong current password denied");
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
            Check(!await auth.ChangePasswordAsync(Guid.Parse(claims.Subject), Guid.Parse(claims.Claims.Single(c => c.Type == "security_version").Value), password, next, default),
                "locked account cannot change password");
            await using (var unlock = source.CreateCommand("UPDATE users SET locked_until = NULL, failed_attempts = 0 WHERE email = $1"))
            {
                unlock.Parameters.AddWithValue(email);
                await unlock.ExecuteNonQueryAsync();
            }
            await recovery.RequestCodeAsync(email, true, default);
            Check((await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = password, newPassword = next })).IsSuccessStatusCode, "valid password changed");
            Check((await client.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "current session revoked");
            client.DefaultRequestHeaders.Authorization = new("Bearer", second.AccessToken);
            Check((await client.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "other session revoked");
            Check(await auth.LoginAsync(email, password, default) is null && await auth.LoginAsync(email, next, default) is not null, "only new password works");
            await using var pending = source.CreateCommand("SELECT count(*) FROM account_tokens t JOIN users u ON u.id = t.user_id WHERE u.email = $1 AND t.purpose = 'reset'");
            pending.Parameters.AddWithValue(email);
            Check((long)(await pending.ExecuteScalarAsync())! == 0, "pending reset invalidated");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; }
        Console.WriteLine("PASS: current password verification, password policy, lockout, pending reset invalidation, password replacement and all-session revocation.");
    }
}
