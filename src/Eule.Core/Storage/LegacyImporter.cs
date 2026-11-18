using System.Globalization;
using Eule.Core.Settings;

namespace Eule.Core.Storage;

/// <summary>One-time import of settings and statistics from the "Oturma Sayacı" prototype.</summary>
public static class LegacyImporter
{
    const string MetaKey = "legacy_imported";

    public sealed record Result(bool SettingsImported, int DaysImported);

    public static Result Run(AppPaths paths, SettingsStore settingsStore, EuleDatabase db)
    {
        if (db.GetMeta(MetaKey) != null || !Directory.Exists(paths.LegacyDir))
            return new Result(false, 0);

        bool settingsImported = false;
        string ini = Path.Combine(paths.LegacyDir, "ayarlar.ini");
        if (!settingsStore.Exists && File.Exists(ini))
        {
            settingsStore.Save(ParseSettings(File.ReadAllLines(ini)));
            settingsImported = true;
        }

        int days = 0;
        string csv = Path.Combine(paths.LegacyDir, "istatistik.csv");
        if (File.Exists(csv))
        {
            foreach (var day in ParseStats(File.ReadAllLines(csv)))
            {
                db.AddDaily(day);
                days++;
            }
        }

        db.SetMeta(MetaKey, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        return new Result(settingsImported, days);
    }

    public static EuleSettings ParseSettings(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int i = line.IndexOf('=');
            if (i > 0) map[line[..i].Trim()] = line[(i + 1)..].Trim();
        }

        int Int(string k, int def) => map.TryGetValue(k, out var v) && int.TryParse(v, out int r) ? r : def;
        bool Bool(string k, bool def) => map.TryGetValue(k, out var v) ? v is "1" or "true" or "True" : def;

        var s = new EuleSettings
        {
            WorkMinutes = Int("WorkMin", 30),
            BreakMinutes = Int("BreakMin", 5),
            SnoozeMinutes = Int("SnoozeMin", 5),
            WarnSeconds = Int("WarnSec", 60),
            Sound = Bool("Sound", true),
            IdleCountsAsBreak = Bool("IdleReset", true),
            AutoRestart = Bool("AutoRestart", true),
            StartWithWindows = Bool("StartWithWindows", false),
            ShowWidget = Bool("ShowMini", true),
            // The prototype always opened the break window at once, with unlimited snoozes.
            Level = ReminderLevel.Firm,
            MaxSnoozes = 10,
        };
        return s.Normalize();
    }

    /// <summary>Lines of <c>yyyy-MM-dd;sit;breaks;skipped;snoozed</c>.</summary>
    public static IEnumerable<DailyStats> ParseStats(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string[] p = line.Split(';');
            if (p.Length < 5) continue;
            if (!DateOnly.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            int N(int i) => int.TryParse(p[i], out int v) ? Math.Max(0, v) : 0;
            yield return new DailyStats(date, N(1), N(2), 0, N(3), N(4), 0, 0);
        }
    }
}
