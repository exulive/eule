namespace Eule.Core.Storage;

public enum DayResult
{
    /// <summary>Hardly used the computer (weekend, holiday): neither keeps nor breaks the streak.</summary>
    Rest,
    /// <summary>Reached the break goal without an emergency exit.</summary>
    Met,
    /// <summary>Sat at the computer but did not reach the goal.</summary>
    Missed,
}

public sealed record StreakInfo(int Days, bool TodayMet, int TodayBreaks, int Goal);

/// <summary>
/// Counts consecutive "good" days. Days with little sitting are rest days and are skipped,
/// so a weekend away does not end a streak. Today only adds to the streak once it is met;
/// an unfinished today never breaks it.
/// </summary>
public static class Streak
{
    /// <summary>Sitting less than this is a rest day.</summary>
    public const int RestDaySeconds = 30 * 60;
    const int LookBackDays = 400;

    public static DayResult Judge(DailyStats d, int goal)
    {
        if (d.SitSeconds < RestDaySeconds && d.AllBreaks == 0) return DayResult.Rest;
        return d.AllBreaks >= goal && d.EmergencyExits == 0 ? DayResult.Met : DayResult.Missed;
    }

    public static StreakInfo Compute(IEnumerable<DailyStats> days, DailyStats today, int goal)
    {
        var byDate = days.ToDictionary(d => d.Date);
        byDate[today.Date] = today;

        bool todayMet = Judge(today, goal) == DayResult.Met;
        int count = todayMet ? 1 : 0;

        for (int i = 1; i <= LookBackDays; i++)
        {
            var date = today.Date.AddDays(-i);
            if (!byDate.TryGetValue(date, out var d)) continue; // no data = rest day
            var r = Judge(d, goal);
            if (r == DayResult.Missed) break;
            if (r == DayResult.Met) count++;
        }
        return new StreakInfo(count, todayMet, today.AllBreaks, goal);
    }

    public static StreakInfo Compute(EuleDatabase db, DailyStats today, int goal) =>
        Compute(db.GetDays(today.Date.AddDays(-LookBackDays), today.Date.AddDays(-1)), today, goal);
}
