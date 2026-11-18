using Eule.Core.Storage;

namespace Eule.Tests;

public class StreakTests
{
    static readonly DateOnly Today = new(2026, 11, 18); // Wednesday

    static DailyStats Day(int daysAgo, int breaks, int sitMinutes = 240, int emergency = 0) =>
        DailyStats.Empty(Today.AddDays(-daysAgo)) with
        {
            Breaks = breaks, SitSeconds = sitMinutes * 60, EmergencyExits = emergency,
        };

    [Fact]
    public void Counts_consecutive_met_days()
    {
        var s = Streak.Compute([Day(1, 4), Day(2, 5), Day(3, 1)], Day(0, 0, 10), goal: 4);
        Assert.Equal(2, s.Days);
        Assert.False(s.TodayMet);
    }

    [Fact]
    public void Today_counts_once_met()
    {
        var s = Streak.Compute([Day(1, 4)], Day(0, 4), goal: 4);
        Assert.Equal(2, s.Days);
        Assert.True(s.TodayMet);
    }

    [Fact]
    public void Unfinished_today_does_not_break_the_streak()
    {
        var s = Streak.Compute([Day(1, 4), Day(2, 4)], Day(0, 1), goal: 4);
        Assert.Equal(2, s.Days);
    }

    [Fact]
    public void Weekend_without_computer_is_skipped()
    {
        // Mon and Fri met, Sat/Sun almost no sitting, today (Wed) not counted yet.
        var s = Streak.Compute([Day(1, 4), Day(2, 4), Day(3, 0, 5), Day(4, 0, 0), Day(5, 4)], Day(0, 0, 0), goal: 4);
        Assert.Equal(3, s.Days);
    }

    [Fact]
    public void Missing_rows_count_as_rest()
    {
        var s = Streak.Compute([Day(1, 4), Day(5, 4)], Day(0, 4), goal: 4);
        Assert.Equal(3, s.Days);
    }

    [Fact]
    public void Emergency_exit_spoils_the_day()
    {
        var s = Streak.Compute([Day(1, 6, emergency: 1), Day(2, 4)], Day(0, 4), goal: 4);
        Assert.Equal(1, s.Days);
    }

    [Fact]
    public void Away_breaks_count_towards_the_goal()
    {
        var today = Day(0, 2) with { AwayBreaks = 2 };
        Assert.True(Streak.Compute([], today, goal: 4).TodayMet);
    }

    [Fact]
    public void Works_from_the_database()
    {
        using var db = new EuleDatabase(":memory:");
        foreach (var d in new[] { Day(1, 4), Day(2, 4) }) db.AddDaily(d);
        Assert.Equal(2, Streak.Compute(db, Day(0, 0, 0), goal: 4).Days);
    }
}
