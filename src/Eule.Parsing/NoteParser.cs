using System.Globalization;
using System.Text.RegularExpressions;

namespace Eule.Parsing;

/// <summary>What a dictated sentence means for a note: a short title and, when one was said, a time.</summary>
public sealed record ParsedNote(
    string Title,
    /// <summary>Local (user's time zone) date and time of the reminder, or null for a plain note.</summary>
    DateTime? DueLocal,
    /// <summary>False when only a day was said ("cumartesi"): <see cref="DueLocal"/> is then that day at 09:00.</summary>
    bool HasTime);

/// <summary>
/// Understands Turkish dates and times in dictated sentences, e.g. "Cumartesi saat 4'te toplantı var, unutma",
/// "yarın akşam 7 buçukta annemi ara", "yarım saat sonra çayı kapat", "25 Kasım'da fatura son gün".
/// Rule-based, offline, deterministic. Hours 1–7 without "sabah" are read as afternoon (16:00, not 04:00).
/// </summary>
public static class NoteParser
{
    static readonly CultureInfo Tr = new("tr-TR");
    public const int DefaultHourForDayOnly = 9;

    const string Days = "pazartesi|salı|çarşamba|perşembe|cumartesi|cuma|pazar";
    const string Months = "ocak|şubat|mart|nisan|mayıs|haziran|temmuz|ağustos|eylül|ekim|kasım|aralık";
    // Turkish case endings that may follow a day/month/number (with or without an apostrophe).
    const string Case = @"(?:'?(?:inde|ında|ünde|unda|nde|nda|ine|ına|ye|ya|yi|yı|si|sı|de|da|te|ta|den|dan|ten|tan|nin|nın|ki|günü|gün|e|a|i|ı))?";
    const string At = @"'?(?:te|ta|de|da)";
    const string NumberWords = "on bir|on iki|bir|iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on|yirmi|otuz|kırk|elli";

    static readonly Dictionary<string, int> Numbers = new()
    {
        ["bir"] = 1, ["iki"] = 2, ["üç"] = 3, ["dört"] = 4, ["beş"] = 5, ["altı"] = 6, ["yedi"] = 7, ["sekiz"] = 8,
        ["dokuz"] = 9, ["on"] = 10, ["on bir"] = 11, ["on iki"] = 12, ["yirmi"] = 20, ["otuz"] = 30, ["kırk"] = 40,
        ["elli"] = 50, ["yarım"] = 0,
    };

    static readonly Dictionary<string, int> DayIndex = new()
    {
        ["pazartesi"] = 0, ["salı"] = 1, ["çarşamba"] = 2, ["perşembe"] = 3, ["cuma"] = 4, ["cumartesi"] = 5, ["pazar"] = 6,
    };

    static readonly string[] MonthNames = Months.Split('|');

    const RegexOptions O = RegexOptions.CultureInvariant;

    // "yarım saat sonra", "10 dakika sonra", "iki gün sonra"
    static readonly Regex RelativeRx = new($@"\b(\d+|yarım|{NumberWords})\s*(dakika|dk|saat|gün|hafta)\s+sonra\b", O);
    static readonly Regex TodayRx = new($@"\b(?:bugün|bu\s+gün){Case}\b", O);
    static readonly Regex TomorrowRx = new($@"\byarın{Case}\b", O);
    static readonly Regex AfterTomorrowRx = new($@"\b(?:öbür\s*gün|ertesi\s+gün){Case}\b", O);
    static readonly Regex WeekdayRx = new($@"\b(haftaya\s+|gelecek\s+|bu\s+)?({Days}){Case}\b", O);
    static readonly Regex MonthDateRx = new($@"\b(\d{{1,2}})\s+({Months}){Case}\b", O);
    static readonly Regex DayOfMonthRx = new($@"\bayın\s+(\d{{1,2}}){Case}\b", O);
    // A part of the day, unless it names a thing: "öğle yemeği", "akşam yemeği", "öğle arası", "sabah namazı".
    static readonly Regex PartRx = new(
        @"\b(sabah|öğleden\s+sonra|öğlen|öğle|akşam|gece)(?>\w*)(?!\s+(?:yemeğ|yemek|arası|aras|namaz|kahvalt|haber|güneş|kahve))", O);
    // "16:30", "16.30", "16:30'da"
    static readonly Regex ClockRx = new($@"\b([01]?\d|2[0-3])[:.]([0-5]\d){Case}\b", O);
    // "saat 4", "saat 4 buçukta", "saat dörtte", "saat on bir buçukta"
    static readonly Regex SaatRx = new($@"\bsaat\s+(\d{{1,2}}|{NumberWords})(\s*buçuk)?(?:{At})?\b", O);
    // "4'te", "4 buçukta" (a digit followed by a locative ending)
    static readonly Regex DigitAtRx = new($@"\b(\d{{1,2}})(?:{At}|\s*buçuk{At})\b", O);
    // "akşam yedide", "sabah dokuz buçukta" (number words only after a part of day)
    static readonly Regex PartWordRx = new($@"\b(sabah|öğleden\s+sonra|akşam|gece)\s+({NumberWords}|\d{{1,2}})(\s*buçuk)?(?:{At})?\b", O);

