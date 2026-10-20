using Microsoft.IdentityModel.Tokens;

namespace Game_Store_API.Auth;

public sealed class JwtSettings
{
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string SigningKey { get; init; } = "";

    public SymmetricSecurityKey GetSigningKey()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience are required.");
        if (string.IsNullOrWhiteSpace(SigningKey))
            throw new InvalidOperationException("Jwt:SigningKey must be configured through secrets or environment variables.");
        var bytes = Convert.FromBase64String(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 random bytes encoded as Base64.");
        return new SymmetricSecurityKey(bytes);
    }
}
