using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class BackupServiceTests
{
    private static void GiveBackup(TempDir temp, string relative, string file = "thing.dat")
    {
        temp.File($@"sync\.backup\{relative}\{file}", "content");
    }

    [Fact]
    public void ClassifyName_SeparatesCopiesFromThingsThatWereMovedHere()
    {
        // Written with a file copy — the original still exists.
        Assert.Equal(BackupKind.Copy, BackupService.ClassifyName("20260817-063000-manual"));
        Assert.Equal(BackupKind.Copy, BackupService.ClassifyName("20260817-063000-before-friday-coop"));

        // Moved here instead of deleted, so this may be the only instance left.
        Assert.Equal(BackupKind.MovedOriginal, BackupService.ClassifyName("20260817-063000-removed-oldset"));
        Assert.Equal(BackupKind.MovedOriginal, BackupService.ClassifyName("20260817-063000"));
    }

    [Fact]
    public void Scan_FindsBothKindsWithTheirSizes()
    {
        using var temp = new TempDir();
        var service = new BackupService(temp.Dir("sync"), temp.Dir("cfgbackups"));
        GiveBackup(temp, @"saves\20260817-100000-manual");
        GiveBackup(temp, @"saves\20260817-100001-removed-oldset");
        GiveBackup(temp, @"20260817-090000\coop", "displaced-mod.lua");

        var entries = service.Scan();

        Assert.Equal(3, entries.Count);
        Assert.Single(entries, e => e.Kind == BackupKind.Copy);
        Assert.Equal(2, entries.Count(e => e.Kind == BackupKind.MovedOriginal));
        Assert.All(entries, e => Assert.True(e.SizeBytes > 0));
    }

    [Fact]
    public void PlanPrune_NeverIncludesSomethingThatWasMovedHere()
    {
        using var temp = new TempDir();
        var service = new BackupService(temp.Dir("sync"), temp.Dir("cfgbackups"));

        for (var i = 0; i < 5; i++)
        {
            GiveBackup(temp, $@"saves\2026081{i}-100000-removed-set{i}");
            Directory.SetLastWriteTime(temp.Combine("sync", ".backup", "saves", $"2026081{i}-100000-removed-set{i}"),
                                       DateTime.Now.AddDays(-30));
        }

        // Deleting these would destroy the only remaining copy.
        Assert.Empty(service.PlanPrune(keep: 0, minimumAgeDays: 1));
    }

    [Fact]
    public void PlanPrune_KeepsTheNewestAndSparesAnythingRecent()
    {
        using var temp = new TempDir();
        var root = temp.Dir("sync");
        var service = new BackupService(root, temp.Dir("cfgbackups"));

        for (var i = 0; i < 6; i++)
        {
            var name = $"2026080{i}-100000-manual";
            GiveBackup(temp, $@"saves\{name}");
            Directory.SetLastWriteTime(Path.Combine(root, ".backup", "saves", name), DateTime.Now.AddDays(-10 + i));
        }
        // One from today, which must survive regardless of the count.
        GiveBackup(temp, @"saves\20260817-235900-manual");

        var plan = service.PlanPrune(keep: 3, minimumAgeDays: 1);

        Assert.Equal(4, plan.Count);
        Assert.All(plan, e => Assert.True(e.When < DateTime.Now.AddDays(-1)));
        Assert.DoesNotContain(plan, e => e.Name == "20260817-235900-manual");
    }

    [Fact]
    public void Prune_DeletesExactlyWhatItPlanned()
    {
        using var temp = new TempDir();
        var root = temp.Dir("sync");
        var service = new BackupService(root, temp.Dir("cfgbackups"));

        for (var i = 0; i < 4; i++)
        {
            var name = $"2026080{i}-100000-manual";
            GiveBackup(temp, $@"saves\{name}");
            Directory.SetLastWriteTime(Path.Combine(root, ".backup", "saves", name), DateTime.Now.AddDays(-9 + i));
        }

        var planned = service.PlanPrune(keep: 2, minimumAgeDays: 1).Select(e => e.Path).ToList();
        var removed = service.Prune(keep: 2, minimumAgeDays: 1);

        Assert.Equal(planned.Count, removed.Count);
        Assert.All(planned, p => Assert.False(Directory.Exists(p)));
        Assert.Equal(2, service.Scan().Count);
    }

    [Fact]
    public void Scan_IsEmptyWhenNothingHasBeenBackedUp()
    {
        using var temp = new TempDir();
        Assert.Empty(new BackupService(temp.Dir("sync"), temp.Dir("cfgbackups")).Scan());
    }
}

public class LogArchiveServiceTests
{
    /// <summary>Each test archives into its own temp folder, never the real one.</summary>
    private static LogArchiveService ServiceFor(TempDir temp, string contents) =>
        new(new LogReaderService(temp.File("log.txt", contents)), temp.Combine("archives"));

    private const string Log = """
        [INFO] - Command Line:
        [INFO] - 	--repentogonoff
        [INFO] - Game Version: J460
        [INFO] - LOADED MOD a:/x/mods/alpha/content/
        """;

    [Fact]
    public void ArchiveCurrent_CopiesTheLogAndTagsItWithTheBuild()
    {
        using var temp = new TempDir();
            var archived = ServiceFor(temp, Log).ArchiveCurrent();

            Assert.NotNull(archived);
            Assert.Equal("J460", archived!.GameVersion);
            Assert.Contains("Game Version: J460", File.ReadAllText(archived.Path));
            Assert.Contains("J460", archived.Label);
    }

    [Fact]
    public void ArchiveCurrent_SkipsALogIdenticalToTheNewestArchive()
    {
        using var temp = new TempDir();
            var service = ServiceFor(temp, Log);
            Assert.NotNull(service.ArchiveCurrent());

            // Opening the app repeatedly must not fill the folder with duplicates.
            Assert.Null(service.ArchiveCurrent());
            Assert.Single(service.List());
    }

    [Fact]
    public void ArchiveCurrent_KeepsANewRunAlongsideTheOldOne()
    {
        using var temp = new TempDir();
            var path = temp.File("log.txt", Log);
            var service = new LogArchiveService(new LogReaderService(path), temp.Combine("archives"));
            service.ArchiveCurrent();

            File.WriteAllText(path, Log.Replace("J460", "J273"));
            var second = service.ArchiveCurrent();

            Assert.NotNull(second);
            Assert.Equal("J273", second!.GameVersion);
            // Two runs to compare, which is the whole point.
            Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void ArchiveCurrent_WorksWhileTheGameHoldsTheLogOpen()
    {
        using var temp = new TempDir();
            var path = temp.File("log.txt", Log);
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            Assert.NotNull(new LogArchiveService(new LogReaderService(path), temp.Combine("archives")).ArchiveCurrent());
    }

    [Fact]
    public void Prune_KeepsTheNewestAndDropsTheRest()
    {
        using var temp = new TempDir();
            var path = temp.File("log.txt", Log);
            var service = new LogArchiveService(new LogReaderService(path), temp.Combine("archives"));

            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(path, Log + $"\n[INFO] - run {i}");
                File.SetLastWriteTime(path, DateTime.Now.AddMinutes(i));
                service.ArchiveCurrent();
            }
            Assert.Equal(5, service.List().Count);

            var dropped = service.Prune(keep: 2);

            Assert.Equal(3, dropped.Count);
            Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void NoLogMeansNothingToArchive()
    {
        using var temp = new TempDir();
        Assert.Null(new LogArchiveService(new LogReaderService(temp.Combine("absent.txt")), temp.Combine("archives")).ArchiveCurrent());
    }
}
