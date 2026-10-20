using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Game_Store_API.Auth;
using Microsoft.IdentityModel.Tokens;

static class TokenValidationChecks
{
    public static async Task RunAsync(HttpClient client, JwtSettings settings, string validToken)
    {
        var original = client.DefaultRequestHeaders.Authorization;
        var handler = new JwtSecurityTokenHandler();
        var claims = handler.ReadJwtToken(validToken).Claims
            .Where(claim => claim.Type is not ("iss" or "aud" or "exp" or "nbf" or "iat")).ToArray();
        string Issue(string issuer, string audience, DateTime start, DateTime expiry, SecurityKey key) =>
            handler.WriteToken(new JwtSecurityToken(issuer, audience, claims, start, expiry,
                new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        var now = DateTime.UtcNow;
        try
        {
            var invalidTokens = new Dictionary<string, string>
            {
                ["malformed"] = "invalid-token",
                ["wrong issuer"] = Issue("OtherIssuer", settings.Audience, now, now.AddMinutes(15), settings.GetSigningKey()),
                ["wrong audience"] = Issue(settings.Issuer, "OtherAudience", now, now.AddMinutes(15), settings.GetSigningKey()),
                ["expired"] = Issue(settings.Issuer, settings.Audience, now.AddMinutes(-30), now.AddMinutes(-1), settings.GetSigningKey()),
                ["not yet valid"] = Issue(settings.Issuer, settings.Audience, now.AddMinutes(5), now.AddMinutes(20), settings.GetSigningKey()),
                ["wrong signing key"] = Issue(settings.Issuer, settings.Audience, now, now.AddMinutes(15),
                    new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
            };
            foreach (var (name, token) in invalidTokens)
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var response = await client.GetAsync("/api/auth/me");
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                    throw new Exception($"JWT: {name} token returned {(int)response.StatusCode}, expected 401.");
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", validToken);
            using var valid = await client.GetAsync("/api/auth/me");
            if (!valid.IsSuccessStatusCode) throw new Exception("JWT: valid token rejected.");
        }
        finally { client.DefaultRequestHeaders.Authorization = original; }
        Console.WriteLine("PASS: malformed, expired, future, wrong-issuer, wrong-audience and wrongly signed JWTs rejected; valid JWT accepted.");
    }
}
