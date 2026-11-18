using Eule.Core.Engine;
using Eule.Core.Settings;

namespace Eule.Tests;

/// <summary>Drives a <see cref="BreakEngine"/> second by second and records what it raises.</summary>
sealed class Harness
{
    public readonly BreakEngine Engine;
    public readonly List<EngineEvent> Events = [];
    public DateTime Now = new(2026, 11, 18, 10, 0, 0); // a Wednesday

    public Harness(Action<EuleSettings>? configure = null)
    {
        var s = new EuleSettings { WorkMinutes = 30, BreakMinutes = 5, WarnSeconds = 60 };
        configure?.Invoke(s);
        Engine = new BreakEngine(s.Normalize());
        Engine.Raised += Events.Add;
    }

    /// <summary>Runs <paramref name="seconds"/> one-second ticks. <paramref name="idle"/> grows each second when <paramref name="idleGrows"/>.</summary>
    public void Run(int seconds, int idle = 0, BusyReason busy = BusyReason.None, bool idleGrows = false)
    {
        for (int i = 0; i < seconds; i++)
        {
            Now = Now.AddSeconds(1);
            Engine.Tick(new EnvironmentSnapshot(Now, idleGrows ? idle + i + 1 : idle, busy));
        }
    }

    public int Count<T>() where T : EngineEvent => Events.OfType<T>().Count();
    public T Last<T>() where T : EngineEvent => Events.OfType<T>().Last();
}

