using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SaveIdentityTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    private const string Account = "351019201";

    private static (SaveSetService Sets, SaveIdentityService Identity, string Remote) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");
        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } } }
            """);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");

        var sets = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync"));
        return (sets, new SaveIdentityService(sets), remote);
    }

    private static void Live(string remote, string slot1, bool run = false)
    {
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), slot1);
        var runFile = Path.Combine(remote, "rep+gamestate1.dat");
        if (run) File.WriteAllText(runFile, "a run in progress");
        else if (File.Exists(runFile)) File.Delete(runFile);
    }

    [Fact]
    public void NoFiles_IsNoSaves()
    {
        using var temp = new TempDir();
        var (_, identity, _) = Build(temp);

        Assert.Equal(LiveSaveState.NoSaves, identity.Identify(null).State);
    }

    [Fact]
    public void LiveMatchingASetByteForByte_IsExact_WithoutAnyHint()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        Live(remote, "solo v1");
        sets.Capture("solo", "Vanilla+");
        Live(remote, "duo v1");
        sets.Capture("duo", "Online+");

        Live(remote, "solo v1");
        var result = identity.Identify(hint: null);

        Assert.Equal(LiveSaveState.Exact, result.State);
        Assert.Equal("solo", result.Set!.Name);
    }

    [Fact]
    public void ARunInProgressOnTopOfAnExactMatch_IsDrifted_NamingTheExtraFile()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        Live(remote, "solo v1");
        sets.Capture("solo", "Vanilla+");

        Live(remote, "solo v1", run: true);
        var result = identity.Identify(null);

        Assert.Equal(LiveSaveState.Drifted, result.State);
        Assert.Equal("solo", result.Set!.Name);
        Assert.Equal(new[] { "rep+gamestate1.dat" }, result.Drift);
    }

    [Fact]
    public void ChangedBytes_AreDrifted_OnlyWhenTheHintNamesTheSet()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        Live(remote, "solo v1");
        sets.Capture("solo", "Vanilla+");

        Live(remote, "solo v2 after an evening's play");

        Assert.Equal(LiveSaveState.Unrecognised, identity.Identify(hint: null).State);

        var hinted = identity.Identify(hint: "solo");
        Assert.Equal(LiveSaveState.Drifted, hinted.State);
        Assert.Equal("solo", hinted.Set!.Name);
        Assert.Equal(new[] { "rep+persistentgamedata1.dat" }, hinted.Drift);
    }

    [Fact]
    public void TheHintCannotOverrideAnExactMatchForAnotherSet()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        Live(remote, "solo v1");
        sets.Capture("solo", "Vanilla+");
        Live(remote, "duo v1");
        sets.Capture("duo", "Online+");

        // Config still says "solo", but the bytes are duo's: the hashes win.
        var result = identity.Identify(hint: "solo");

        Assert.Equal(LiveSaveState.Exact, result.State);
        Assert.Equal("duo", result.Set!.Name);
    }

    [Fact]
    public void AHintForASetWhoseFilesAreNotLive_IsUnrecognised()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        Live(remote, "solo v1");
        sets.Capture("solo", "Vanilla+");

        // The live folder now holds a REPENTOGON save; "solo" is a vanilla set.
        File.Delete(Path.Combine(remote, "rep+persistentgamedata1.dat"));
        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "something else");

        Assert.Equal(LiveSaveState.Unrecognised, identity.Identify(hint: "solo").State);
    }

    [Fact]
    public void EmptySets_NeverMatch()
    {
        using var temp = new TempDir();
        var (sets, identity, remote) = Build(temp);
        sets.CreateEmpty("fresh", GameBuild.Vanilla, "Vanilla+");
        Live(remote, "anything");

        Assert.Equal(LiveSaveState.Unrecognised, identity.Identify(hint: "fresh").State);
    }
}
