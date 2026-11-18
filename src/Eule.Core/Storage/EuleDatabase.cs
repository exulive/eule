using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Eule.Core.Storage;

public sealed record DailyStats(
    DateOnly Date, int SitSeconds, int Breaks, int AwayBreaks, int Skipped, int Snoozed, int EmergencyExits, int EyeBreaks,
    int LongestStretch = 0)
{
    public static DailyStats Empty(DateOnly date) => new(date, 0, 0, 0, 0, 0, 0, 0);
    public int AllBreaks => Breaks + AwayBreaks;
}

public enum Counter { Breaks, AwayBreaks, Skipped, Snoozed, EmergencyExits, EyeBreaks }

/// <summary>Local SQLite store: per-day counters, per-hour sitting time (for the heat map) and an event log.</summary>
public sealed class EuleDatabase : IDisposable
{
    const int SchemaVersion = 2;
    readonly SqliteConnection conn;

    public EuleDatabase(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        Migrate();
    }

    void Migrate()
    {
        long version = (long)Scalar("PRAGMA user_version")!;
        if (version >= SchemaVersion) return;
        if (version < 1) CreateV1();
        if (version < 2)
        {
            // v2: longest uninterrupted sitting of the day.
            Exec("""
                ALTER TABLE daily ADD COLUMN longest_stretch INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 2;
                """);
        }
    }