    static readonly Regex FillerRx = new(
        @"\b(?:unutma(?:yayım|yalım|)|hatırlat(?:ır mısın|sana|ma|ın|)?|bana|lütfen|not\s+al|not\s+et|kaydet|var(?:dı)?|olacak|saat|günü)\b",
        O);

    public static ParsedNote Parse(string text, DateTime nowLocal)
    {
        text = (text ?? "").Trim();
        string s = Normalize(text);
        var cut = new bool[s.Length];
        void Cut(Match m) { for (int i = m.Index; i < m.Index + m.Length && i < cut.Length; i++) cut[i] = true; }

        DateTime? relative = null;
        DateOnly? date = null;
        int? hour = null, minute = null;
        string? part = null;

        // 1. Relative ("yarım saat sonra") wins over everything else.
        var rel = RelativeRx.Match(s);
        if (rel.Success)
        {
            int n = rel.Groups[1].Value == "yarım" ? 0 : ToNumber(rel.Groups[1].Value);
            string unit = rel.Groups[2].Value;
            relative = unit switch
            {
                "dakika" or "dk" => nowLocal.AddMinutes(n),
                "saat" => rel.Groups[1].Value == "yarım" ? nowLocal.AddMinutes(30) : nowLocal.AddHours(n),
                "gün" => nowLocal.AddDays(n),
                _ => nowLocal.AddDays(7 * n),
            };
            Cut(rel);
        }

        // 2. The day.
        var today = DateOnly.FromDateTime(nowLocal);
        Match m;
        if ((m = AfterTomorrowRx.Match(s)).Success) { date = today.AddDays(2); Cut(m); }
        else if ((m = TomorrowRx.Match(s)).Success) { date = today.AddDays(1); Cut(m); }
        else if ((m = TodayRx.Match(s)).Success) { date = today; Cut(m); }
        if ((m = MonthDateRx.Match(s)).Success)
        {
            int day = int.Parse(m.Groups[1].Value), month = Array.IndexOf(MonthNames, m.Groups[2].Value) + 1;
            if (TryDate(today.Year, month, day, out var d))
            {
                if (d < today && TryDate(today.Year + 1, month, day, out var next)) d = next;
                date = d;
                Cut(m);
            }
        }
        else if ((m = DayOfMonthRx.Match(s)).Success)
        {
            int day = int.Parse(m.Groups[1].Value);
            if (TryDate(today.Year, today.Month, day, out var d))
            {
                if (d < today) TryDate(today.AddMonths(1).Year, today.AddMonths(1).Month, day, out d);
                date = d;
                Cut(m);
            }
        }
        if (date == null && (m = WeekdayRx.Match(s)).Success)
        {
            date = ResolveWeekday(today, DayIndex[m.Groups[2].Value], m.Groups[1].Value.Trim());
            Cut(m);
        }

        // 3. The time.
        if ((m = PartWordRx.Match(s)).Success)
        {
            part = m.Groups[1].Value;
            hour = ToNumber(m.Groups[2].Value);
            minute = m.Groups[3].Success && m.Groups[3].Length > 0 ? 30 : 0;
            Cut(m);
        }
        else if ((m = ClockRx.Match(s)).Success)
        {
            hour = int.Parse(m.Groups[1].Value);
            minute = int.Parse(m.Groups[2].Value);
            Cut(m);
        }
        else if ((m = SaatRx.Match(s)).Success)
        {
            hour = ToNumber(m.Groups[1].Value);
            minute = m.Groups[2].Success && m.Groups[2].Length > 0 ? 30 : 0;
            Cut(m);
        }
        else if ((m = DigitAtRx.Match(s)).Success && int.Parse(m.Groups[1].Value) is >= 0 and <= 23)
        {
            hour = int.Parse(m.Groups[1].Value);
            minute = m.Value.Contains("buçuk") ? 30 : 0;
            Cut(m);
        }
        if ((m = PartRx.Match(s)).Success)
        {
            // "akşam yemeği" is a meal, not a time: a bare part of the day only counts next to a day or an
            // hour ("yarın akşam", "akşam 7") or after "bu" ("bu akşam").
            bool afterBu = m.Index >= 3 && s.Substring(m.Index - 3, 3) == "bu ";
            if (hour != null || date != null || afterBu)
            {
                part ??= m.Groups[1].Value;
                Cut(m);
                if (afterBu) for (int i = m.Index - 3; i < m.Index; i++) cut[i] = true;
            }
        }

        string title = BuildTitle(text, s, cut);

        if (relative != null)
            return new ParsedNote(title, Trim(relative.Value), true);

        if (hour == null && part != null)
        {
            // "yarın sabah" with no hour: a sensible default for that part of the day.
            (hour, minute) = part switch
            {
                "sabah" => (9, 0),
                "öğlen" or "öğle" => (12, 0),
                "akşam" => (19, 0),
                "gece" => (22, 0),
                _ => (15, 0), // öğleden sonra
            };
        }

        if (hour == null)
        {
            if (date == null) return new ParsedNote(title, null, false);
            return new ParsedNote(title, date.Value.ToDateTime(new TimeOnly(DefaultHourForDayOnly, 0)), false);
        }

        int h = AdjustHour(hour.Value, part);
        var time = new TimeOnly(h, minute ?? 0);
        if (date == null)
        {
            // Only a time: today, or tomorrow if it has already passed.
            date = today;
            if (today.ToDateTime(time) <= nowLocal) date = today.AddDays(1);
        }
        return new ParsedNote(title, date.Value.ToDateTime(time), true);
    }

