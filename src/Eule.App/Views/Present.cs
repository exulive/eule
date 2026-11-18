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

    public sealed record Exercise(string Title, string Text);

    /// <summary>Short movements for the break screen, roughly 30–40 seconds each.</summary>
    public static readonly Exercise[] Exercises =
    [
        new("Ayağa kalk", "Önce bir kalk. Omuzlarını gevşet, ağırlığını iki ayağına eşit ver."),
        new("Boyun", "Başını yavaşça sağa yatır, on saniye bekle. Sonra sola. Zorlamadan."),
        new("Omuzlar", "Omuzlarını on kez geriye, on kez öne doğru büyük daireler çizerek çevir."),
        new("Göğüs", "Ellerini arkada birleştir, kollarını hafifçe kaldır, göğsünü aç. On beş saniye."),
        new("Bel", "Kollarını yukarı uzat. Önce sağa, sonra sola uzun bir yay gibi eğil."),
        new("Bilekler", "Kolunu öne uzat, parmaklarını diğer elinle kendine doğru çek. İki el de."),
        new("Bacaklar", "Bir ayağını arkaya kıvırıp bileğinden tut, on beş saniye. Sonra diğeri."),
        new("Gözler", "Pencereden en uzak noktaya bak. Yirmi saniye sonra gözlerini birkaç kez kırp."),
        new("Yürü", "Birkaç adım at. Bir bardak su doldur ya da pencereye kadar git gel."),
    ];

    /// <summary>Exercises for one break, starting with standing up, the rest shuffled.</summary>
    public static List<Exercise> ExercisePlan()
    {
        var rest = Exercises.Skip(1).OrderBy(_ => Rng.Next()).ToList();
        rest.Insert(0, Exercises[0]);
        return rest;
    }
}
