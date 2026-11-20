using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Eule.Server.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Eule.Server.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "https://eule.adacreative.net";
    public string Audience { get; set; } = "eule-desktop";
    /// <summary>HMAC key, at least 32 characters. Comes from the Jwt__SigningKey environment variable in production.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 30;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));

    public void Validate()
    {
        if (SigningKey.Length < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey en az 32 karakter olmalı (üretimde Jwt__SigningKey ortam değişkeni).");
    }
}

public sealed class TokenService(JwtOptions options, TimeProvider time)
{
    readonly JsonWebTokenHandler handler = new();

    public (string Token, DateTime ExpiresAt) CreateAccessToken(User user)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.AccessMinutes);
        string token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("name", user.DisplayName),
            ]),
            SigningCredentials = new SigningCredentials(options.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>A new random refresh token and the hash that is stored for it.</summary>
    public static (string Token, string Hash) NewRefreshToken()
    {
        string token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public DateTime RefreshExpiry() => time.GetUtcNow().UtcDateTime.AddDays(options.RefreshDays);
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                   ?? throw new InvalidOperationException("sub claim missing"));
}
