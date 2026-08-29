using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Which folder the game actually saves in.
///
/// Steam's userdata folder is right only for a copy running against the real
/// Steam client. With a DRM emulator the game writes somewhere else entirely,
/// and the app watched the Steam folder regardless — so clearing it did
/// nothing, a fresh set could never be filled, and the game went on loading the
/// save it had all along.
/// </summary>
public class SaveLocationTests
{
    private const string Account = "351019201";

    /// <summary>A Steam tree, and the game folder that reports its own save path.</summary>
    private static (SaveLocationService Service, string Steam, string Remote, string GameDir) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", Account, "250900", "remote");
        var gameDir = temp.Dir("game");

        temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf",
            "\"UserRoamingConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n" +
            "\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t\t\"250900\"\n\t\t\t\t\t{\n" +
            "\t\t\t\t\t\t\"cloudenabled\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}");

        return (new SaveLocationService(new SteamCloudService(steam)), steam, remote, gameDir);
    }

    private static void GiveReportedPath(string gameDir, string savePath) =>
        File.WriteAllText(Path.Combine(gameDir, SaveLocationService.PathFileName),
            "This file is purely informational. Changing it will have no effect on saving or loading data.\n\n" +
            $"Save Data Path: {savePath}\n" +
            "Modding Data Path: wherever\n");

    private static void GiveSave(string folder) =>
        File.WriteAllText(Path.Combine(folder, "rep+persistentgamedata1.dat"), "a save");

    [Fact]
    public void AConfiguredFolderWinsOutright()
    {
        using var temp = new TempDir();
        var (service, _, remote, gameDir) = Build(temp);
        GiveSave(remote);
        var mine = temp.Dir("elsewhere");

        var resolved = service.Resolve(mine, gameDir);

        Assert.Equal(mine, resolved.Path);
        Assert.Equal(SaveFolderSource.Configured, resolved.Source);
    }

    [Fact]
    public void TheFolderHoldingSavesIsPreferredOverTheOneThatDoesNot()
    {
        using var temp = new TempDir();
        var (service, _, remote, gameDir) = Build(temp);

        // The emulated copy's real location, which Steam knows nothing about.
        var documents = temp.Dir("Documents", "My Games", "Binding of Isaac Repentance+");
        GiveReportedPath(gameDir, documents);
        GiveSave(documents);

        // Steam's folder exists but is empty - the case that fooled the app.
        Assert.True(Directory.Exists(remote));

        var resolved = service.Resolve(null, gameDir);

        Assert.Equal(documents, resolved.Path);
        Assert.Equal(SaveFolderSource.ReportedByGame, resolved.Source);
        Assert.Equal(1, resolved.SaveFileCount);
    }

    [Fact]
    public void SteamsFolderIsUsedWhenItIsTheOneWithTheSaves()
    {
        using var temp = new TempDir();
        var (service, _, remote, gameDir) = Build(temp);

        var documents = temp.Dir("Documents", "My Games", "Binding of Isaac Repentance+");
        GiveReportedPath(gameDir, documents);
        GiveSave(remote);

        var resolved = service.Resolve(null, gameDir);

        // A legitimate Steam copy must keep working exactly as before.
        Assert.Equal(remote, resolved.Path, ignoreCase: true);
        Assert.Equal(SaveFolderSource.SteamUserdata, resolved.Source);
    }

    [Fact]
    public void WithNoSavesAnywhereTheGamesOwnReportIsTheBetterGuess()
    {
        using var temp = new TempDir();
        var (service, _, _, gameDir) = Build(temp);
        var documents = temp.Dir("Documents", "My Games", "Binding of Isaac Repentance+");
        GiveReportedPath(gameDir, documents);

        var resolved = service.Resolve(null, gameDir);

        Assert.Equal(documents, resolved.Path);
        Assert.Equal(SaveFolderSource.ReportedByGame, resolved.Source);
        Assert.Equal(0, resolved.SaveFileCount);
    }

    [Fact]
    public void TheReportedPathIsNormalisedFromMixedSeparators()
    {
        using var temp = new TempDir();
        var (_, _, _, gameDir) = Build(temp);
        var documents = temp.Dir("Documents", "My Games", "Binding of Isaac Repentance+");

        // The game writes it exactly like this, backslash then forward slashes.
        GiveReportedPath(gameDir, documents.Replace('\\', '/'));

        Assert.Equal(documents, SaveLocationService.ReadReportedPath(gameDir), ignoreCase: true);
    }

    [Fact]
    public void AReportedPathThatDoesNotExistIsIgnored()
    {
        using var temp = new TempDir();
        var (_, _, _, gameDir) = Build(temp);
        GiveReportedPath(gameDir, @"Q:\nowhere\at\all");

        Assert.Null(SaveLocationService.ReadReportedPath(gameDir));
    }

    private sealed class ClosedGame : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    [Fact]
    public void CloudIsNotAGateWhenTheSavesAreNotSteams()
    {
        using var temp = new TempDir();
        var steam = temp.Dir("Steam");
        temp.Dir("Steam", "userdata", Account, "250900", "remote");
        var gameDir = temp.Dir("game");
        var documents = temp.Dir("Documents", "My Games", "Binding of Isaac Repentance+");

        // Cloud left ON for the app, which on a Steam copy blocks a swap outright.
            temp.File($@"Steam\userdata\{Account}\7\remote\sharedconfig.vdf",
                "\"UserRoamingConfigStore\"\n{\n" +
                "\"Software\"\n{\n" +
                "\"Valve\"\n{\n" +
                "\"Steam\"\n{\n" +
                "\"apps\"\n{\n" +
                "\"250900\"\n{\n" +
                "\"cloudenabled\"\t\t\"1\"\n" +
                "}\n}\n}\n}\n}\n}\n");

        GiveReportedPath(gameDir, documents);
        GiveSave(documents);

        var service = new SaveSetService(new ClosedGame(), new SteamCloudService(steam),
                                         temp.Dir("sync"), configuredSaveFolder: null, gameDir: gameDir);
        var set = service.Capture("mine", "my-mods");

        var checks = service.Check(set, IsaacProfileManager.Core.Models.GameBuild.Vanilla);

        // Steam has no copy of a file outside its own folder, so it cannot put
        // one back, and blocking on that refuses a swap for a reason that
        // cannot apply to this install.
        Assert.False(checks.CloudApplies);
        Assert.True(checks.CanActivate);
        Assert.Empty(checks.Blockers);
    }

    [Fact]
    public void NoSavedataFileMeansNoReportedPath()
    {
        using var temp = new TempDir();
        var (_, _, _, gameDir) = Build(temp);

        Assert.Null(SaveLocationService.ReadReportedPath(gameDir));
    }
}
