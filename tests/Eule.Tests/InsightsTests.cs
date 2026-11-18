using Eule.Core.Engine;
using Eule.Core.Insights;
using Eule.Core.Settings;
using Eule.Core.Storage;

namespace Eule.Tests;

public class StretchTests
{
    [Fact]
    public void Longest_stretch_ends_with_a_completed_break_not_with_a_skip()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);

        h.Run(30 * 60);          // break starts
        h.Engine.Skip();         // skipped: still sitting
        h.Run(30 * 60);          // break starts again
        Assert.Equal(60 * 60, rec.Today.LongestStretch);
        h.Run(5 * 60);           // completed break closes the stretch
        h.Run(10 * 60);
        Assert.Equal(60 * 60, rec.Today.LongestStretch);

        rec.Flush();
        Assert.Equal(60 * 60, db.GetDay(DateOnly.FromDateTime(h.Now)).LongestStretch);
    }

    [Fact]
    public void Time_away_is_not_part_of_the_stretch()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness();
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);

        h.Run(20 * 60);
        h.Run(5 * 60, idle: 0, idleGrows: true);
        Assert.InRange(rec.Today.LongestStretch, 20 * 60, 20 * 60 + 2);
    }

    [Fact]
    public void Running_stretch_is_saved_only_on_final_flush()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness();
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);
        h.Run(10 * 60);
        rec.Flush();
        var day = DateOnly.FromDateTime(h.Now);
        Assert.Equal(0, db.GetDay(day).LongestStretch);
        rec.Flush(final: true);
        Assert.Equal(10 * 60, db.GetDay(day).LongestStretch);
    }

    [Fact]
    public void Schema_v1_database_is_upgraded()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "old.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE daily(date TEXT PRIMARY KEY, sit_seconds INTEGER NOT NULL DEFAULT 0, breaks INTEGER NOT NULL DEFAULT 0,
                    away_breaks INTEGER NOT NULL DEFAULT 0, skipped INTEGER NOT NULL DEFAULT 0, snoozed INTEGER NOT NULL DEFAULT 0,
                    emergency_exits INTEGER NOT NULL DEFAULT 0, eye_breaks INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE hourly(date TEXT NOT NULL, hour INTEGER NOT NULL, sit_seconds INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(date, hour));
                CREATE TABLE events(id INTEGER PRIMARY KEY, ts TEXT NOT NULL, type TEXT NOT NULL, data TEXT);
                INSERT INTO daily(date, breaks) VALUES('2026-11-17', 3);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        using var db = new EuleDatabase(path);
        var d = db.GetDay(new DateOnly(2026, 11, 17));
        Assert.Equal(3, d.Breaks);
        Assert.Equal(0, d.LongestStretch);
        db.RecordStretch(d.Date, 900);
        Assert.Equal(900, db.GetDay(d.Date).LongestStretch);
    }
}

public class ReportTests
{
    static readonly DateOnly Today = new(2026, 11, 18); // Wednesday
    static readonly StreakInfo NoStreak = new(0, false, 0, 4);

    static DailyStats Day(DateOnly d, int sitMin, int breaks = 0, int skipped = 0, int longestMin = 0) =>
        DailyStats.Empty(d) with { SitSeconds = sitMin * 60, Breaks = breaks, Skipped = skipped, LongestStretch = longestMin * 60 };

    [Fact]
    public void Recent_has_fourteen_days_with_gaps_filled()
    {
        var r = Report.Build([Day(Today, 60), Day(Today.AddDays(-5), 120)], new Dictionary<(DateOnly, int), int>(), Today, 4, NoStreak);
        Assert.Equal(14, r.Recent.Count);
        Assert.Equal(Today.AddDays(-13), r.Recent[0].Date);
        Assert.Equal(Today, r.Recent[13].Date);
        Assert.Equal(120 * 60, r.Recent[8].SitSeconds);
        Assert.Equal(0, r.Recent[9].SitSeconds);
    }

    [Fact]
    public void Heatmap_averages_over_four_weeks_by_weekday()
    {
        var hourly = new Dictionary<(DateOnly, int), int>
        {
            [(Today, 14)] = 40 * 60,               // Wednesday 14:00
            [(Today.AddDays(-7), 14)] = 40 * 60,   // previous Wednesday
            [(Today.AddDays(-40), 14)] = 60 * 60,  // older than four weeks: ignored
            [(Today, EuleDatabase.UnknownHour)] = 999, // imported, hour unknown: ignored
        };
        var r = Report.Build([], hourly, Today, 4, NoStreak);
        Assert.Equal(20, r.Heatmap[2, 14], 3); // (40 + 40) / 4 weeks
        Assert.Equal(20, r.HeatmapMax, 3);
    }

    [Fact]
    public void Week_totals_start_on_monday()
    {
        var days = new[] { Day(Today, 60, 2), Day(Today.AddDays(-2), 60, 1), Day(Today.AddDays(-3), 600, 9) }; // Mon, Wed in; Sun out
        var r = Report.Build(days, new Dictionary<(DateOnly, int), int>(), Today, 4, NoStreak);
        Assert.Equal(120 * 60, r.ThisWeekSit);
        Assert.Equal(3, r.ThisWeekBreaks);
        Assert.Equal(600 * 60, r.LastWeekSit);
    }

    [Fact]
    public void Insights_name_the_peak_trend_stretch_and_break_rate()
    {
        var days = new List<DailyStats>
        {
            Day(Today, 300, breaks: 4, skipped: 1, longestMin: 75),
            Day(Today.AddDays(-1), 300, breaks: 3),
            Day(Today.AddDays(-2), 300, breaks: 1),
            Day(Today.AddDays(-7), 400), Day(Today.AddDays(-8), 400),
        };
        var hourly = new Dictionary<(DateOnly, int), int>();
        foreach (var w in new[] { 0, 7, 14, 21 })
            for (int h = 13; h < 17; h++)
                hourly[(Today.AddDays(-1 - w), h)] = 50 * 60; // Tuesdays, 13–17

        var r = Report.Build(days, hourly, Today, 4, NoStreak);
        Assert.Contains("En çok Salı öğleden sonraları oturuyorsun.", r.Insights);
        Assert.Contains(r.Insights, s => s.StartsWith("Bu haftaki en uzun kesintisiz oturuşun 1 sa 15 dk (Çarşamba)"));
        Assert.Contains("Geçen haftaya göre günde ortalama 1 sa 40 dk daha az oturdun.", r.Insights);
        Assert.Contains("Bu hafta mola verme oranın %89 (8 / 9).", r.Insights);
    }

    [Fact]
    public void No_insights_without_enough_data()
    {
        var r = Report.Build([Day(Today, 20)], new Dictionary<(DateOnly, int), int>(), Today, 4, NoStreak);
        Assert.Empty(r.Insights);
    }

    [Fact]
    public void Csv_has_header_bom_and_minutes()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.csv");
        CsvExport.Write(path, [Day(Today, 95, breaks: 3, longestMin: 40), Day(Today.AddDays(-1), 10)]);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = File.ReadAllLines(path);
        Assert.Equal(CsvExport.Header, lines[0]);
        Assert.Equal("2026-11-17;10;0;0;0;0;0;0;0", lines[1]);
        Assert.Equal("2026-11-18;95;3;0;0;0;0;0;40", lines[2]);
    }

    [Fact]
    public void Duration_words()
    {
        Assert.Equal("45 dk", Words.Duration(45 * 60));
        Assert.Equal("2 sa", Words.Duration(120 * 60));
        Assert.Equal("1 sa 5 dk", Words.Duration(65 * 60));
    }
}
