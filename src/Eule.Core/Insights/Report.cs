using System.Globalization;
using System.Text;
using Eule.Core.Localization;
using Eule.Core.Storage;

namespace Eule.Core.Insights;

/// <summary>Turkish words and durations shared by the report and the UI.</summary>
public static class Words
{
    /// <summary>Monday-first index (0 = Monday … 6 = Sunday).</summary>
    public static int WeekdayIndex(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    public static string[] DayShort => L.T("days.short").Split(',');
    public static string[] DayName => L.T("days.long").Split(',');

    public static string Duration(int seconds)
    {
        int m = Math.Max(0, seconds) / 60;
        if (m < 60) return L.F("unit.min", m);
        return m % 60 == 0 ? L.F("unit.hour", m / 60) : L.F("unit.hourMin", m / 60, m % 60);
    }

    public static string Date(DateOnly d) => d.ToString("d MMM ddd", L.Culture);

    public static DateOnly WeekStart(DateOnly d) => d.AddDays(-WeekdayIndex(d));
}

public enum DayPart { Morning, Afternoon, Evening, Night }

/// <summary>Everything the statistics screen shows, computed from plain data so it can be tested.</summary>
public sealed class Report
{
    public const int HeatmapWeeks = 4;

    public required DateOnly Today { get; init; }
    /// <summary>Last 14 days including today, oldest first; missing days are filled with zeros.</summary>
    public required IReadOnlyList<DailyStats> Recent { get; init; }
    public required int Goal { get; init; }
    public required StreakInfo Streak { get; init; }
    /// <summary>Average sitting minutes per [weekday (Mon=0), hour] over the last four weeks.</summary>
    public required double[,] Heatmap { get; init; }
    public required int ThisWeekSit { get; init; }
    public required int ThisWeekBreaks { get; init; }
    public required int LastWeekSit { get; init; }
    public required IReadOnlyList<string> Insights { get; init; }

    public double HeatmapMax
    {
        get
        {
            double max = 0;
            foreach (double v in Heatmap) max = Math.Max(max, v);
            return max;
        }
    }

    /// <summary>Hours worth drawing: the span that has any data, at least 08–18.</summary>
    public (int From, int To) ActiveHours
    {
        get
        {
            int from = 8, to = 18;
            for (int h = 0; h < 24; h++)
                for (int d = 0; d < 7; d++)
                    if (Heatmap[d, h] > 0.5) { from = Math.Min(from, h); to = Math.Max(to, h); }
            return (from, to);
        }
    }

    public static Report Build(EuleDatabase db, DailyStats today, int goal)
    {
        var from = today.Date.AddDays(-(HeatmapWeeks * 7 + 13));
        var days = db.GetDays(from, today.Date.AddDays(-1));
        days.Add(today);
        var hourly = db.GetHourly(today.Date.AddDays(-(HeatmapWeeks * 7 - 1)), today.Date);
        return Build(days, hourly, today.Date, goal, Core.Storage.Streak.Compute(db, today, goal));
    }

    public static Report Build(IEnumerable<DailyStats> days, IReadOnlyDictionary<(DateOnly Date, int Hour), int> hourly,
        DateOnly today, int goal, StreakInfo streak)
    {
        var byDate = days.GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Last());
        DailyStats Get(DateOnly d) => byDate.TryGetValue(d, out var s) ? s : DailyStats.Empty(d);

        var recent = Enumerable.Range(0, 14).Select(i => Get(today.AddDays(i - 13))).ToList();

        // Heat map: average over the last four weeks, per weekday.
        var heat = new double[7, 24];
        var heatFrom = today.AddDays(-(HeatmapWeeks * 7 - 1));
        foreach (var ((date, hour), secs) in hourly)
            if (hour is >= 0 and < 24 && date >= heatFrom && date <= today)
                heat[Words.WeekdayIndex(date), hour] += secs / 60.0;
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++)
                heat[d, h] /= HeatmapWeeks;

        var weekStart = Words.WeekStart(today);
        var thisWeek = Enumerable.Range(0, Words.WeekdayIndex(today) + 1).Select(i => Get(weekStart.AddDays(i))).ToList();
        var lastWeek = Enumerable.Range(0, 7).Select(i => Get(weekStart.AddDays(i - 7))).ToList();

