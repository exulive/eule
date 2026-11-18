using Eule.Core.Settings;

namespace Eule.Core.Engine;

/// <summary>
/// The sitting / break state machine. It has no timers and no UI: the host calls <see cref="Tick"/>
/// once a second with a snapshot of the environment, and calls the command methods when the user acts.
/// Everything that happens is reported through <see cref="Raised"/>.
/// </summary>
public sealed class BreakEngine
{
    /// <summary>Ticks longer than this are treated as a gap (sleep, hibernate, frozen process).</summary>
    public const int GapSeconds = 60;
    /// <summary>Idle below this many seconds means the user is back.</summary>
    public const int ReturnIdleSeconds = 5;

    /// <summary>A full-screen app (video, game) may hold the break this long; calls and presentations have no limit.</summary>
    public const int MaxFullScreenDeferSeconds = 30 * 60;

    int dueElapsed, eyeElapsed, deferElapsed;
    bool warned;

    public BreakEngine(EuleSettings settings)
    {
        Settings = settings.Clone();
        ResetCounters();
    }

    public event Action<EngineEvent>? Raised;

    public EuleSettings Settings { get; private set; }
    public Phase Phase { get; private set; } = Phase.Working;
    public int WorkElapsed { get; private set; }
    public int WorkTarget { get; private set; }
    public int BreakRemaining { get; private set; }
    public int BreakTotal { get; private set; }
    public int SnoozesUsed { get; private set; }
    public bool IsStrictBreak { get; private set; }

    public int WorkLeft => Math.Max(0, WorkTarget - WorkElapsed);

    /// <summary>0..1 progress of the current phase (sitting fills up; break fills up).</summary>
    public double Progress => Phase switch
    {
        Phase.Break => BreakTotal <= 0 ? 0 : 1 - (double)BreakRemaining / BreakTotal,
        Phase.BreakFinished => 1,
        Phase.Away or Phase.OffHours => 0,
        _ => WorkTarget <= 0 ? 0 : Math.Min(1, (double)WorkElapsed / WorkTarget),
    };

    public bool CanSnooze =>
        Phase is Phase.BreakDue or Phase.Break or Phase.Deferred && !IsStrictBreak &&
        (Settings.Level == ReminderLevel.Gentle || SnoozesUsed < Settings.MaxSnoozes);

    public bool CanSkip => Phase is Phase.BreakDue or Phase.Break or Phase.Deferred && !IsStrictBreak;

    public bool CanEmergencyExit => Phase == Phase.Break && IsStrictBreak && Settings.EmergencyExit;

    int SnoozesLeft => Settings.Level == ReminderLevel.Gentle ? -1 : Math.Max(0, Settings.MaxSnoozes - SnoozesUsed);

    // ------------------------------------------------------------------ tick

    public void Tick(EnvironmentSnapshot env, int seconds = 1)
    {
        if (seconds <= 0) return;
        if (seconds > GapSeconds)
        {
            HandleGap(env, seconds);
            return;
        }

        if (!UpdateWorkHours(env)) return;

        switch (Phase)
        {
            case Phase.Working:
            case Phase.Deferred:
            case Phase.BreakDue:
                TickSitting(env, seconds);
                break;

            case Phase.Away:
                if (env.IdleSeconds < ReturnIdleSeconds)
                {
                    SetPhase(Phase.Working);
                    Raise(new ReturnedFromAway());
                }
                break;

            case Phase.Break:
                BreakRemaining = Math.Max(0, BreakRemaining - seconds);
                if (BreakRemaining == 0) FinishBreak();
                break;
        }
    }

    /// <summary>Returns false when the tick should stop here (off hours).</summary>
    bool UpdateWorkHours(EnvironmentSnapshot env)
    {
        bool inHours = Settings.WorkHours.Contains(env.LocalNow);
        if (Phase == Phase.OffHours)
        {
            if (!inHours) return false;
            ResetCounters();
            SetPhase(Phase.Working);
            return true;
        }
        // A running break or a paused session is left alone when work hours end.
        if (!inHours && Phase is Phase.Working or Phase.Deferred or Phase.BreakDue or Phase.Away)
        {
            ResetCounters();
            SetPhase(Phase.OffHours);
            return false;
        }
        return true;
    }

