using Eule.Core.Engine;

namespace Eule.Core.Storage;

/// <summary>
/// Turns engine events into statistics. Sitting seconds arrive every second, so they are buffered
/// and written by <see cref="Flush"/>; rare events (breaks, skips…) are written immediately.
/// Keeps an up-to-date <see cref="Today"/> for the UI without hitting the database.
/// </summary>
public sealed class StatsRecorder
{
    readonly EuleDatabase db;
    readonly Func<DateTime> now;
    readonly Dictionary<(DateOnly Date, int Hour), int> buffer = [];

    // Uninterrupted sitting: grows with every sitting second, ends with a completed break or time away.
    // Skips and snoozes do not end it - you kept sitting.
    int currentStretch, longestClosed;

    public StatsRecorder(EuleDatabase db, Func<DateTime> now)
    {
        this.db = db;
        this.now = now;
        Today = db.GetDay(DateOnly.FromDateTime(now()));
        longestClosed = Today.LongestStretch;
    }

    public DailyStats Today { get; private set; }

    public event Action? Changed;

    public void Attach(BreakEngine engine) => engine.Raised += Handle;

    public void Handle(EngineEvent e)
    {
        DateTime t = e is Sitting s ? s.At : now();
        var date = DateOnly.FromDateTime(t);
        RollDay(date);

        switch (e)
        {
            case Sitting sit:
                AddBuffer(date, t.Hour, sit.Seconds);
                currentStretch += sit.Seconds;
                Today = Today with
                {
                    SitSeconds = Today.SitSeconds + sit.Seconds,
                    LongestStretch = Math.Max(longestClosed, currentStretch),
                };
                break;

            case AwayCredited away:
                AddBuffer(date, t.Hour, -away.UncountSeconds);
                // The idle part was never real sitting: take it off before closing the stretch.
                currentStretch = Math.Max(0, currentStretch - away.UncountSeconds);
                CloseStretch(date);
                Today = Today with
                {
                    SitSeconds = Math.Max(0, Today.SitSeconds - away.UncountSeconds),
                    AwayBreaks = Today.AwayBreaks + 1,
                };
                Count(date, Counter.AwayBreaks);
                break;

            case BreakEnded { Reason: BreakEndReason.Completed }:
                CloseStretch(date);
                Today = Today with { Breaks = Today.Breaks + 1 };
                Count(date, Counter.Breaks);
                break;

            case BreakEnded { Reason: BreakEndReason.Skipped }:
                Today = Today with { Skipped = Today.Skipped + 1 };
                Count(date, Counter.Skipped);
                break;

            case BreakEnded { Reason: BreakEndReason.EmergencyExit }:
                CloseStretch(date);
                Today = Today with { EmergencyExits = Today.EmergencyExits + 1 };
                Count(date, Counter.EmergencyExits);
                break;

            case BreakSnoozed:
                Today = Today with { Snoozed = Today.Snoozed + 1 };
                Count(date, Counter.Snoozed);
                break;

            case EyeBreakDue:
                Today = Today with { EyeBreaks = Today.EyeBreaks + 1 };
                Count(date, Counter.EyeBreaks);
                break;
        }

        if (e is not (Sitting or PhaseChanged))
            db.LogEvent(t, e.GetType().Name, e.ToString());

        Changed?.Invoke();
    }

    /// <summary>
    /// Writes buffered sitting time. Call every ~30 seconds, and with <paramref name="final"/> on exit so the
    /// running stretch is kept too. (While running it is not written: it may still include idle time that an
    /// upcoming "away" event takes back.)
    /// </summary>
    public void Flush(bool final = false)
    {
        if (buffer.Count > 0)
        {
            db.AddSitting(buffer.ToList());
            buffer.Clear();
        }
        if (final) db.RecordStretch(Today.Date, currentStretch);
    }

    void CloseStretch(DateOnly date)
    {
        longestClosed = Math.Max(longestClosed, currentStretch);
        db.RecordStretch(date, currentStretch);
        currentStretch = 0;
        Today = Today with { LongestStretch = longestClosed };
    }

    void AddBuffer(DateOnly date, int hour, int seconds)
    {
        if (seconds == 0) return;
        buffer[(date, hour)] = buffer.GetValueOrDefault((date, hour)) + seconds;
    }

    void Count(DateOnly date, Counter c)
    {
        Flush(); // keep sitting time and counters consistent on disk
        db.Increment(date, c);
    }

    void RollDay(DateOnly date)
    {
        if (Today.Date == date) return;
        Flush();
        db.RecordStretch(Today.Date, currentStretch); // a stretch over midnight counts for the day it was in
        currentStretch = 0;
        Today = db.GetDay(date);
        longestClosed = Today.LongestStretch;
    }
}
