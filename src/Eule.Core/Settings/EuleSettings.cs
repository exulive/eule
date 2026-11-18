using System.Text.Json.Serialization;

namespace Eule.Core.Settings;

/// <summary>How insistently Eule asks for a break when the sitting time is up.</summary>
public enum ReminderLevel
{
    /// <summary>Notification only; repeats every few minutes until answered.</summary>
    Gentle,
    /// <summary>Notification first; break screen opens after a short grace period or when snoozes run out.</summary>
    Balanced,
    /// <summary>Break screen opens immediately; can be snoozed or skipped.</summary>
    Firm,
    /// <summary>Screen goes dark; no snooze, no skip. Only the optional emergency exit ends it early.</summary>
    Strict,
}

public enum AppTheme { System, Light, Dark }

public sealed class EyeBreakSettings
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 20;
    public int DurationSeconds { get; set; } = 20;
}

public sealed class WorkHoursSettings
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(9, 0);
    public TimeOnly End { get; set; } = new(18, 0);
    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>True when reminders should run at <paramref name="t"/>. Supports overnight ranges (22:00–06:00).</summary>
    public bool Contains(DateTime t)
    {
        if (!Enabled) return true;
        var time = TimeOnly.FromDateTime(t);
        if (Start == End) return Days.Contains(t.DayOfWeek);

        if (Start < End)
            return time >= Start && time < End && Days.Contains(t.DayOfWeek);

        // Overnight: the early-morning part belongs to the previous day's shift.
        if (time >= Start) return Days.Contains(t.DayOfWeek);
        if (time < End) return Days.Contains(t.AddDays(-1).DayOfWeek);
        return false;
    }
}

public sealed class EuleSettings
{
    public int WorkMinutes { get; set; } = 30;
    public int BreakMinutes { get; set; } = 5;
    public int SnoozeMinutes { get; set; } = 5;
    public int MaxSnoozes { get; set; } = 2;
    public int WarnSeconds { get; set; } = 60;

    public ReminderLevel Level { get; set; } = ReminderLevel.Balanced;
    /// <summary>Balanced level: seconds the notification waits before the break screen opens by itself.</summary>
    public int BalancedGraceSeconds { get; set; } = 60;
    /// <summary>Gentle level: seconds between repeated notifications.</summary>
    public int GentleRepeatSeconds { get; set; } = 300;

    /// <summary>Being away from the computer for a full break length counts as a break.</summary>
    public bool IdleCountsAsBreak { get; set; } = true;
    /// <summary>Hold the break while presenting, in a call or in a full-screen app.</summary>
    public bool DeferWhenBusy { get; set; } = true;
    /// <summary>Strict level: allow ending the break early by holding Shift for 10 seconds.</summary>
    public bool EmergencyExit { get; set; } = true;
    public bool AutoRestart { get; set; } = true;
    public bool CoverAllMonitors { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool ShowWidget { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>Breaks per day that count the day towards the streak.</summary>
    public int DailyBreakGoal { get; set; } = 4;

    public EyeBreakSettings EyeBreak { get; set; } = new();
    public WorkHoursSettings WorkHours { get; set; } = new();

    public AppTheme Theme { get; set; } = AppTheme.System;
    public string Language { get; set; } = "tr";
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }

    [JsonIgnore] public int WorkSeconds => WorkMinutes * 60;
    [JsonIgnore] public int BreakSeconds => BreakMinutes * 60;
    [JsonIgnore] public int SnoozeSeconds => SnoozeMinutes * 60;

    /// <summary>Clamps every value into a usable range so a hand-edited file cannot break the app.</summary>
    public EuleSettings Normalize()
    {
        WorkMinutes = Math.Clamp(WorkMinutes, 1, 240);
        BreakMinutes = Math.Clamp(BreakMinutes, 1, 60);
        SnoozeMinutes = Math.Clamp(SnoozeMinutes, 1, 60);
        MaxSnoozes = Math.Clamp(MaxSnoozes, 0, 10);
        WarnSeconds = Math.Clamp(WarnSeconds, 0, 600);
        BalancedGraceSeconds = Math.Clamp(BalancedGraceSeconds, 10, 600);
        GentleRepeatSeconds = Math.Clamp(GentleRepeatSeconds, 60, 3600);
        DailyBreakGoal = Math.Clamp(DailyBreakGoal, 1, 20);
        EyeBreak ??= new();
        EyeBreak.IntervalMinutes = Math.Clamp(EyeBreak.IntervalMinutes, 5, 120);
        EyeBreak.DurationSeconds = Math.Clamp(EyeBreak.DurationSeconds, 5, 120);
        WorkHours ??= new();
        WorkHours.Days ??= [];
        if (!Enum.IsDefined(Level)) Level = ReminderLevel.Balanced;
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (Language is not ("tr" or "en")) Language = "tr";
        return this;
    }

    public EuleSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));
}
