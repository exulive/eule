namespace Eule.Core.Storage;

/// <summary>Four weeks of believable, deterministic usage for trying the statistics screen. Developer use only.</summary>
public static class DemoData
{
    public static void Seed(EuleDatabase db, DateOnly today)
    {
        var rng = new Random(42);
        for (int i = 27; i >= 1; i--)
        {
            var date = today.AddDays(-i);
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend && rng.NextDouble() < 0.7) continue;

            var buckets = new List<KeyValuePair<(DateOnly, int), int>>();
            int start = weekend ? 11 : 8 + rng.Next(2), end = weekend ? 14 : 17 + rng.Next(3);
            int total = 0;
            for (int h = start; h <= end; h++)
            {
                double lunch = h == 12 ? 0.35 : 1;
                double afternoon = h is >= 14 and <= 16 && date.DayOfWeek == DayOfWeek.Tuesday ? 1.15 : 1;
                int secs = (int)(Math.Min(60, (35 + rng.Next(25)) * lunch * afternoon) * 60);
                buckets.Add(new((date, h), secs));
                total += secs;
            }
            db.AddSitting(buckets);

            int breaks = Math.Max(0, total / 3600 - rng.Next(3));
            db.AddDaily(DailyStats.Empty(date) with
            {
                Breaks = breaks, Skipped = rng.Next(3), Snoozed = rng.Next(4), AwayBreaks = rng.Next(2),
                EyeBreaks = rng.Next(6),
            });
            db.RecordStretch(date, (40 + rng.Next(70)) * 60);
        }
    }
}
