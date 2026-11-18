using Eule.App.Controls;
using Eule.Core.Engine;
using Eule.Core.Insights;
using Eule.Core.Localization;

namespace Eule.App.Views;

/// <summary>Turns engine state into the few words and numbers Eule shows. One place, so every surface says the same thing.</summary>
static class Present
{
    public static string Clock(int s)
    {
        s = Math.Max(0, s);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    public static string Duration(int s) => Words.Duration(s);

    public static bool IsWarning(BreakEngine e) =>
        e.Phase == Phase.Working && e.WorkLeft <= Math.Max(e.Settings.WarnSeconds, e.WorkTarget / 10);

    public static string Caption(BreakEngine e) => L.T(e.Phase switch
    {
        Phase.Working => "caption.working",
        Phase.Paused => "caption.paused",
        Phase.Away => "caption.away",
        Phase.Deferred => "caption.deferred",
        Phase.BreakDue => "caption.due",
        Phase.Break => e.IsStrictBreak ? "caption.strict" : "caption.break",
        Phase.BreakFinished => "caption.finished",
        _ => "caption.offhours",
    });

    public static string Time(BreakEngine e) => e.Phase switch
    {
        Phase.Working or Phase.Paused => Clock(e.WorkLeft),
        Phase.Break => Clock(e.BreakRemaining),
        Phase.Away => L.T("time.away"),
        Phase.Deferred => L.T("time.deferred"),
        Phase.BreakDue => L.T("time.due"),
        Phase.BreakFinished => L.T("time.finished"),
        _ => "—",
    };

    public static OwlMood Mood(BreakEngine e) => e.Phase switch
    {
        Phase.Working => IsWarning(e) ? OwlMood.Alert : OwlMood.Awake,
        Phase.BreakDue or Phase.Deferred => OwlMood.Alert,
        Phase.Break or Phase.Away => OwlMood.Sleeping,
        Phase.Paused or Phase.OffHours => OwlMood.Drowsy,
        _ => OwlMood.Awake,
    };

    /// <summary>One-line status for the tray tooltip and menu header.</summary>
    public static string Status(BreakEngine e) => e.Phase switch
    {
        Phase.Working => L.F("status.working", Clock(e.WorkLeft)),
        Phase.Paused => L.F("status.paused", Clock(e.WorkLeft)),
        Phase.Away => L.T("status.away"),
        Phase.Deferred => L.T("status.deferred"),
        Phase.BreakDue => L.T("status.due"),
        Phase.Break => L.F("status.break", Clock(e.BreakRemaining)),
        Phase.BreakFinished => L.T("status.finished"),
        _ => L.T("status.offhours"),
    };

    public sealed record Exercise(string Title, string Text);

    const int ExerciseCount = 9;

    /// <summary>Short movements for the break screen, roughly 30–40 seconds each.</summary>
    public static Exercise[] Exercises =>
        Enumerable.Range(0, ExerciseCount).Select(i => new Exercise(L.T($"ex.{i}.t"), L.T($"ex.{i}.x"))).ToArray();

    static readonly Random Rng = new();

    /// <summary>Exercises for one break, starting with standing up, the rest shuffled.</summary>
    public static List<Exercise> ExercisePlan()
    {
        var all = Exercises;
        var rest = all.Skip(1).OrderBy(_ => Rng.Next()).ToList();
        rest.Insert(0, all[0]);
        return rest;
    }
}
