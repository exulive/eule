using Eule.App.Controls;
using Eule.Core.Engine;

namespace Eule.App.Views;

/// <summary>Turns engine state into the few words and numbers Eule shows. One place, so every surface says the same thing.</summary>
static class Present
{
    public static string Clock(int s)
    {
        s = Math.Max(0, s);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    public static string Duration(int s)
    {
        int m = Math.Max(0, s) / 60;
        return m < 60 ? $"{m} dk" : $"{m / 60} sa {m % 60} dk";
    }

    public static bool IsWarning(BreakEngine e) =>
        e.Phase == Phase.Working && e.WorkLeft <= Math.Max(e.Settings.WarnSeconds, e.WorkTarget / 10);

    public static string Caption(BreakEngine e) => e.Phase switch
    {
        Phase.Working => "molaya",
        Phase.Paused => "duraklatıldı",
        Phase.Away => "uzaktasın",
        Phase.Deferred => "meşgulsün",
        Phase.BreakDue => "kalkma zamanı",
        Phase.Break => e.IsStrictBreak ? "zorunlu mola" : "mola",
        Phase.BreakFinished => "mola bitti",
        Phase.OffHours => "mesai dışı",
        _ => "",
    };

    public static string Time(BreakEngine e) => e.Phase switch
    {
        Phase.Working or Phase.Paused => Clock(e.WorkLeft),
        Phase.Break => Clock(e.BreakRemaining),
        Phase.Away => "mola",
        Phase.Deferred => "bekliyor",
        Phase.BreakDue => "şimdi",
        Phase.BreakFinished => "hazır",
        Phase.OffHours => "—",
        _ => "",
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
        Phase.Working => $"Molaya {Clock(e.WorkLeft)}",
        Phase.Paused => $"Duraklatıldı · {Clock(e.WorkLeft)}",
        Phase.Away => "Uzaktasın, mola sayıldı",
        Phase.Deferred => "Meşgulsün, mola bekliyor",
        Phase.BreakDue => "Kalkma zamanı",
        Phase.Break => $"Mola · {Clock(e.BreakRemaining)}",
        Phase.BreakFinished => "Mola bitti",
        Phase.OffHours => "Mesai dışı",
        _ => "",
    };

    static readonly string[] Tips =
    [
        "Ayağa kalk, birkaç dakika yürü.",
        "Omuzlarını geriye doğru çevir, boynunu yavaşça esnet.",
        "Bir bardak su iç.",
        "Pencereden uzağa bak, gözlerini dinlendir.",
        "Kollarını yukarı uzat, belini iki yana esnet.",
        "Gözlerini kapat, dört saniye nefes al, dört saniye ver.",
        "Merdivenleri kullan ya da odada bir tur at.",
        "Bileklerini çevir, parmaklarını aç ve kapa.",
    ];

    static readonly Random Rng = new();
    public static string RandomTip() => Tips[Rng.Next(Tips.Length)];
}
