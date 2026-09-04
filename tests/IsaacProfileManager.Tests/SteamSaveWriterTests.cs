using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Activation goes through Steam's API when it can, and falls back to a file
/// copy — saying so — when it cannot. The writer is faked; the real one is a
/// 32-bit process against the real client and is covered by Test.ps1 -Live.
/// </summary>
public class SteamSaveWriterTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    /// <summary>Stands in for Steam: does the file work itself and records what it was asked.</summary>
    private sealed class FakeSteam : ISteamSaveWriter
    {
        public string LiveFolder { get; init; } = string.Empty;
        public bool IsAvailable { get; set; } = true;
        public bool Refuse { get; set; }
        public List<string> Deleted { get; } = new();
        public List<string> Written { get; } = new();

        public SteamSaveWriteResult Replace(IReadOnlyList<string> deleteNames, IReadOnlyList<string> writeNames, string fromFolder)
        {
            if (Refuse) return SteamSaveWriteResult.Unavailable("Steam is not running, or no user is signed in.");
            foreach (var name in deleteNames) { File.Delete(Path.Combine(LiveFolder, name)); Deleted.Add(name); }
            foreach (var name in writeNames) { File.Copy(Path.Combine(fromFolder, name), Path.Combine(LiveFolder, name), true); Written.Add(name); }
            return new SteamSaveWriteResult(true, Written, Deleted, Array.Empty<string>());
        }
    }

    private const string Account = "351019201";

    private static (SaveSetService Service, FakeSteam Steam, string Remote) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");
        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } } }
            """);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n\t\"rep+persistentgamedata1.dat\"\n\t{\n\t}\n}\n");

        var fake = new FakeSteam { LiveFolder = remote };
        var service = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync"), null, temp.Dir("Game"),
                                         new SaveSetOptions { SteamWriter = fake });
        return (service, fake, remote);
    }

    [Fact]
    public void Activate_WritesThroughSteam_WhenTheLiveFolderIsSteams()
    {
        using var temp = new TempDir();
        var (service, steam, remote) = Build(temp);
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "one");
        var set = service.Capture("solo", "Vanilla+");
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "two");
        File.WriteAllText(Path.Combine(remote, "rep+gamestate1.dat"), "a run");

        var result = service.ActivateSet(set, GameBuild.Vanilla);

        Assert.True(result.ViaSteam);
        Assert.Contains("through Steam", result.Transport);
        Assert.Equal(new[] { "rep+gamestate1.dat", "rep+persistentgamedata1.dat" }, steam.Deleted);
        Assert.Equal(new[] { "rep+persistentgamedata1.dat" }, steam.Written);
        Assert.Equal("one", File.ReadAllText(Path.Combine(remote, "rep+persistentgamedata1.dat")));
        Assert.False(File.Exists(Path.Combine(remote, "rep+gamestate1.dat")));
    }

    [Fact]
    public void Activate_FallsBackToACopy_AndSaysWhy_WhenSteamRefuses()
    {
        using var temp = new TempDir();
        var (service, steam, remote) = Build(temp);
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "one");
        var set = service.Capture("solo", "Vanilla+");
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "two");
        steam.Refuse = true;

        var result = service.ActivateSet(set, GameBuild.Vanilla);

        Assert.False(result.ViaSteam);
        Assert.Contains("file copy", result.Transport);
        Assert.Contains("not running", result.Transport);
        Assert.Equal("one", File.ReadAllText(Path.Combine(remote, "rep+persistentgamedata1.dat")));
    }

    [Fact]
    public void Activate_UsesACopy_WhenNoWriterIsConfigured()
    {
        using var temp = new TempDir();
        var (service, steam, remote) = Build(temp);
        steam.IsAvailable = false;
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "one");
        var set = service.Capture("solo", "Vanilla+");

        var result = service.ActivateSet(set, GameBuild.Vanilla);

        Assert.False(result.ViaSteam);
        Assert.Empty(steam.Written);
        Assert.Equal("one", File.ReadAllText(Path.Combine(remote, "rep+persistentgamedata1.dat")));
    }

    [Fact]
    public void RestoreBackup_GoesThroughSteamToo()
    {
        using var temp = new TempDir();
        var (service, steam, remote) = Build(temp);
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "one");
        var backup = service.BackupLive("manual");
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "two");

        service.RestoreBackup(Path.GetFileName(backup));

        Assert.Contains("rep+persistentgamedata1.dat", steam.Written);
        Assert.Equal("one", File.ReadAllText(Path.Combine(remote, "rep+persistentgamedata1.dat")));
    }
}
