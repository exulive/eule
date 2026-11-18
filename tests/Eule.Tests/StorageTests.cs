using Eule.Core;
using Eule.Core.Engine;
using Eule.Core.Settings;
using Eule.Core.Storage;

namespace Eule.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eule-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_all_values()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var s = new EuleSettings { WorkMinutes = 60, BreakMinutes = 10, Level = ReminderLevel.Strict, Theme = AppTheme.Dark };
        s.WorkHours.Enabled = true;
        s.WorkHours.Start = new(8, 30);
        s.EyeBreak.Enabled = true;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(60, loaded.WorkMinutes);
        Assert.Equal(10, loaded.BreakMinutes);
        Assert.Equal(ReminderLevel.Strict, loaded.Level);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.WorkHours.Enabled);
        Assert.Equal(new TimeOnly(8, 30), loaded.WorkHours.Start);
        Assert.Equal(5, loaded.WorkHours.Days.Count);
        Assert.True(loaded.EyeBreak.Enabled);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(Path.Combine(dir.Path, "none.json")).Load();
        Assert.Equal(30, s.WorkMinutes);
        Assert.Equal(ReminderLevel.Balanced, s.Level);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ bozuk");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Equal(30, s.WorkMinutes);
        Assert.NotNull(store.LastLoadError);
        Assert.True(File.Exists(path + ".bozuk"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = SettingsStore.Deserialize("""{ "workMinutes": 0, "breakMinutes": 999, "language": "xx" }""");
        Assert.Equal(1, s.WorkMinutes);
        Assert.Equal(60, s.BreakMinutes);
        Assert.Equal("tr", s.Language);
    }
}

public class DatabaseTests
{
    static readonly DateOnly Day = new(2026, 11, 18);

    [Fact]
    public void Sitting_is_aggregated_per_hour_and_day_and_never_negative()
    {
        using var db = new EuleDatabase(":memory:");
        db.AddSitting([new((Day, 9), 600), new((Day, 10), 300)]);
        db.AddSitting([new((Day, 9), 60), new((Day, 10), -1000)]);

        var hourly = db.GetHourly(Day, Day);
        Assert.Equal(660, hourly[(Day, 9)]);
        Assert.Equal(0, hourly[(Day, 10)]);
        Assert.Equal(660, db.GetDay(Day).SitSeconds); // daily = sum of hourly buckets
    }

    [Fact]
    public void Counters_increment()
    {
        using var db = new EuleDatabase(":memory:");
        db.Increment(Day, Counter.Breaks);
        db.Increment(Day, Counter.Breaks);
        db.Increment(Day, Counter.Skipped);
        var d = db.GetDay(Day);
        Assert.Equal(2, d.Breaks);
        Assert.Equal(1, d.Skipped);
        Assert.Equal(DailyStats.Empty(Day.AddDays(1)), db.GetDay(Day.AddDays(1)));
    }

    [Fact]
    public void Data_survives_reopening()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.db");
        using (var db = new EuleDatabase(path)) db.Increment(Day, Counter.Snoozed, 3);
        using (var db = new EuleDatabase(path)) Assert.Equal(3, db.GetDay(Day).Snoozed);
    }
}

public class StatsRecorderTests
{
    [Fact]
    public void Engine_events_become_statistics()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);

        h.Run(30 * 60 + 5 * 60);   // one full cycle with a completed break
        h.Run(30 * 60);
        h.Engine.Skip();
        h.Run(30 * 60);
        h.Engine.Snooze();
        rec.Flush(final: true);

        var today = db.GetDay(DateOnly.FromDateTime(h.Now));
        Assert.Equal(90 * 60, today.SitSeconds);
        Assert.Equal(1, today.Breaks);
        Assert.Equal(1, today.Skipped);
        Assert.Equal(1, today.Snoozed);
        Assert.Equal(today, rec.Today);
    }

    [Fact]
    public void Away_time_is_taken_back_from_sitting()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness();
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);

        h.Run(10 * 60);
        h.Run(5 * 60, idle: 0, idleGrows: true);
        rec.Flush();

        var today = db.GetDay(DateOnly.FromDateTime(h.Now));
        Assert.Equal(1, today.AwayBreaks);
        Assert.InRange(today.SitSeconds, 10 * 60, 10 * 60 + 2);
    }

    [Fact]
    public void A_new_day_starts_from_zero()
    {
        using var db = new EuleDatabase(":memory:");
        var h = new Harness();
        h.Now = new DateTime(2026, 11, 18, 23, 59, 0);
        var rec = new StatsRecorder(db, () => h.Now);
        rec.Attach(h.Engine);
        h.Run(120);
        Assert.Equal(new DateOnly(2026, 11, 19), rec.Today.Date);
        Assert.Equal(61, rec.Today.SitSeconds); // ticks 00:00:00 … 00:01:00
        rec.Flush();
        Assert.Equal(59, db.GetDay(new DateOnly(2026, 11, 18)).SitSeconds); // ticks 23:59:01 … 23:59:59
        Assert.Equal(61, db.GetDay(new DateOnly(2026, 11, 19)).SitSeconds);
    }
}

public class LegacyImporterTests
{
    [Fact]
    public void Imports_prototype_settings_and_stats_once()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(Path.Combine(dir.Path, "Eule"), Path.Combine(dir.Path, "OturmaSayaci"));
        Directory.CreateDirectory(paths.LegacyDir);
        File.WriteAllLines(Path.Combine(paths.LegacyDir, "ayarlar.ini"),
            ["WorkMin=60", "BreakMin=10", "SnoozeMin=3", "Sound=0", "ShowMini=1"]);
        File.WriteAllLines(Path.Combine(paths.LegacyDir, "istatistik.csv"),
            ["2026-11-17;3600;2;1;0", "2026-11-18;1800;1;0;2", "bozuk satır"]);

        var store = new SettingsStore(paths.SettingsFile);
        using var db = new EuleDatabase(paths.DatabaseFile);

        var result = LegacyImporter.Run(paths, store, db);
        Assert.True(result.SettingsImported);
        Assert.Equal(2, result.DaysImported);

        var s = store.Load();
        Assert.Equal(60, s.WorkMinutes);
        Assert.Equal(10, s.BreakMinutes);
        Assert.Equal(3, s.SnoozeMinutes);
        Assert.False(s.Sound);
        Assert.Equal(ReminderLevel.Firm, s.Level);

        var d = db.GetDay(new DateOnly(2026, 11, 17));
        Assert.Equal(3600, d.SitSeconds);
        Assert.Equal(2, d.Breaks);
        Assert.Equal(1, d.Skipped);

        // Second run does nothing (no double counting).
        Assert.Equal(0, LegacyImporter.Run(paths, store, db).DaysImported);
        Assert.Equal(3600, db.GetDay(new DateOnly(2026, 11, 17)).SitSeconds);
    }

    [Fact]
    public void Existing_eule_settings_are_not_overwritten()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(Path.Combine(dir.Path, "Eule"), Path.Combine(dir.Path, "OturmaSayaci"));
        Directory.CreateDirectory(paths.LegacyDir);
        File.WriteAllLines(Path.Combine(paths.LegacyDir, "ayarlar.ini"), ["WorkMin=60"]);
        var store = new SettingsStore(paths.SettingsFile);
        store.Save(new EuleSettings { WorkMinutes = 45 });
        using var db = new EuleDatabase(paths.DatabaseFile);

        Assert.False(LegacyImporter.Run(paths, store, db).SettingsImported);
        Assert.Equal(45, store.Load().WorkMinutes);
    }
}
