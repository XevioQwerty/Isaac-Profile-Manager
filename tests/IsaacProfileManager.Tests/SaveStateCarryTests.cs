using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// A save set carries the per-slot state the game keeps outside the save
/// folder, files its previous revision before every overwrite, and stamps
/// which device made it. All against a temp tree.
/// </summary>
public class SaveStateCarryTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private const string Account = "351019201";

    private sealed record Fixture(SaveSetService Service, FakeProcessService Process, string Remote, string Data, string Rgon, string Log);

    private static Fixture Build(TempDir temp, string device = "desktop1", bool keepHistory = true)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");
        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf", """
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
            						"cloudenabled"		"0"
            					}
            				}
            			}
            		}
            	}
            }
            """);

        var game = temp.Dir("Game");
        var data = temp.Dir("Game", "data");
        var rgon = temp.Dir("Docs", "Repentogon");
        var log = temp.File(@"Docs\log.txt", "[INFO] - hello\n[INFO] - Game Version: J460 \n");

        var process = new FakeProcessService();
        var options = new SaveSetOptions
        {
            RepentogonStateFolder = rgon,
            DeviceId = device,
            DeviceName = device,
            ReadGameVersion = () => LogReaderService.ReadGameVersion(log),
            KeepHistory = keepHistory,
        };

        var service = new SaveSetService(process, new SteamCloudService(steam), temp.Dir("sync"), null, game, options);
        return new Fixture(service, process, remote, data, rgon, log);
    }

    private static void GiveLiveSaves(Fixture f, string tag = "v1")
    {
        File.WriteAllText(Path.Combine(f.Remote, "rgon_steam_persistentgamedata1.dat"), $"rgon slot 1 {tag}");
        File.WriteAllText(Path.Combine(f.Remote, "rgon_savesyncstatus.json"), "{}");
        File.WriteAllText(Path.Combine(f.Remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");
    }

    private static void GiveModData(Fixture f, string mod, int slot, string content)
    {
        Directory.CreateDirectory(Path.Combine(f.Data, mod));
        File.WriteAllText(Path.Combine(f.Data, mod, $"save{slot}.dat"), content);
    }

    private static void GiveRepentogonState(Fixture f, int slot, string content)
    {
        File.WriteAllText(Path.Combine(f.Rgon, $"achievements{slot}.json"), content);
        File.WriteAllText(Path.Combine(f.Rgon, $"completionmarks{slot}.json"), content);
    }

    // --- Capture carries state ---------------------------------------------

    [Fact]
    public void Capture_CarriesModDataAndRepentogonState_ForTheCapturedSlotsOnly()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);
        GiveModData(f, "eid", 1, "eid slot 1");
        GiveModData(f, "eid", 2, "eid slot 2");        // slot 2 is not live, so not captured
        GiveModData(f, "minimapi", 1, "map slot 1");
        GiveRepentogonState(f, 1, "{\"a\":1}");
        GiveRepentogonState(f, 3, "{\"a\":3}");

        var set = f.Service.Capture("friday", "RPTG");

        Assert.True(set.ModDataCaptured);
        Assert.Equal(new[] { "moddata/eid/save1.dat", "moddata/minimapi/save1.dat" }, set.ModData.Keys.OrderBy(k => k));
        Assert.True(set.RepentogonStateCaptured);
        Assert.Equal(new[] { "repentogon/achievements1.json", "repentogon/completionmarks1.json" }, set.RepentogonState.Keys.OrderBy(k => k));

        var folder = f.Service.SetFolder("friday");
        Assert.Equal("eid slot 1", File.ReadAllText(Path.Combine(folder, "moddata", "eid", "save1.dat")));
        Assert.False(File.Exists(Path.Combine(folder, "moddata", "eid", "save2.dat")));
        Assert.True(File.Exists(Path.Combine(folder, "repentogon", "achievements1.json")));
    }

    [Fact]
    public void Capture_StampsDeviceClockAndGameVersion()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);

        var set = f.Service.Capture("friday", "RPTG");

        Assert.Equal("desktop1", set.Device);
        Assert.Equal(1, set.Clock["desktop1"]);
        Assert.Equal("J460", set.GameVersion);

        var reloaded = f.Service.LoadSet("friday")!;
        Assert.Equal(1, reloaded.Clock["desktop1"]);
        Assert.Equal("J460", reloaded.GameVersion);
    }

    [Fact]
    public void Capture_WithoutModDataFolder_RecordsThatItNeverLooked()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);
        Directory.Delete(f.Data);

        var set = f.Service.Capture("friday", "RPTG");

        Assert.False(set.ModDataCaptured);
        Assert.Empty(set.ModData);
    }

    // --- Activation restores state -----------------------------------------

    [Fact]
    public void Activate_RestoresCarriedState_AndBacksUpWhatWasLive()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "captured");
        GiveModData(f, "eid", 1, "eid at capture");
        GiveRepentogonState(f, 1, "rgon at capture");
        var set = f.Service.Capture("friday", "RPTG");

        // Play on: everything moves, and a new mod gains data the set never saw.
        GiveLiveSaves(f, "later");
        GiveModData(f, "eid", 1, "eid later");
        GiveModData(f, "newmod", 1, "new mod later");
        GiveRepentogonState(f, 1, "rgon later");

        var result = f.Service.ActivateSet(set, GameBuild.Repentogon);

        Assert.Equal("eid at capture", File.ReadAllText(Path.Combine(f.Data, "eid", "save1.dat")));
        Assert.Equal("rgon at capture", File.ReadAllText(Path.Combine(f.Rgon, "achievements1.json")));

        // Slot 1's state is replaced wholesale, so the newer mod's slot-1 data goes too — into the backup.
        Assert.False(File.Exists(Path.Combine(f.Data, "newmod", "save1.dat")));
        Assert.Equal("new mod later", File.ReadAllText(Path.Combine(result.Backup, "moddata", "newmod", "save1.dat")));
        Assert.Equal("eid later", File.ReadAllText(Path.Combine(result.Backup, "moddata", "eid", "save1.dat")));
        Assert.Equal("rgon later", File.ReadAllText(Path.Combine(result.Backup, "repentogon", "completionmarks1.json")));

        Assert.NotNull(result.ModData);
        Assert.Equal(1, result.ModData!.Restored);
        Assert.Equal(2, result.ModData.Removed);
    }

    [Fact]
    public void Activate_LeavesLiveModDataAlone_ForASetThatNeverCapturedIt()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "captured");
        var set = f.Service.Capture("legacy", "RPTG");

        // Make it look like a 1.x set: it never looked at mod data.
        set.ModDataCaptured = false;
        set.ModData.Clear();
        set.RepentogonStateCaptured = false;
        set.RepentogonState.Clear();
        f.Service.SaveSetMetadata(set);

        GiveModData(f, "eid", 1, "settings I would miss");
        GiveRepentogonState(f, 1, "marks I would miss");

        var result = f.Service.ActivateSet(set, GameBuild.Repentogon);

        Assert.Null(result.ModData);
        Assert.Null(result.RepentogonState);
        Assert.Equal("settings I would miss", File.ReadAllText(Path.Combine(f.Data, "eid", "save1.dat")));
        Assert.Equal("marks I would miss", File.ReadAllText(Path.Combine(f.Rgon, "achievements1.json")));
    }

    [Fact]
    public void Activate_OnlyTouchesSlotsTheSetHolds()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);
        GiveModData(f, "eid", 1, "slot 1");
        var set = f.Service.Capture("friday", "RPTG");

        GiveModData(f, "eid", 2, "slot 2 belongs to someone else");

        f.Service.ActivateSet(set, GameBuild.Repentogon);

        Assert.Equal("slot 2 belongs to someone else", File.ReadAllText(Path.Combine(f.Data, "eid", "save2.dat")));
    }

    [Fact]
    public void RestoreBackup_PutsCarriedStateBack_WhenTheBackupHasIt()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "one");
        GiveModData(f, "eid", 1, "eid one");
        var set = f.Service.Capture("friday", "RPTG");

        GiveLiveSaves(f, "two");
        GiveModData(f, "eid", 1, "eid two");
        var activation = f.Service.ActivateSet(set, GameBuild.Repentogon);   // backs up "two", restores "one"
        Assert.Equal("eid one", File.ReadAllText(Path.Combine(f.Data, "eid", "save1.dat")));

        f.Service.RestoreBackup(Path.GetFileName(activation.Backup));

        Assert.Equal("eid two", File.ReadAllText(Path.Combine(f.Data, "eid", "save1.dat")));
        Assert.Equal("rgon slot 1 two", File.ReadAllText(Path.Combine(f.Remote, "rgon_steam_persistentgamedata1.dat")));
    }

    // --- History -------------------------------------------------------------

    [Fact]
    public void CaptureInto_FilesThePreviousRevision_BeforeOverwriting()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "first");
        GiveModData(f, "eid", 1, "eid first");
        var set = f.Service.Capture("friday", "RPTG");

        GiveLiveSaves(f, "second");
        GiveModData(f, "eid", 1, "eid second");
        var updated = f.Service.CaptureInto(set);

        var history = f.Service.ListHistory("friday");
        var entry = Assert.Single(history);
        Assert.Equal(entry.Name, updated.ParentRevision);
        Assert.EndsWith("-desktop1", entry.Name);
        Assert.Equal("desktop1", entry.Device);
        Assert.Equal(1, entry.Revision);
        Assert.Equal(2, updated.Clock["desktop1"]);

        Assert.Equal("rgon slot 1 first", File.ReadAllText(Path.Combine(entry.Path, "rgon_steam_persistentgamedata1.dat")));
        Assert.Equal("eid first", File.ReadAllText(Path.Combine(entry.Path, "moddata", "eid", "save1.dat")));
        Assert.True(File.Exists(Path.Combine(entry.Path, "set.json")));

        // And the set itself now holds the second capture, with no history nested inside history.
        Assert.Equal("eid second", File.ReadAllText(Path.Combine(f.Service.SetFolder("friday"), "moddata", "eid", "save1.dat")));
        Assert.False(Directory.Exists(Path.Combine(entry.Path, SaveSetService.HistoryFolderName)));
    }

    [Fact]
    public void CaptureInto_AnEmptySet_FilesNothing()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        var set = f.Service.CreateEmpty("fresh", GameBuild.Repentogon, "RPTG");

        GiveLiveSaves(f);
        f.Service.CaptureInto(set);

        Assert.Empty(f.Service.ListHistory("fresh"));
    }

    [Fact]
    public void RestoreHistory_BringsARevisionBack_AndFilesTheCurrentOneFirst()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "first");
        var set = f.Service.Capture("friday", "RPTG");
        GiveLiveSaves(f, "second");
        f.Service.CaptureInto(set);

        var first = Assert.Single(f.Service.ListHistory("friday"));
        var restored = f.Service.RestoreHistory("friday", first.Name);

        Assert.Equal("rgon slot 1 first", File.ReadAllText(Path.Combine(f.Service.SetFolder("friday"), "rgon_steam_persistentgamedata1.dat")));
        Assert.Equal(first.Name, restored.ParentRevision);
        Assert.Equal(3, restored.Clock["desktop1"]);
        Assert.Equal("friday", restored.Name);

        // "second" was filed before being replaced, so nothing was lost.
        var entries = f.Service.ListHistory("friday");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => File.ReadAllText(Path.Combine(e.Path, "rgon_steam_persistentgamedata1.dat")) == "rgon slot 1 second");
    }

    [Fact]
    public void RestoreHistory_RefusesAPathOutsideTheSetsHistory()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);
        f.Service.Capture("friday", "RPTG");

        Assert.Throws<UnsafePathException>(() => f.Service.RestoreHistory("friday", @"..\..\friday"));
    }

    [Fact]
    public void PruneHistory_KeepsTheNewest()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f, "0");
        var set = f.Service.Capture("friday", "RPTG");
        for (var i = 1; i <= 3; i++)
        {
            GiveLiveSaves(f, i.ToString());
            f.Service.CaptureInto(set);
        }

        Assert.Equal(3, f.Service.ListHistory("friday").Count);
        var pruned = f.Service.PruneHistory("friday", keep: 1);

        Assert.Equal(2, pruned.Count);
        var kept = Assert.Single(f.Service.ListHistory("friday"));
        Assert.Equal("rgon slot 1 2", File.ReadAllText(Path.Combine(kept.Path, "rgon_steam_persistentgamedata1.dat")));
    }

    [Fact]
    public void HistoryCanBeTurnedOff()
    {
        using var temp = new TempDir();
        var f = Build(temp, keepHistory: false);
        GiveLiveSaves(f, "first");
        var set = f.Service.Capture("friday", "RPTG");
        GiveLiveSaves(f, "second");
        f.Service.CaptureInto(set);

        Assert.Empty(f.Service.ListHistory("friday"));
        Assert.Null(set.ParentRevision);
    }

    // --- Compatibility ------------------------------------------------------

    [Fact]
    public void AOnePointXSet_StillLoads_AndReportsNothingCarried()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        var folder = temp.Dir("sync", ".saves", "old");
        File.WriteAllText(Path.Combine(folder, "set.json"), """
            {
              "SchemaVersion": 1,
              "Name": "old",
              "Build": "Vanilla",
              "ModProfile": "Vanilla+",
              "Players": [],
              "Notes": "",
              "SlotNotes": {},
              "Files": [ "rep+persistentgamedata1.dat" ],
              "Slots": [ 1 ],
              "Sha1": { "rep+persistentgamedata1.dat": "abc" },
              "CapturedUtc": "2026-08-17T10:30:47Z",
              "SomethingFromTheFuture": 42
            }
            """);

        var set = f.Service.LoadSet("old")!;

        Assert.False(set.ModDataCaptured);
        Assert.Empty(set.Clock);
        Assert.Null(set.GameVersion);
        Assert.True(set.Extra.ContainsKey("SomethingFromTheFuture"));

        // Writing it back keeps the unknown key.
        f.Service.SaveSetMetadata(set);
        Assert.Contains("SomethingFromTheFuture", File.ReadAllText(Path.Combine(folder, "set.json")));
    }

    // --- Drift in carried files ---------------------------------------------

    [Fact]
    public void DetectCarriedDrift_ReportsChangedAndMissingModData()
    {
        using var temp = new TempDir();
        var f = Build(temp);
        GiveLiveSaves(f);
        GiveModData(f, "eid", 1, "one");
        GiveModData(f, "minimapi", 1, "map");
        var set = f.Service.Capture("friday", "RPTG");

        Assert.Empty(f.Service.DetectCarriedDrift(set));

        GiveModData(f, "eid", 1, "two");
        File.Delete(Path.Combine(f.Data, "minimapi", "save1.dat"));

        Assert.Equal(new[] { "moddata/eid/save1.dat", "moddata/minimapi/save1.dat" }, f.Service.DetectCarriedDrift(set));
    }
}