    void TickSitting(EnvironmentSnapshot env, int seconds)
    {
        if (Settings.IdleCountsAsBreak && env.IdleSeconds >= Settings.BreakSeconds)
        {
            // The idle period was counted as sitting until now; take it back (it cannot exceed what we counted).
            int uncount = Math.Min(env.IdleSeconds - seconds, WorkElapsed);
            Raise(new AwayCredited(Math.Max(0, uncount)));
            ResetCounters();
            SetPhase(Phase.Away);
            return;
        }

        WorkElapsed += seconds;
        Raise(new Sitting(env.LocalNow, seconds));

        if (Settings.EyeBreak.Enabled && Phase == Phase.Working)
        {
            eyeElapsed += seconds;
            if (eyeElapsed >= Settings.EyeBreak.IntervalMinutes * 60)
            {
                eyeElapsed = 0;
                // Skip the eye break when the real break is about to start anyway.
                if (WorkLeft > Settings.EyeBreak.DurationSeconds * 3)
                    Raise(new EyeBreakDue(Settings.EyeBreak.DurationSeconds));
            }
        }

        switch (Phase)
        {
            case Phase.Working:
                if (!warned && Settings.WarnSeconds > 0 && WorkLeft > 0 && WorkLeft <= Settings.WarnSeconds)
                {
                    warned = true;
                    Raise(new WarningRaised(WorkLeft));
                }
                if (WorkLeft == 0) BreakIsDue(env);
                break;

            case Phase.Deferred:
                deferElapsed += seconds;
                if (!IsBusy(env)) BreakIsDue(env);
                break;

            case Phase.BreakDue:
                dueElapsed += seconds;
                if (Settings.Level == ReminderLevel.Balanced && dueElapsed >= Settings.BalancedGraceSeconds)
                    StartBreak();
                else if (Settings.Level == ReminderLevel.Gentle && dueElapsed >= Settings.GentleRepeatSeconds)
                {
                    dueElapsed = 0;
                    Raise(new BreakDue(Settings.Level, SnoozesLeft, Repeat: true));
                }
                break;
        }
    }

    bool IsBusy(EnvironmentSnapshot env)
    {
        if (!Settings.DeferWhenBusy || env.Busy == BusyReason.None) return false;
        // Only a full-screen app (no call, no presentation) runs out of patience.
        bool onlyFullScreen = (env.Busy & ~BusyReason.FullScreen) == BusyReason.None;
        return !(onlyFullScreen && deferElapsed >= MaxFullScreenDeferSeconds);
    }

    void BreakIsDue(EnvironmentSnapshot env)
    {
        if (IsBusy(env))
        {
            if (Phase != Phase.Deferred)
            {
                deferElapsed = 0;
                SetPhase(Phase.Deferred);
                Raise(new BreakDeferred(env.Busy));
            }
            return;
        }

        bool direct = Settings.Level is ReminderLevel.Firm or ReminderLevel.Strict
                      || (Settings.Level == ReminderLevel.Balanced && SnoozesUsed >= Settings.MaxSnoozes);
        if (direct)
        {
            StartBreak();
            return;
        }

        dueElapsed = 0;
        SetPhase(Phase.BreakDue);
        Raise(new BreakDue(Settings.Level, SnoozesLeft, Repeat: false));
    }