public class EngineTests
{
    [Fact]
    public void Firm_starts_break_exactly_when_sitting_time_is_up()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60 - 1);
        Assert.Equal(Phase.Working, h.Engine.Phase);
        h.Run(1);
        Assert.Equal(Phase.Break, h.Engine.Phase);
        Assert.False(h.Last<BreakStarted>().Strict);
        Assert.Equal(5 * 60, h.Engine.BreakRemaining);
    }

    [Fact]
    public void Warning_is_raised_once_before_the_break()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60);
        Assert.Equal(1, h.Count<WarningRaised>());
        Assert.Equal(60, h.Last<WarningRaised>().SecondsLeft);
    }

    [Fact]
    public void Completed_break_counts_and_restarts_work()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60 + 5 * 60);
        Assert.Equal(BreakEndReason.Completed, h.Last<BreakEnded>().Reason);
        Assert.Equal(Phase.Working, h.Engine.Phase);
        Assert.Equal(0, h.Engine.WorkElapsed);
        Assert.Equal(1, h.Count<WorkResumed>());
    }

    [Fact]
    public void Without_auto_restart_the_engine_waits_for_start_work()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Firm; s.AutoRestart = false; });
        h.Run(30 * 60 + 5 * 60 + 100);
        Assert.Equal(Phase.BreakFinished, h.Engine.Phase);
        h.Engine.StartWork();
        Assert.Equal(Phase.Working, h.Engine.Phase);
    }

    [Fact]
    public void Balanced_notifies_first_then_opens_break_after_grace()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Balanced; s.BalancedGraceSeconds = 60; });
        h.Run(30 * 60);
        Assert.Equal(Phase.BreakDue, h.Engine.Phase);
        Assert.Equal(2, h.Last<BreakDue>().SnoozesLeft);
        h.Run(59);
        Assert.Equal(Phase.BreakDue, h.Engine.Phase);
        h.Run(1);
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void Balanced_goes_straight_to_break_when_snoozes_run_out()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Balanced; s.MaxSnoozes = 1; s.SnoozeMinutes = 5; });
        h.Run(30 * 60);
        Assert.True(h.Engine.Snooze());
        Assert.Equal(Phase.Working, h.Engine.Phase);
        h.Run(5 * 60);
        Assert.Equal(Phase.Break, h.Engine.Phase);
        Assert.False(h.Engine.CanSnooze);
    }

    [Fact]
    public void Snooze_moves_the_target_by_snooze_minutes()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Firm; s.SnoozeMinutes = 5; });
        h.Run(30 * 60);
        Assert.True(h.Engine.Snooze());
        Assert.Equal(5 * 60, h.Engine.WorkLeft);
        Assert.Equal(BreakEndReason.Snoozed, h.Last<BreakEnded>().Reason);
        Assert.Equal(0, h.Count<WarningRaised>() - 1); // no second warning after a snooze
    }

    [Fact]
    public void Gentle_repeats_the_notification_and_never_forces()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Gentle; s.GentleRepeatSeconds = 300; });
        h.Run(30 * 60 + 900);
        Assert.Equal(Phase.BreakDue, h.Engine.Phase);
        Assert.Equal(4, h.Count<BreakDue>()); // first + 3 repeats
        Assert.Equal(-1, h.Last<BreakDue>().SnoozesLeft);
        Assert.True(h.Engine.CanSnooze);
    }

    [Fact]
    public void Strict_break_cannot_be_snoozed_or_skipped()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Strict);
        h.Run(30 * 60);
        Assert.Equal(Phase.Break, h.Engine.Phase);
        var started = h.Last<BreakStarted>();
        Assert.True(started.Strict);
        Assert.True(started.EmergencyExitAllowed);
        Assert.False(h.Engine.Snooze());
        Assert.False(h.Engine.Skip());
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void Strict_emergency_exit_ends_break_and_is_reported()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Strict);
        h.Run(30 * 60 + 10);
        Assert.True(h.Engine.EmergencyExit());
        Assert.Equal(BreakEndReason.EmergencyExit, h.Last<BreakEnded>().Reason);
        Assert.Equal(Phase.Working, h.Engine.Phase);
    }

    [Fact]
    public void Strict_without_emergency_exit_cannot_be_ended()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Strict; s.EmergencyExit = false; });
        h.Run(30 * 60);
        Assert.False(h.Last<BreakStarted>().EmergencyExitAllowed);
        Assert.False(h.Engine.EmergencyExit());
        h.Run(5 * 60);
        Assert.Equal(BreakEndReason.Completed, h.Last<BreakEnded>().Reason);
    }

    [Fact]
    public void Skip_counts_as_skipped_and_resets()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60);
        Assert.True(h.Engine.Skip());
        Assert.Equal(BreakEndReason.Skipped, h.Last<BreakEnded>().Reason);
        Assert.Equal(30 * 60, h.Engine.WorkLeft);
    }

    [Fact]
    public void Being_away_for_a_break_length_counts_as_a_break()
    {
        var h = new Harness();
        h.Run(10 * 60);
        h.Run(5 * 60, idle: 0, idleGrows: true); // no input for 5 minutes
        Assert.Equal(Phase.Away, h.Engine.Phase);
        var credited = h.Last<AwayCredited>();
        Assert.InRange(credited.UncountSeconds, 5 * 60 - 2, 5 * 60);
        Assert.Equal(0, h.Engine.WorkElapsed);

        h.Run(1, idle: 0);
        Assert.Equal(Phase.Working, h.Engine.Phase);
        Assert.Equal(1, h.Count<ReturnedFromAway>());
    }

    [Fact]
    public void Away_is_ignored_when_the_option_is_off()
    {
        var h = new Harness(s => s.IdleCountsAsBreak = false);
        h.Run(10 * 60, idle: 0, idleGrows: true);
        Assert.Equal(Phase.Working, h.Engine.Phase);
        Assert.Equal(0, h.Count<AwayCredited>());
    }

    [Fact]
    public void Break_is_deferred_while_busy_and_starts_when_free()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60, busy: BusyReason.Call);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
        Assert.Equal(BusyReason.Call, h.Last<BreakDeferred>().Reason);
        h.Run(600, busy: BusyReason.Call);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
        Assert.Equal(1, h.Count<BreakDeferred>());
        h.Run(1);
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void Strict_break_is_also_deferred_during_a_call()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Strict);
        h.Run(30 * 60, busy: BusyReason.Presentation);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
    }

    [Fact]
    public void Full_screen_alone_defers_at_most_thirty_minutes()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60, busy: BusyReason.FullScreen);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
        h.Run(BreakEngine.MaxFullScreenDeferSeconds - 1, busy: BusyReason.FullScreen);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
        h.Run(1, busy: BusyReason.FullScreen);
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void A_call_defers_without_limit()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60 + 2 * 3600, busy: BusyReason.Call | BusyReason.FullScreen);
        Assert.Equal(Phase.Deferred, h.Engine.Phase);
    }

    [Fact]
    public void Busy_is_ignored_when_defer_is_off()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Firm; s.DeferWhenBusy = false; });
        h.Run(30 * 60, busy: BusyReason.FullScreen);
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void Pause_stops_counting()
    {
        var h = new Harness();
        h.Run(60);
        h.Engine.Pause();
        h.Run(3600);
        Assert.Equal(Phase.Paused, h.Engine.Phase);
        Assert.Equal(60, h.Engine.WorkElapsed);
        h.Engine.Resume();
        h.Run(1);
        Assert.Equal(61, h.Engine.WorkElapsed);
    }

    [Fact]
    public void Sleep_gap_longer_than_a_break_resets_the_counter()
    {
        var h = new Harness();
        h.Run(20 * 60);
        h.Now = h.Now.AddHours(1);
        h.Engine.Tick(new EnvironmentSnapshot(h.Now, 3600), 3600);
        Assert.Equal(0, h.Engine.WorkElapsed);
        Assert.Equal(0, h.Last<AwayCredited>().UncountSeconds);
        Assert.Equal(Phase.Away, h.Engine.Phase);
    }

    [Fact]
    public void Gap_during_break_shortens_the_break()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(30 * 60);
        h.Engine.Tick(new EnvironmentSnapshot(h.Now.AddMinutes(10), 600), 600);
        Assert.Equal(BreakEndReason.Completed, h.Last<BreakEnded>().Reason);
    }

    [Fact]
    public void Outside_work_hours_nothing_is_counted()
    {
        var h = new Harness(s => { s.WorkHours.Enabled = true; s.WorkHours.Start = new(9, 0); s.WorkHours.End = new(10, 0); });
        h.Now = new DateTime(2026, 11, 18, 9, 59, 0);
        h.Run(60);
        Assert.Equal(Phase.OffHours, h.Engine.Phase);
        int sitting = h.Count<Sitting>();
        h.Run(600);
        Assert.Equal(sitting, h.Count<Sitting>());
    }

    [Fact]
    public void Work_hours_start_resets_the_counter()
    {
        var h = new Harness(s => { s.WorkHours.Enabled = true; s.WorkHours.Start = new(9, 0); s.WorkHours.End = new(18, 0); });
        h.Now = new DateTime(2026, 11, 18, 8, 58, 0);
        h.Run(10);
        Assert.Equal(Phase.OffHours, h.Engine.Phase);
        h.Run(120);
        Assert.Equal(Phase.Working, h.Engine.Phase);
        Assert.Equal(11, h.Engine.WorkElapsed); // ticks 09:00:00 … 09:00:10
    }

    [Fact]
    public void Eye_break_is_raised_on_its_interval()
    {
        var h = new Harness(s => { s.WorkMinutes = 60; s.EyeBreak.Enabled = true; s.EyeBreak.IntervalMinutes = 20; });
        h.Run(41 * 60);
        Assert.Equal(2, h.Count<EyeBreakDue>());
    }

    [Fact]
    public void Apply_settings_changes_the_running_target()
    {
        var h = new Harness();
        h.Run(10 * 60);
        h.Engine.ApplySettings(new EuleSettings { WorkMinutes = 60 });
        Assert.Equal(50 * 60, h.Engine.WorkLeft);
    }

    [Fact]
    public void Apply_settings_keeps_a_running_snooze()
    {
        var h = new Harness(s => { s.Level = ReminderLevel.Firm; s.SnoozeMinutes = 5; });
        h.Run(30 * 60);
        h.Engine.Snooze();
        h.Run(60);
        h.Engine.ApplySettings(new EuleSettings { Level = ReminderLevel.Firm, SnoozeMinutes = 5, Sound = false });
        Assert.Equal(4 * 60, h.Engine.WorkLeft);
    }

    [Fact]
    public void Shortening_work_below_elapsed_breaks_on_next_tick()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        h.Run(20 * 60);
        h.Engine.ApplySettings(new EuleSettings { WorkMinutes = 10, Level = ReminderLevel.Firm });
        h.Run(1);
        Assert.Equal(Phase.Break, h.Engine.Phase);
    }

    [Fact]
    public void Progress_runs_from_zero_to_one()
    {
        var h = new Harness(s => s.Level = ReminderLevel.Firm);
        Assert.Equal(0, h.Engine.Progress);
        h.Run(15 * 60);
        Assert.Equal(0.5, h.Engine.Progress, 3);
        h.Run(15 * 60);
        Assert.Equal(0, h.Engine.Progress, 3); // break just started
    }
}

public class WorkHoursTests
{
    [Theory]
    [InlineData("2026-11-18 08:59", false)]
    [InlineData("2026-11-18 09:00", true)]
    [InlineData("2026-11-18 17:59", true)]
    [InlineData("2026-11-18 18:00", false)]
    [InlineData("2026-11-21 12:00", false)] // Saturday
    public void Day_range(string at, bool expected)
    {
        var w = new WorkHoursSettings { Enabled = true };
        Assert.Equal(expected, w.Contains(DateTime.Parse(at)));
    }

    [Theory]
    [InlineData("2026-11-20 23:00", true)]  // Friday night
    [InlineData("2026-11-21 05:00", true)]  // Saturday early morning belongs to Friday's shift
    [InlineData("2026-11-22 05:00", false)] // Sunday early morning belongs to Saturday (off)
    [InlineData("2026-11-18 12:00", false)]
    public void Overnight_range(string at, bool expected)
    {
        var w = new WorkHoursSettings { Enabled = true, Start = new(22, 0), End = new(6, 0) };
        Assert.Equal(expected, w.Contains(DateTime.Parse(at)));
    }
}
