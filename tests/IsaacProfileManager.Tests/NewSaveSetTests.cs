using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Starting a save set from nothing: the "vanilla online vs vanilla solo" case,
/// where the point is a fresh unlock state rather than a copy of an existing one.
/// </summary>
public class NewSaveSetTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private const string Account = "351019201";

    private static (SaveSetService Service, FakeProcessService Process, string Remote) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");

        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf",
            "\"UserRoamingConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n" +
            "\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t\t\"250900\"\n\t\t\t\t\t{\n" +
            "\t\t\t\t\t\t\"cloudenabled\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}");

        var process = new FakeProcessService();
        return (new SaveSetService(process, new SteamCloudService(steam), temp.Dir("sync")), process, remote);
    }

    private static void GiveVanillaSave(string remote, string contents = "vanilla slot 1")
    {
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), contents);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");
    }

    [Fact]
    public void CreateEmpty_MakesASetWithNoFilesButAKnownBuild()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);

        var set = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");

        Assert.Empty(set.Files);
        Assert.Empty(set.Slots);
        Assert.Equal(GameBuild.Vanilla, set.Build);
        Assert.Contains("vanilla-solo", service.ListSets());
    }

    [Fact]
    public void CreateEmpty_RefusesWithoutABuild()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);

        // Nothing to derive it from, and an Unknown build is refused at
        // activation anyway - better to say so at the point of creation.
        Assert.Throws<ArgumentException>(() =>
            service.CreateEmpty("nameless", GameBuild.Unknown, "my-mods"));
    }

    [Fact]
    public void CreateEmpty_RefusesToOverwriteAnExistingSet()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveVanillaSave(remote);
        service.Capture("existing", "my-mods");

        Assert.Throws<UnsafePathException>(() =>
            service.CreateEmpty("existing", GameBuild.Vanilla, "my-mods"));
    }

    [Fact]
    public void ActivatingAnEmptySet_ClearsTheLiveSavesSoTheGameMakesNewOnes()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveVanillaSave(remote);

        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");
        service.Activate(fresh, GameBuild.Vanilla);

        Assert.False(File.Exists(Path.Combine(remote, "rep+persistentgamedata1.dat")));

        // Steam's manifest is not ours and must survive.
        Assert.True(File.Exists(Path.Combine(remote, "remotecache.vdf")));
    }

    [Fact]
    public void ActivatingAnEmptySet_BacksUpWhatWasThereFirst()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveVanillaSave(remote, "the run I already had");

        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");
        var backup = service.Activate(fresh, GameBuild.Vanilla);

        Assert.True(File.Exists(Path.Combine(backup, "rep+persistentgamedata1.dat")));
        Assert.Equal("the run I already had",
                     File.ReadAllText(Path.Combine(backup, "rep+persistentgamedata1.dat")));
    }

    [Fact]
    public void CaptureInto_AdoptsWhatTheGameGenerated()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);

        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");

        // The game has now been launched once and written a save.
        GiveVanillaSave(remote, "freshly generated");
        var filled = service.CaptureInto(fresh);

        Assert.Equal(new[] { "rep+persistentgamedata1.dat" }, filled.Files);
        Assert.Equal(new[] { 1 }, filled.Slots);
        Assert.Equal(GameBuild.Vanilla, filled.Build);

        var stored = service.LoadSet("vanilla-solo")!;
        Assert.Equal(new[] { "rep+persistentgamedata1.dat" }, stored.Files);
    }

    [Fact]
    public void CaptureInto_RefusesWhenTheLiveFolderIsStillEmpty()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);
        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");

        var error = Assert.Throws<UnsafePathException>(() => service.CaptureInto(fresh));
        Assert.Contains("Launch the game", error.Message);
    }

    [Fact]
    public void CaptureInto_RefusesSavesFromTheOtherBuild()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");

        // REPENTOGON saves under a set that claims to be vanilla is exactly the
        // mislabelling the cross-build check exists to prevent.
        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "rgon");

        Assert.Throws<UnsafePathException>(() => service.CaptureInto(fresh));
    }

    [Fact]
    public void CaptureInto_ReplacesRatherThanMergesTheStoredFiles()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);

        GiveVanillaSave(remote);
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata2.dat"), "slot 2");
        var set = service.Capture("vanilla-solo", "my-mods");
        Assert.Equal(2, set.Files.Count);

        // Slot 2 is gone from live; it must not survive in the set.
        File.Delete(Path.Combine(remote, "rep+persistentgamedata2.dat"));
        var recaptured = service.CaptureInto(set);

        Assert.Equal(new[] { "rep+persistentgamedata1.dat" }, recaptured.Files);
        Assert.False(File.Exists(Path.Combine(temp.Path, "sync", ".saves", "vanilla-solo",
                                              "rep+persistentgamedata2.dat")));
    }

    // --- Deleting a backup --------------------------------------------------

    [Fact]
    public void DeleteBackup_RemovesOnlyThatBackup()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveVanillaSave(remote);

        var first = Path.GetFileName(service.BackupLive("one"));
        System.Threading.Thread.Sleep(1100);   // the folder name is stamped to the second
        var second = Path.GetFileName(service.BackupLive("two"));

        service.DeleteBackup(first);

        Assert.DoesNotContain(first, service.ListBackups());
        Assert.Contains(second, service.ListBackups());
    }

    [Fact]
    public void DeleteBackup_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp);
        GiveVanillaSave(remote);
        var name = Path.GetFileName(service.BackupLive("one"));

        // A backup taken seconds ago may hold the only copy of the run in play.
        process.Running = true;
        Assert.Throws<UnsafePathException>(() => service.DeleteBackup(name));
        Assert.Contains(name, service.ListBackups());
    }

    [Fact]
    public void DeleteBackup_RefusesAnythingOutsideTheBackupFolder()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);

        Assert.ThrowsAny<Exception>(() => service.DeleteBackup(@"..\..\sync"));
    }

    [Fact]
    public void MeasureBackup_ReportsWhatItHolds()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveVanillaSave(remote);
        var name = Path.GetFileName(service.BackupLive("one"));

        var (files, bytes) = service.MeasureBackup(name);

        Assert.Equal(1, files);
        Assert.True(bytes > 0);
    }

    [Fact]
    public void CaptureInto_RefusesWhileIsaacIsRunningIsNotItsJob_ButActivateStillChecks()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp);
        GiveVanillaSave(remote);
        var fresh = service.CreateEmpty("vanilla-solo", GameBuild.Vanilla, "my-mods");

        process.Running = true;

        // Activating replaces files under a live game; capturing only reads them.
        Assert.Throws<UnsafePathException>(() => service.Activate(fresh, GameBuild.Vanilla));
    }
}