    /// <summary>"4" means 16:00 unless it is the morning; "sabah 4" stays 04:00, "akşam 8" is 20:00.</summary>
    static int AdjustHour(int h, string? part)
    {
        if (h >= 13) return Math.Min(h, 23);
        return part switch
        {
            "sabah" => h == 12 ? 0 : h,
            "öğlen" or "öğle" => h is >= 1 and <= 6 ? h + 12 : h,
            "öğleden sonra" or "akşam" => h == 12 ? 12 : h + 12,
            "gece" => h is >= 6 and <= 11 ? h + 12 : (h == 12 ? 0 : h),
            _ => h is >= 1 and <= 7 ? h + 12 : h, // no part of day: working hours guess
        };
    }

    static DateOnly ResolveWeekday(DateOnly today, int target, string modifier)
    {
        int todayIdx = ((int)today.DayOfWeek + 6) % 7;
        if (modifier is "haftaya" or "gelecek")
        {
            // The given day of next week (weeks start on Monday).
            return today.AddDays(-todayIdx + 7 + target);
        }
        int ahead = (target - todayIdx + 7) % 7;
        return today.AddDays(ahead); // today counts; a past time today moves on below only when no day was said
    }

    static string BuildTitle(string original, string normalized, bool[] cut)
    {
        // Lower-casing Turkish keeps the length, so the cut marks line up with the original text and its capitals.
        string source = original.Length == normalized.Length ? original : normalized;
        var chars = new char[source.Length];
        for (int i = 0; i < source.Length; i++) chars[i] = cut[i] ? ' ' : source[i];
        string rest = new(chars);

        // Remove filler words on a lower-case copy, again position by position.
        string lower = Normalize(rest);
        var fill = new bool[lower.Length];
        foreach (Match f in FillerRx.Matches(lower))
            for (int i = f.Index; i < f.Index + f.Length; i++) fill[i] = true;
        var kept = new char[rest.Length];
        for (int i = 0; i < rest.Length; i++) kept[i] = i < fill.Length && fill[i] ? ' ' : rest[i];

        string title = Regex.Replace(new string(kept), @"\s+", " ");
        title = title.Trim(' ', ',', '.', '!', '?', ';', ':', '-', '\'', '’');
        // Clean dangling punctuation left between removed words ("toplantı ,").
        title = Regex.Replace(title, @"\s+([,.!?;:])", "$1").Trim(' ', ',', '.', ';', ':');
        if (title.Length == 0) title = original.Trim();
        return title.Length == 0 ? "" : char.ToUpper(title[0], Tr) + title[1..];
    }

    static string Normalize(string s) =>
        s.ToLower(Tr).Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'');

    static int ToNumber(string w)
    {
        if (int.TryParse(w, out int n)) return n;
        w = Regex.Replace(w, @"\s+", " ");
        return Numbers.TryGetValue(w, out n) ? n : 0;
    }

    static bool TryDate(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    static DateTime Trim(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);

    /// <summary>"Cumartesi 22 Kasım, 16:00" — how Eule reads the time back to the user.</summary>
    public static string Describe(DateTime dueLocal, bool hasTime, DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        var d = DateOnly.FromDateTime(dueLocal);
        string day = d == today ? "Bugün" : d == today.AddDays(1) ? "Yarın" : dueLocal.ToString("dddd d MMMM", Tr);
        if (d.Year != today.Year) day += " " + d.Year;
        return hasTime ? $"{day}, {dueLocal:HH:mm}" : day;
    }
}
