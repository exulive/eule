using Eule.Core.Settings;

namespace Eule.Core.Engine;

public enum Phase
{
    /// <summary>Counting sitting time.</summary>
    Working,
    /// <summary>User paused Eule.</summary>
    Paused,
    /// <summary>Away from the computer long enough to count as a break; waiting for input.</summary>
    Away,
    /// <summary>Break is due but the user is presenting / in a call / in a full-screen app.</summary>
    Deferred,
    /// <summary>Break is due and a notification is waiting for an answer (Gentle / Balanced).</summary>
    BreakDue,
    /// <summary>Break in progress.</summary>
    Break,
    /// <summary>Break completed; waiting for the user to start again (AutoRestart off).</summary>
    BreakFinished,
    /// <summary>Outside the configured work hours.</summary>
    OffHours,
}

/// <summary>Why the user should not be interrupted right now. Detected by the app layer.</summary>
[Flags]
public enum BusyReason
{
    None = 0,
    Presentation = 1,
    FullScreen = 2,
    Call = 4,
    DoNotDisturb = 8,
}

public enum BreakEndReason { Completed, Skipped, Snoozed, EmergencyExit }

/// <summary>What the engine needs to know about the outside world on each tick.</summary>
public readonly record struct EnvironmentSnapshot(DateTime LocalNow, int IdleSeconds, BusyReason Busy = BusyReason.None);

public abstract record EngineEvent;

/// <summary>Seconds spent sitting at <see cref="At"/>. Raised every tick while sitting.</summary>
public sealed record Sitting(DateTime At, int Seconds) : EngineEvent;
public sealed record PhaseChanged(Phase From, Phase To) : EngineEvent;
public sealed record WarningRaised(int SecondsLeft) : EngineEvent;
/// <summary>Ask the user for a break (notification). <see cref="SnoozesLeft"/> is -1 when unlimited.</summary>
public sealed record BreakDue(ReminderLevel Level, int SnoozesLeft, bool Repeat) : EngineEvent;
public sealed record BreakDeferred(BusyReason Reason) : EngineEvent;
public sealed record BreakStarted(int DurationSeconds, bool Strict, bool EmergencyExitAllowed) : EngineEvent;
public sealed record BreakEnded(BreakEndReason Reason) : EngineEvent;
public sealed record BreakSnoozed(int Minutes) : EngineEvent;
public sealed record WorkResumed : EngineEvent;
/// <summary>Away for a full break length; counted as a break. <see cref="UncountSeconds"/> were counted as sitting but were not.</summary>
public sealed record AwayCredited(int UncountSeconds) : EngineEvent;
public sealed record ReturnedFromAway : EngineEvent;
public sealed record EyeBreakDue(int Seconds) : EngineEvent;