        return new Report
        {
            Today = today,
            Recent = recent,
            Goal = goal,
            Streak = streak,
            Heatmap = heat,
            ThisWeekSit = thisWeek.Sum(d => d.SitSeconds),
            ThisWeekBreaks = thisWeek.Sum(d => d.AllBreaks),
            LastWeekSit = lastWeek.Sum(d => d.SitSeconds),
            Insights = BuildInsights(heat, thisWeek, lastWeek, byDate.Values),
        };
    }

    // ------------------------------------------------------------------ insights

    static List<string> BuildInsights(double[,] heat, List<DailyStats> thisWeek, List<DailyStats> lastWeek,
        IEnumerable<DailyStats> all)
    {
        var list = new List<string>();
        static bool Active(DailyStats d) => d.SitSeconds >= Core.Storage.Streak.RestDaySeconds;

        // 1. When do you sit the most?
        if (all.Count(Active) >= 3)
        {
            var (day, part, minutes) = PeakBlock(heat);
            if (minutes >= 30)
                list.Add(L.F("insight.peak", Words.DayName[day], PartWord(part)));
        }

        // 2. Longest uninterrupted sitting this week.
        var longest = thisWeek.MaxBy(d => d.LongestStretch);
        if (longest is { LongestStretch: >= 20 * 60 })
            list.Add(L.F("insight.longest", Words.Duration(longest.LongestStretch), Words.DayName[Words.WeekdayIndex(longest.Date)]));

        // 3. Compared with last week, per active day.
        var a = thisWeek.Where(Active).ToList();
        var b = lastWeek.Where(Active).ToList();
        if (a.Count >= 2 && b.Count >= 2)
        {
            int avgNow = (int)a.Average(d => d.SitSeconds), avgThen = (int)b.Average(d => d.SitSeconds);
            int diff = avgNow - avgThen;
            if (Math.Abs(diff) >= 10 * 60)
                list.Add(L.F(diff < 0 ? "insight.less" : "insight.more", Words.Duration(Math.Abs(diff))));
            else
                list.Add(L.T("insight.same"));
        }

        // 4. How often you actually take the break.
        int taken = thisWeek.Sum(d => d.Breaks), missed = thisWeek.Sum(d => d.Skipped + d.EmergencyExits);
        if (taken + missed >= 3)
            list.Add(L.F("insight.rate", (int)Math.Round(100.0 * taken / (taken + missed)), taken, taken + missed));

        return list;
    }

    static (int Day, DayPart Part, double Minutes) PeakBlock(double[,] heat)
    {
        (int, DayPart, double) best = (0, DayPart.Morning, 0);
        for (int d = 0; d < 7; d++)
            foreach (DayPart p in Enum.GetValues<DayPart>())
            {
                double sum = Hours(p).Sum(h => heat[d, h]);
                if (sum > best.Item3) best = (d, p, sum);
            }
        return best;
    }

    public static IEnumerable<int> Hours(DayPart p) => p switch
    {
        DayPart.Morning => Enumerable.Range(6, 6),     // 06–12
        DayPart.Afternoon => Enumerable.Range(12, 5),  // 12–17
        DayPart.Evening => Enumerable.Range(17, 5),    // 17–22
        _ => [22, 23, 0, 1, 2, 3, 4, 5],
    };

    static string PartWord(DayPart p) => L.T(p switch
    {
        DayPart.Morning => "part.morning",
        DayPart.Afternoon => "part.afternoon",
        DayPart.Evening => "part.evening",
        _ => "part.night",
    });
}

/// <summary>Daily statistics as a semicolon CSV with a BOM, so Excel in Turkish locale opens it correctly.</summary>
public static class CsvExport
{
    public static string Header => L.T("csv.header");

    public static string Build(IEnumerable<DailyStats> days)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var d in days.OrderBy(d => d.Date))
            sb.AppendLine(string.Join(';',
                d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.SitSeconds / 60, d.Breaks, d.AwayBreaks, d.Skipped, d.Snoozed, d.EmergencyExits, d.EyeBreaks,
                d.LongestStretch / 60));
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<DailyStats> days) =>
        File.WriteAllText(path, Build(days), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static void Write(string path, EuleDatabase db) =>
        Write(path, db.GetDays(DateOnly.MinValue, DateOnly.MaxValue));
}
