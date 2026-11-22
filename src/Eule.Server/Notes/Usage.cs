using Eule.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server.Notes;

/// <summary>How many dictations the user has made this calendar month (in their time zone) and may make.</summary>
public sealed record UsageDto(int Used, int Limit, DateTime ResetsAt);

public static class Usage
{
    public const string LimitTitle = "dictation_limit";
    public const int DefaultPerMonth = 100;

    public static int LimitFor(User user, IConfiguration config) =>
        user.DictationLimit ?? config.GetValue("Limits:DictationsPerMonth", DefaultPerMonth);

    /// <summary>Deleted dictations still count: deleting must not give the allowance back.</summary>
    public static async Task<UsageDto> ForAsync(EuleDbContext db, User user, IConfiguration config, TimeZoneInfo zone,
        DateTime utcNow, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var monthStartLocal = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(monthStartLocal, zone);
        int used = await db.Dictations.CountAsync(d => d.UserId == user.Id && d.ReceivedAt >= fromUtc, ct);
        return new UsageDto(used, LimitFor(user, config), monthStartLocal.AddMonths(1));
    }
}