    void HandleGap(EnvironmentSnapshot env, int seconds)
    {
        switch (Phase)
        {
            case Phase.Break:
                BreakRemaining = Math.Max(0, BreakRemaining - seconds);
                if (BreakRemaining == 0) FinishBreak();
                return;

            case Phase.Working:
            case Phase.Deferred:
            case Phase.BreakDue:
                if (Settings.IdleCountsAsBreak && seconds >= Settings.BreakSeconds)
                {
                    // Nothing was counted during the gap, so there is nothing to take back.
                    Raise(new AwayCredited(0));
                    ResetCounters();
                    SetPhase(env.IdleSeconds < ReturnIdleSeconds ? Phase.Working : Phase.Away);
                }
                return;
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>"Take a break now" or accepting the notification.</summary>
    public void StartBreakNow()
    {
        if (Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue) StartBreak();
    }

    public bool Snooze()
    {
        if (!CanSnooze) return false;
        bool wasBreak = Phase == Phase.Break;
        SnoozesUsed++;
        WorkTarget = WorkElapsed + Settings.SnoozeSeconds;
        warned = true;
        IsStrictBreak = false;
        if (wasBreak) Raise(new BreakEnded(BreakEndReason.Snoozed));
        Raise(new BreakSnoozed(Settings.SnoozeMinutes));
        SetPhase(Phase.Working);
        return true;
    }

    public bool Skip()
    {
        if (!CanSkip) return false;
        Raise(new BreakEnded(BreakEndReason.Skipped));
        ResetCounters();
        SetPhase(Phase.Working);
        return true;
    }

    public bool EmergencyExit()
    {
        if (!CanEmergencyExit) return false;
        Raise(new BreakEnded(BreakEndReason.EmergencyExit));
        ResetCounters();
        SetPhase(Phase.Working);
        return true;
    }

    /// <summary>"Start working" after a finished break.</summary>
    public void StartWork()
    {
        if (Phase != Phase.BreakFinished) return;
        ResetCounters();
        SetPhase(Phase.Working);
        Raise(new WorkResumed());
    }

    public void Pause()
    {
        if (Phase is Phase.Working or Phase.Deferred or Phase.BreakDue or Phase.Away) SetPhase(Phase.Paused);
    }

    public void Resume()
    {
        if (Phase == Phase.Paused) SetPhase(Phase.Working);
    }

    public void TogglePause()
    {
        if (Phase == Phase.Paused) Resume(); else Pause();
    }

    /// <summary>"Reset the counter": start a fresh sitting period.</summary>
    public void Reset()
    {
        if (Phase is Phase.Break) return;
        ResetCounters();
        if (Phase is not (Phase.Paused or Phase.OffHours)) SetPhase(Phase.Working);
    }

    public void ApplySettings(EuleSettings settings)
    {
        int oldWork = Settings.WorkSeconds;
        Settings = settings.Clone();
        // Only a changed sitting length moves the target; other settings must not cancel a running snooze.
        if (Settings.WorkSeconds != oldWork && Phase is Phase.Working or Phase.Paused)
        {
            WorkTarget = Math.Max(WorkTarget + Settings.WorkSeconds - oldWork, WorkElapsed + 1);
            warned = WorkLeft <= Settings.WarnSeconds;
        }
    }

    // ------------------------------------------------------------------ internals

    void StartBreak()
    {
        IsStrictBreak = Settings.Level == ReminderLevel.Strict;
        BreakTotal = BreakRemaining = Settings.BreakSeconds;
        SetPhase(Phase.Break);
        Raise(new BreakStarted(BreakTotal, IsStrictBreak, IsStrictBreak && Settings.EmergencyExit));
    }

    void FinishBreak()
    {
        IsStrictBreak = false;
        Raise(new BreakEnded(BreakEndReason.Completed));
        ResetCounters();
        if (Settings.AutoRestart)
        {
            SetPhase(Phase.Working);
            Raise(new WorkResumed());
        }
        else
        {
            SetPhase(Phase.BreakFinished);
        }
    }

    void ResetCounters()
    {
        WorkElapsed = 0;
        WorkTarget = Settings.WorkSeconds;
        SnoozesUsed = 0;
        warned = false;
        dueElapsed = 0;
        eyeElapsed = 0;
        IsStrictBreak = false;
    }

    void SetPhase(Phase next)
    {
        if (next == Phase) return;
        var prev = Phase;
        Phase = next;
        Raise(new PhaseChanged(prev, next));
    }

    void Raise(EngineEvent e) => Raised?.Invoke(e);
}
