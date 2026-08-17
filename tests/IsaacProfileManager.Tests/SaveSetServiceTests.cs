using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SaveSetServiceTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private const string Account = "351019201";

    /// <summary>Builds a Steam tree with the save folder and the cloud setting we want.</summary>
    private static (SaveSetService Service, FakeProcessService Process, string Remote) Build(
        TempDir temp, string cloudEnabled = "0")
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");

        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf", $$"""
            "UserRoamingConfigStore"
            {
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"apps"
            				{
            					"250900"
            					{
            						"cloudenabled"		"{{cloudEnabled}}"
            					}
            				}
            			}
            		}
            	}
            }
            """);

        var process = new FakeProcessService();
        return (new SaveSetService(process, new SteamCloudService(steam), temp.Dir("sync")), process, remote);
    }

    private static void GiveLiveSaves(string remote, bool repentogon = true, bool vanilla = true)
    {
        if (repentogon)
        {
            File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "rgon slot 1");
            File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata2.dat"), "rgon slot 2");
            File.WriteAllText(Path.Combine(remote, "rgon_savesyncstatus.json"),
                              """{"AutoSyncingEnabled":true,"Checksums":{"REPENTOGON.1":1}}""");
        }
        if (vanilla) File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "vanilla slot 1");

        // Steam's own manifest lives here too and must never be captured or deleted.
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");
    }

    // --- Classification -----------------------------------------------------

    [Fact]
    public void IsSaveFile_RecognisesBothBuildsAndTheSyncStatus_AndNothingElse()
    {
        Assert.True(SaveSetService.IsSaveFile("rgon_steam_persistentgamedata1.dat"));
        Assert.True(SaveSetService.IsSaveFile("rep+persistentgamedata1.dat"));
        Assert.True(SaveSetService.IsSaveFile("rgon_savesyncstatus.json"));

        // Steam's manifest and anything else in that folder is not ours.
        Assert.False(SaveSetService.IsSaveFile("remotecache.vdf"));
        Assert.False(SaveSetService.IsSaveFile("something.txt"));
    }

    [Fact]
    public void BuildOf_ReadsTheBuildFromTheFilenamePrefixes()
    {
        Assert.Equal(GameBuild.Repentogon, SaveSetService.BuildOf(new[] { "rgon_steam_persistentgamedata1.dat" }));
        Assert.Equal(GameBuild.Vanilla, SaveSetService.BuildOf(new[] { "rep+persistentgamedata1.dat" }));
        Assert.Equal(GameBuild.Both, SaveSetService.BuildOf(new[] { "rgon_steam_persistentgamedata1.dat", "rep+persistentgamedata1.dat" }));
        Assert.Equal(GameBuild.Unknown, SaveSetService.BuildOf(Array.Empty<string>()));
    }

    [Fact]
    public void SlotsOf_ReadsSlotNumbersAndIgnoresNonSlotFiles()
    {
        var slots = SaveSetService.SlotsOf(new[]
        {
            "rgon_steam_persistentgamedata1.dat",
            "rgon_steam_persistentgamedata3.dat",
            "rep+persistentgamedata1.dat",
            "rgon_savesyncstatus.json",
        });

        Assert.Equal(new[] { 1, 3 }, slots);
    }

    // --- Capture ------------------------------------------------------------

    [Fact]
    public void Capture_CopiesTheLiveSavesAndRecordsWhatTheyAre()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);

        var set = service.Capture("friday-coop", "RPTG_v1.0", new[] { "alex" }, "the good run");

        Assert.Equal(GameBuild.Both, set.Build);
        Assert.Equal(new[] { 1, 2 }, set.Slots);
        Assert.Equal("RPTG_v1.0", set.ModProfile);
        Assert.Contains("alex", set.Players);
        Assert.Equal(4, set.Files.Count);

        var folder = Path.Combine(service.SetsRoot, "friday-coop");
        Assert.True(File.Exists(Path.Combine(folder, "rgon_steam_persistentgamedata1.dat")));
        // REPENTOGON reconciles against this; without it a restore is unsafe.
        Assert.True(File.Exists(Path.Combine(folder, "rgon_savesyncstatus.json")));
        // Steam's manifest is not ours to copy.
        Assert.False(File.Exists(Path.Combine(folder, "remotecache.vdf")));
    }

    [Fact]
    public void Capture_IsReadOnlyTowardsTheLiveFolder()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        var before = Directory.GetFiles(remote).Length;

        service.Capture("snapshot", "profile");

        Assert.Equal(before, Directory.GetFiles(remote).Length);
        Assert.Equal("rgon slot 1", File.ReadAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat")));
    }

    [Fact]
    public void Capture_RefusesADuplicateNameOrAnEmptyFolder()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        service.Capture("one", "p");

        Assert.Throws<UnsafePathException>(() => service.Capture("one", "p"));
        Assert.Throws<ArgumentException>(() => service.Capture(@"bad\name", "p"));

        foreach (var f in Directory.GetFiles(remote)) File.Delete(f);
        Assert.Throws<UnsafePathException>(() => service.Capture("two", "p"));
    }

    // --- Preconditions ------------------------------------------------------

    [Fact]
    public void Check_PassesWhenIsaacIsClosedAndCloudIsOff()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp, cloudEnabled: "0");
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");

        var checks = service.Check(set, GameBuild.Both);

        Assert.True(checks.CanActivate);
        Assert.Empty(checks.Blockers);
    }

    [Fact]
    public void Check_BlocksWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");
        process.Running = true;

        var checks = service.Check(set, GameBuild.Both);

        Assert.False(checks.CanActivate);
        Assert.Contains(checks.Blockers, b => b.Contains("Isaac is running"));
    }

    [Fact]
    public void Check_BlocksWhileSteamCloudIsOnForTheGame()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp, cloudEnabled: "1");
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");

        var checks = service.Check(set, GameBuild.Both);

        Assert.False(checks.CanActivate);
        Assert.Equal(SteamCloudState.Enabled, checks.CloudState);
        Assert.Contains(checks.Blockers, b => b.Contains("Steam Cloud is on"));
    }

    [Fact]
    public void AcknowledgingCloudUnblocksIt_BecauseSteamsFileCanLagBehindItsOwnDialog()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp, cloudEnabled: "1");
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");

        Assert.False(service.Check(set, GameBuild.Both).CanActivate);
        Assert.True(service.Check(set, GameBuild.Both, cloudAcknowledged: true).CanActivate);
    }

    [Fact]
    public void AcknowledgingCloudDoesNotUnblockAnythingElse()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp, cloudEnabled: "1");
        GiveLiveSaves(remote, repentogon: true, vanilla: false);
        var set = service.Capture("rgon", "p");
        process.Running = true;

        // The acknowledgement covers Steam Cloud only — never the game running,
        // and never a cross-build load.
        var checks = service.Check(set, GameBuild.Vanilla, cloudAcknowledged: true);

        Assert.False(checks.CanActivate);
        Assert.Contains(checks.Blockers, b => b.Contains("Isaac is running"));
        Assert.Contains(checks.Blockers, b => b.Contains("destroy every achievement"));
        Assert.Throws<UnsafePathException>(() => service.Activate(set, GameBuild.Vanilla, cloudAcknowledged: true));
    }

    [Fact]
    public void Check_BlocksACrossBuildLoadRatherThanWarning()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote, repentogon: true, vanilla: false);
        var set = service.Capture("rgon-only", "p");
        Assert.Equal(GameBuild.Repentogon, set.Build);

        var checks = service.Check(set, GameBuild.Vanilla);

        Assert.False(checks.CanActivate);
        Assert.Contains(checks.Blockers, b => b.Contains("destroy every achievement"));
    }

    [Fact]
    public void Check_AllowsASetCoveringBothBuildsEitherWay()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("both", "p");

        Assert.True(service.Check(set, GameBuild.Vanilla).CanActivate);
        Assert.True(service.Check(set, GameBuild.Repentogon).CanActivate);
    }

    // --- Activation ---------------------------------------------------------

    [Fact]
    public void Activate_BacksUpTheLiveSavesThenReplacesThem()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("original", "p");

        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "played since");

        var backup = service.Activate(set, GameBuild.Both);

        Assert.Equal("rgon slot 1", File.ReadAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat")));
        // The overwritten progress is recoverable.
        Assert.Equal("played since", File.ReadAllText(Path.Combine(backup, "rgon_steam_persistentgamedata1.dat")));
        Assert.NotNull(service.LoadSet("original")!.LastUsedUtc);
    }

    [Fact]
    public void Activate_LeavesSteamsOwnFilesInThatFolderAlone()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");

        service.Activate(set, GameBuild.Both);

        Assert.True(File.Exists(Path.Combine(remote, "remotecache.vdf")));
    }

    [Fact]
    public void Activate_RefusesWhenAnyPreconditionFails_AndChangesNothing()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");
        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "live progress");
        process.Running = true;

        Assert.Throws<UnsafePathException>(() => service.Activate(set, GameBuild.Both));

        Assert.Equal("live progress", File.ReadAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat")));
        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public void DetectDrift_NamesFilesChangedSinceTheSetWasCaptured()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        var set = service.Capture("s", "p");

        Assert.Empty(service.DetectDrift(set));

        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "progress since capture");
        Assert.Equal(new[] { "rgon_steam_persistentgamedata1.dat" }, service.DetectDrift(set));
    }

    [Fact]
    public void RestoreBackup_PutsASnapshotBackAndSafeguardsWhatWasThere()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);

        var snapshot = Path.GetFileName(service.BackupLive("manual"));
        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "later state");

        var safety = service.RestoreBackup(snapshot);

        Assert.Equal("rgon slot 1", File.ReadAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat")));
        Assert.Equal("later state", File.ReadAllText(Path.Combine(safety, "rgon_steam_persistentgamedata1.dat")));
    }

    [Fact]
    public void BackupLive_TwiceInTheSameSecondKeepsBothRatherThanThrowing()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);

        var first = service.BackupLive("manual");
        var second = service.BackupLive("manual");

        Assert.NotEqual(first, second);
        Assert.Equal(2, service.ListBackups().Count);
        Assert.True(File.Exists(Path.Combine(second, "rgon_steam_persistentgamedata1.dat")));
    }

    [Fact]
    public void RestoreBackup_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, process, remote) = Build(temp);
        GiveLiveSaves(remote);
        var snapshot = Path.GetFileName(service.BackupLive("manual"));
        process.Running = true;

        Assert.Throws<UnsafePathException>(() => service.RestoreBackup(snapshot));
    }

    [Fact]
    public void LoadSet_RefusesAnUnknownSchema()
    {
        using var temp = new TempDir();
        var (service, _, remote) = Build(temp);
        GiveLiveSaves(remote);
        service.Capture("s", "p");

        var path = Path.Combine(service.SetsRoot, "s", SaveSetService.MetadataFileName);
        File.WriteAllText(path, """{"SchemaVersion": 99, "Name": "s"}""");

        Assert.Throws<ConfigSchemaMismatchException>(() => service.LoadSet("s"));
    }
}
