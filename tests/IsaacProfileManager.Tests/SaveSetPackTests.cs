using System.Buffers.Binary;
using System.Text;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>A set travels as one file, and a save file from elsewhere can be put into a slot.</summary>
public class SaveSetPackTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    private const string Account = "351019201";

    private static (SaveSetService Service, string Remote, string Data) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");
        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } } }
            """);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");
        var game = temp.Dir("Game");
        var data = temp.Dir("Game", "data");
        var service = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync"), null, game,
                                         new SaveSetOptions { DeviceId = "desk0001" });
        return (service, remote, data);
    }

    /// <summary>A minimal file the parser accepts: magic, u32, one achievements chunk, bestiary header, trailer.</summary>
    private static byte[] SaveBytes(params int[] achievements)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("ISAACNGSAVE09R  "));
        var b = new byte[4];
        void W(int v) { BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        W(0);
        W(1); W(642); W(642);
        var data = new byte[642];
        foreach (var a in achievements) data[a] = 1;
        ms.Write(data);
        W(11); W(8); W(4);
        ms.Write(new byte[16]);
        W(3); W(12345);
        return ms.ToArray();
    }

    [Fact]
    public void ExportThenImport_RoundTripsASet_WithoutItsHistory()
    {
        using var temp = new TempDir();
        var (service, remote, data) = Build(temp);
        File.WriteAllBytes(Path.Combine(remote, "rep+persistentgamedata1.dat"), SaveBytes(1, 2));
        Directory.CreateDirectory(Path.Combine(data, "eid"));
        File.WriteAllText(Path.Combine(data, "eid", "save1.dat"), "eid");
        var set = service.Capture("solo", "Vanilla+", new[] { "me" }, "notes");
        File.WriteAllBytes(Path.Combine(remote, "rep+persistentgamedata1.dat"), SaveBytes(1, 2, 3));
        service.CaptureInto(set);   // makes a history entry

        var pack = temp.Combine("solo.ipmsave");
        service.ExportPack("solo", pack);

        using (var zip = System.IO.Compression.ZipFile.OpenRead(pack))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "set.json");
            Assert.Contains(zip.Entries, e => e.FullName == "moddata/eid/save1.dat");
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(".history/"));
        }

        var imported = service.ImportPack(pack, "solo-from-laptop");

        Assert.Equal("solo-from-laptop", imported.Name);
        Assert.Equal("Vanilla+", imported.ModProfile);
        Assert.Equal(new[] { "me" }, imported.Players);
        Assert.True(imported.ModDataCaptured);
        Assert.Equal("eid", File.ReadAllText(Path.Combine(service.SetFolder("solo-from-laptop"), "moddata", "eid", "save1.dat")));
        Assert.Contains("solo-from-laptop", service.ListSets());
        Assert.Empty(service.ListHistory("solo-from-laptop"));
    }

    [Fact]
    public void ImportPack_RefusesAnExistingName_AndAPackWithoutMetadata()
    {
        using var temp = new TempDir();
        var (service, remote, _) = Build(temp);
        File.WriteAllBytes(Path.Combine(remote, "rep+persistentgamedata1.dat"), SaveBytes(1));
        service.Capture("solo", "Vanilla+");
        var pack = temp.Combine("solo.ipmsave");
        service.ExportPack("solo", pack);

        Assert.Throws<UnsafePathException>(() => service.ImportPack(pack));

        var junk = temp.Combine("junk.ipmsave");
        using (var zip = System.IO.Compression.ZipFile.Open(junk, System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        Assert.Throws<UnsafePathException>(() => service.ImportPack(junk));
    }

    [Fact]
    public void ImportPack_RefusesEntriesThatEscapeTheSetFolder()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);

        var evil = temp.Combine("evil.ipmsave");
        using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("set.json").Open()))
                w.Write("""{"SchemaVersion":1,"Name":"evil","Build":"Vanilla"}""");
            zip.CreateEntry("../escaped.txt");
        }

        Assert.Throws<UnsafePathException>(() => service.ImportPack(evil));
        Assert.False(File.Exists(temp.Combine("sync", ".saves", "escaped.txt")));
    }

    [Fact]
    public void ImportSaveFile_PutsAValidatedSaveIntoASlot_AndFilesHistory()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);
        var set = service.CreateEmpty("full", GameBuild.Vanilla, "Vanilla+");
        var source = temp.File("downloaded.dat");
        File.WriteAllBytes(source, SaveBytes(1, 2, 3, 4));

        var updated = service.ImportSaveFile("full", 2, source, GameBuild.Vanilla);

        Assert.Equal(new[] { "rep+persistentgamedata2.dat" }, updated.Files);
        Assert.Equal(new[] { 2 }, updated.Slots);
        Assert.Equal(GameBuild.Vanilla, updated.Build);
        Assert.Equal(1, updated.Clock["desk0001"]);

        var described = Assert.Single(service.DescribeSet(updated));
        Assert.Equal(2, described.Slot);
        Assert.Equal(4, described.Summary.Achievements.Count);
    }

    [Fact]
    public void ImportSaveFile_RefusesTheWrongBuild_AndNonSaves()
    {
        using var temp = new TempDir();
        var (service, _, _) = Build(temp);
        service.CreateEmpty("rgon", GameBuild.Repentogon, "RPTG");
        var save = temp.File("save.dat");
        File.WriteAllBytes(save, SaveBytes(1));
        var junk = temp.File("junk.dat", "not a save");

        Assert.Throws<UnsafePathException>(() => service.ImportSaveFile("rgon", 1, save, GameBuild.Vanilla));
        Assert.Throws<UnsafePathException>(() => service.ImportSaveFile("rgon", 1, junk, GameBuild.Repentogon));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ImportSaveFile("rgon", 4, save, GameBuild.Repentogon));
    }

    [Fact]
    public void DescribeLive_ParsesEachSlotOnEachBuild()
    {
        using var temp = new TempDir();
        var (service, remote, _) = Build(temp);
        File.WriteAllBytes(Path.Combine(remote, "rep+persistentgamedata1.dat"), SaveBytes(1, 2));
        File.WriteAllBytes(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), SaveBytes(1));
        File.WriteAllText(Path.Combine(remote, "rep+gamestate1.dat"), "a run");

        var live = service.DescribeLive();

        Assert.Equal(2, live.Count);
        Assert.Equal(GameBuild.Vanilla, live[0].Build);
        Assert.Equal(2, live[0].Summary.Achievements.Count);
        Assert.Equal(GameBuild.Repentogon, live[1].Build);
        Assert.Equal("Slot 1 · REPENTOGON", live[1].Label);
    }
}
