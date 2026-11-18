using System.Text.RegularExpressions;
using Eule.Core.Insights;
using Eule.Core.Localization;
using Eule.Core.Storage;

namespace Eule.Tests;

public class LocalizationTests
{
    [Fact]
    public void Every_key_has_both_languages()
    {
        foreach (var (key, v) in L.All)
        {
            Assert.True(v.Length == 2, key);
            Assert.False(string.IsNullOrWhiteSpace(v[0]), $"{key}: tr");
            Assert.False(string.IsNullOrWhiteSpace(v[1]), $"{key}: en");
        }
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        static string[] Holes(string s) =>
            Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        foreach (var (key, v) in L.All)
            Assert.True(Holes(v[0]).SequenceEqual(Holes(v[1])), $"{key}: placeholders differ");
    }

    [Fact]
    public void Lists_have_seven_days()
    {
        foreach (string lang in L.Languages)
            using (L.Use(lang))
            {
                Assert.Equal(7, Words.DayShort.Length);
                Assert.Equal(7, Words.DayName.Length);
            }
    }

    [Fact]
    public void English_durations_and_insights()
    {
        using (L.Use("en"))
        {
            Assert.Equal("1 h 5 min", Words.Duration(65 * 60));
            var today = new DateOnly(2026, 11, 18);
            var days = new[]
            {
                DailyStats.Empty(today) with { SitSeconds = 300 * 60, Breaks = 4, Skipped = 1 },
                DailyStats.Empty(today.AddDays(-1)) with { SitSeconds = 300 * 60, Breaks = 4 },
            };
            var r = Report.Build(days, new Dictionary<(DateOnly, int), int>(), today, 4, new StreakInfo(0, false, 0, 4));
            Assert.Contains("This week you took 89% of your breaks (8 / 9).", r.Insights);
            Assert.StartsWith("Date;Sitting (min)", CsvExport.Header);
        }
    }

    [Fact]
    public void Unknown_key_falls_back_to_the_key() => Assert.Equal("no.such.key", L.T("no.such.key"));
}
