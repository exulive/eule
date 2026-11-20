using Eule.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server.Auth;

public sealed record LoginRequest(string Email, string Password, string? Device);
public sealed record RefreshRequest(string RefreshToken);
public sealed record UserDto(Guid Id, string Email, string DisplayName);
public sealed record TokenResponse(
    string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, UserDto User);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, EuleDbContext db, TokenService tokens, PasswordHasher<User> hasher,
            TimeProvider time, ILogger<LoginRequest> log) =>
        {
            string email = (req.Email ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            // Same answer for "no such user" and "wrong password", so accounts can't be discovered.
            if (user == null || !user.IsActive || string.IsNullOrEmpty(req.Password))
            {
                log.LogInformation("Başarısız giriş: {Email}", email);
                return Results.Problem(statusCode: 401, title: "invalid_credentials");
            }
            var check = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (check == PasswordVerificationResult.Failed)
            {
                log.LogInformation("Başarısız giriş: {Email}", email);
                return Results.Problem(statusCode: 401, title: "invalid_credentials");
            }
            if (check == PasswordVerificationResult.SuccessRehashNeeded)
                user.PasswordHash = hasher.HashPassword(user, req.Password);

            user.LastLoginAt = time.GetUtcNow().UtcDateTime;
            var response = await Issue(db, tokens, time, user, Trim(req.Device));
            log.LogInformation("Giriş: {Email}", email);
            return Results.Ok(response);
        }).RequireRateLimiting("login");

        g.MapPost("/refresh", async (RefreshRequest req, EuleDbContext db, TokenService tokens, TimeProvider time,
            ILogger<RefreshRequest> log) =>
        {
            string hash = TokenService.Hash(req.RefreshToken ?? "");
            var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            var now = time.GetUtcNow().UtcDateTime;
            if (stored == null) return Results.Problem(statusCode: 401, title: "invalid_token");

            if (stored.RevokedAt != null)
            {
                // A used token came back: someone may have copied it. End every session of this user.
                log.LogWarning("Yenileme anahtarı yeniden kullanıldı, tüm oturumlar kapatılıyor: {User}", stored.UserId);
                await db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
                return Results.Problem(statusCode: 401, title: "invalid_token");
            }
            var user = await db.Users.FindAsync(stored.UserId);
            if (stored.ExpiresAt <= now || user == null || !user.IsActive)
                return Results.Problem(statusCode: 401, title: "invalid_token");

            stored.RevokedAt = now;
            var response = await Issue(db, tokens, time, user, stored.Device, replaces: stored);
            return Results.Ok(response);
        }).RequireRateLimiting("login");

        g.MapPost("/logout", async (RefreshRequest req, EuleDbContext db, TimeProvider time) =>
        {
            string hash = TokenService.Hash(req.RefreshToken ?? "");
            var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored is { RevokedAt: null })
            {
                stored.RevokedAt = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        app.MapGet("/api/me", async (HttpContext http, EuleDbContext db) =>
        {
            var user = await db.Users.FindAsync(http.User.UserId());
            return user == null ? Results.NotFound() : Results.Ok(new UserDto(user.Id, user.Email, user.DisplayName));
        }).RequireAuthorization();
    }

    static async Task<TokenResponse> Issue(EuleDbContext db, TokenService tokens, TimeProvider time, User user,
        string? device, RefreshToken? replaces = null)
    {
        var (access, accessExp) = tokens.CreateAccessToken(user);
        var (refresh, hash) = TokenService.NewRefreshToken();
        var row = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Device = device,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            ExpiresAt = tokens.RefreshExpiry(),
        };
        db.RefreshTokens.Add(row);
        if (replaces != null) replaces.ReplacedById = row.Id;
        await db.SaveChangesAsync();
        return new TokenResponse(access, accessExp, refresh, row.ExpiresAt, new UserDto(user.Id, user.Email, user.DisplayName));
    }

    static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(200, s.Trim().Length)];
}