    void CreateV1()
    {
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta(
                key   TEXT PRIMARY KEY,
                value TEXT);
            CREATE TABLE IF NOT EXISTS daily(
                date            TEXT PRIMARY KEY,
                sit_seconds     INTEGER NOT NULL DEFAULT 0,
                breaks          INTEGER NOT NULL DEFAULT 0,
                away_breaks     INTEGER NOT NULL DEFAULT 0,
                skipped         INTEGER NOT NULL DEFAULT 0,
                snoozed         INTEGER NOT NULL DEFAULT 0,
                emergency_exits INTEGER NOT NULL DEFAULT 0,
                eye_breaks      INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS hourly(
                date        TEXT    NOT NULL,
                hour        INTEGER NOT NULL,
                sit_seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(date, hour));
            CREATE TABLE IF NOT EXISTS events(
                id   INTEGER PRIMARY KEY,
                ts   TEXT NOT NULL,
                type TEXT NOT NULL,
                data TEXT);
            CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
            PRAGMA user_version = 1;
            """);
    }

    static string Key(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string Column(Counter c) => c switch
    {
        Counter.Breaks => "breaks",
        Counter.AwayBreaks => "away_breaks",
        Counter.Skipped => "skipped",
        Counter.Snoozed => "snoozed",
        Counter.EmergencyExits => "emergency_exits",
        Counter.EyeBreaks => "eye_breaks",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Adds (or, with a negative value, removes) sitting seconds for many hours in one transaction. Never goes below zero.</summary>
    public void AddSitting(IEnumerable<KeyValuePair<(DateOnly Date, int Hour), int>> buckets)
    {
        using var tx = conn.BeginTransaction();
        var dates = new HashSet<DateOnly>();
        foreach (var ((date, hour), seconds) in buckets)
        {
            if (seconds == 0) continue;
            dates.Add(date);
            Exec("""
                INSERT INTO hourly(date, hour, sit_seconds) VALUES($d, $h, MAX(0, $s))
                ON CONFLICT(date, hour) DO UPDATE SET sit_seconds = MAX(0, sit_seconds + $s);
                """, tx, ("$d", Key(date)), ("$h", hour), ("$s", seconds));
        }
        foreach (var date in dates) SyncDailySitting(date, tx);
        tx.Commit();
    }

    /// <summary>The daily total is always the sum of its hourly buckets, so the two never disagree.</summary>
    void SyncDailySitting(DateOnly date, SqliteTransaction? tx)
    {
        Exec("""
            INSERT INTO daily(date) VALUES($d) ON CONFLICT(date) DO NOTHING;
            UPDATE daily SET sit_seconds = (SELECT COALESCE(SUM(sit_seconds), 0) FROM hourly WHERE date = $d)
            WHERE date = $d;
            """, tx, ("$d", Key(date)));
    }

    /// <summary>Hour bucket for imported time whose hour of day is unknown. Ignored by the heat map.</summary>
    public const int UnknownHour = -1;

    public void Increment(DateOnly date, Counter counter, int by = 1)
    {
        string col = Column(counter);
        Exec($"""
            INSERT INTO daily(date, {col}) VALUES($d, $n)
            ON CONFLICT(date) DO UPDATE SET {col} = {col} + $n;
            """, null, ("$d", Key(date)), ("$n", by));
    }

    /// <summary>Adds imported totals to a day (used by the legacy importer).</summary>
    public void AddDaily(DailyStats s)
    {
        using var tx = conn.BeginTransaction();
        Exec("""
            INSERT INTO daily(date, breaks, away_breaks, skipped, snoozed, emergency_exits, eye_breaks)
            VALUES($d, $b, $a, $sk, $sn, $e, $eye)
            ON CONFLICT(date) DO UPDATE SET
                breaks = breaks + $b, away_breaks = away_breaks + $a,
                skipped = skipped + $sk, snoozed = snoozed + $sn,
                emergency_exits = emergency_exits + $e, eye_breaks = eye_breaks + $eye;
            INSERT INTO hourly(date, hour, sit_seconds) VALUES($d, $h, MAX(0, $sit))
            ON CONFLICT(date, hour) DO UPDATE SET sit_seconds = sit_seconds + MAX(0, $sit);
            """, tx, ("$d", Key(s.Date)), ("$h", UnknownHour), ("$sit", s.SitSeconds), ("$b", s.Breaks),
            ("$a", s.AwayBreaks), ("$sk", s.Skipped), ("$sn", s.Snoozed), ("$e", s.EmergencyExits), ("$eye", s.EyeBreaks));
        SyncDailySitting(s.Date, tx);
        tx.Commit();
    }

    /// <summary>Raises the day's longest uninterrupted sitting to <paramref name="seconds"/> if it is longer.</summary>
    public void RecordStretch(DateOnly date, int seconds)
    {
        if (seconds <= 0) return;
        Exec("""
            INSERT INTO daily(date, longest_stretch) VALUES($d, $s)
            ON CONFLICT(date) DO UPDATE SET longest_stretch = MAX(longest_stretch, $s);
            """, null, ("$d", Key(date)), ("$s", seconds));
    }

    public void LogEvent(DateTime at, string type, string? data = null)
    {
        Exec("INSERT INTO events(ts, type, data) VALUES($ts, $t, $data)", null,
            ("$ts", at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)), ("$t", type), ("$data", data));
    }

    public DailyStats GetDay(DateOnly date) =>
        GetDays(date, date).FirstOrDefault() ?? DailyStats.Empty(date);

    /// <summary>Days between <paramref name="from"/> and <paramref name="to"/> (inclusive) that have data, oldest first.</summary>
    public List<DailyStats> GetDays(DateOnly from, DateOnly to)
    {
        using var cmd = Command("""
            SELECT date, sit_seconds, breaks, away_breaks, skipped, snoozed, emergency_exits, eye_breaks, longest_stretch
            FROM daily WHERE date BETWEEN $f AND $t ORDER BY date
            """, null, ("$f", Key(from)), ("$t", Key(to)));
        using var r = cmd.ExecuteReader();
        var list = new List<DailyStats>();
        while (r.Read())
            list.Add(new DailyStats(DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                r.GetInt32(8)));
        return list;
    }

    /// <summary>Sitting seconds per (date, hour) between two dates, inclusive.</summary>
    public Dictionary<(DateOnly Date, int Hour), int> GetHourly(DateOnly from, DateOnly to)
    {
        using var cmd = Command("SELECT date, hour, sit_seconds FROM hourly WHERE date BETWEEN $f AND $t", null,
            ("$f", Key(from)), ("$t", Key(to)));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(DateOnly, int), int>();
        while (r.Read())
            map[(DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetInt32(1))] = r.GetInt32(2);
        return map;
    }

    public string? GetMeta(string key) => Scalar("SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v", null,
            ("$k", key), ("$v", value));

    public void Dispose() => conn.Dispose();

    // ---- helpers

    SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, null, args);
        return cmd.ExecuteScalar();
    }
}
