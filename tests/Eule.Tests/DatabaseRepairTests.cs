using Eule.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Eule.Tests;

public class DatabaseRepairTests
{
    static readonly DateOnly Day = new(2026, 11, 20);

    [Fact]
    public void Healthy_database_opens_without_repair()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.db");
        using (var db = new EuleDatabase(path)) db.Increment(Day, Counter.Breaks);
        using (var db = EuleDatabase.OpenOrRepair(path, Path.Combine(dir.Path, "yedek"), out var repair))
        {
            Assert.Equal(EuleDatabase.Repair.None, repair);
            Assert.Equal(1, db.GetDay(Day).Breaks);
        }
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "yedek")));
    }

    [Fact]
    public void Clean_close_leaves_no_journal_behind()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.db");
        using (var db = new EuleDatabase(path))
            for (int i = 0; i < 50; i++) db.Increment(Day.AddDays(i), Counter.Breaks);
        Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0, "journal left behind");
        using var again = new EuleDatabase(path);
        Assert.Equal(1, again.GetDay(Day.AddDays(49)).Breaks);
    }

    [Fact]
    public void Unreadable_database_is_set_aside_and_a_fresh_one_starts()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.db");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x42, 8192).ToArray()); // not a database at all
        string backup = Path.Combine(dir.Path, "yedek");

        using (var db = EuleDatabase.OpenOrRepair(path, backup, out var repair))
        {
            Assert.Equal(EuleDatabase.Repair.StartedFresh, repair);
            db.Increment(Day, Counter.Breaks); // usable
            Assert.Equal(1, db.GetDay(Day).Breaks);
        }
        Assert.True(File.Exists(Path.Combine(backup, "eule.db")), "old file kept in the backup folder");
    }

    [Fact]
    public void Corrupt_page_inside_a_real_database_is_handled()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "eule.db");
        using (var db = new EuleDatabase(path))
            for (int i = 0; i < 30; i++) db.Increment(Day.AddDays(i), Counter.Breaks);
        SqliteConnection.ClearAllPools();
        // Overwrite the second page (schema data) with garbage, keeping the valid header.
        using (var f = File.OpenWrite(path))
        {
            f.Seek(4096, SeekOrigin.Begin);
            f.Write(Enumerable.Repeat((byte)0xFF, 4096).ToArray());
        }

        using var repaired = EuleDatabase.OpenOrRepair(path, Path.Combine(dir.Path, "yedek"), out var repair);
        Assert.NotEqual(EuleDatabase.Repair.None, repair);
        repaired.Increment(Day, Counter.Breaks);
        Assert.Equal(1, repaired.GetDay(Day).Breaks);
    }
}
