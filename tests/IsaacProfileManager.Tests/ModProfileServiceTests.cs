using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

public class ModProfileServiceTests
{
    private static (ModProfileService Service, AppConfig Config, LauncherIniService Ini) Build(TempDir temp, params string[] profiles)
    {
        var gameDir = temp.Dir("game");
        var syncRoot = temp.Dir("sync");
        foreach (var p in profiles) Directory.CreateDirectory(Path.Combine(syncRoot, p));

        var config = new AppConfig
        {
            GameDir = gameDir,
            ModsDir = Path.Combine(gameDir, "mods"),
            SyncRoot = syncRoot,
            Profiles = profiles.ToList(),
            ActiveProfile = null,
        };

        var ini = new LauncherIniService(temp.File("launcher.ini", "[Shared]\nLaunchMode = 0\n"));
        var store = new ConfigStore(Path.Combine(temp.Path, ConfigStore.FileName));
        return (new ModProfileService(new JunctionService(), ini, store), config, ini);
    }

    [Fact]
    public void Activate_PointsModsAtTheProfileFolder()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop", "heavy");
        Directory.CreateDirectory(Path.Combine(config.SyncRoot!, "coop", "SomeMod"));

        var result = service.Activate(config, "coop");

        Assert.Equal(Path.Combine(config.SyncRoot!, "coop"), new JunctionService().GetTarget(config.ModsDir!), ignoreCase: true);
        Assert.Equal(1, result.ModCount);
        Assert.Equal("coop", config.ActiveProfile);
    }

    [Fact]
    public void Activate_SweepsDisableMarkersSoNoModIsSilentlyOff()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");
        temp.File(@"sync\coop\ModA\disable.it");
        temp.File(@"sync\coop\ModA\main.lua", "-- a");
        temp.File(@"sync\coop\ModB\disable.it");
        temp.File(@"sync\coop\ModC\main.lua", "-- c");

        var result = service.Activate(config, "coop");

        Assert.Equal(2, result.ClearedMarkers);
        Assert.Empty(Directory.GetFiles(Path.Combine(config.SyncRoot!, "coop"), "disable.it", SearchOption.AllDirectories));
        // Sweeping removes the marker only, never the mod.
        Assert.True(File.Exists(Path.Combine(config.SyncRoot!, "coop", "ModA", "main.lua")));
        Assert.Equal(3, result.ModCount);
    }

    [Fact]
    public void Activate_SwitchingBetweenProfilesLeavesBothFoldersIntact()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop", "heavy");
        temp.File(@"sync\coop\ModA\main.lua", "-- a");
        temp.File(@"sync\heavy\ModB\main.lua", "-- b");

        service.Activate(config, "coop");
        service.Activate(config, "heavy");

        Assert.Equal(Path.Combine(config.SyncRoot!, "heavy"), new JunctionService().GetTarget(config.ModsDir!), ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(config.SyncRoot!, "coop", "ModA", "main.lua")));
        Assert.True(File.Exists(Path.Combine(config.SyncRoot!, "heavy", "ModB", "main.lua")));
    }

    [Fact]
    public void Activate_RefusesWhenModsIsARealFolderAndDoesNotDeleteIt()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");
        // A real mods\ folder full of the user's mods — never delete this.
        temp.File(@"game\mods\ExistingMod\main.lua", "-- precious");

        Assert.Throws<UnsafePathException>(() => service.Activate(config, "coop"));

        Assert.True(File.Exists(temp.Combine("game", "mods", "ExistingMod", "main.lua")));
    }

    [Fact]
    public void Activate_RefusesWhenTheProfileFolderIsMissing()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");
        Directory.Delete(Path.Combine(config.SyncRoot!, "coop"));

        Assert.Throws<UnsafePathException>(() => service.Activate(config, "coop"));
    }

    [Fact]
    public void Activate_RefusesAnUnknownProfile()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");

        Assert.Throws<ArgumentException>(() => service.Activate(config, "nope"));
    }

    [Fact]
    public void Activate_SelectsTheBuildOnlyWhenPerProfileBuildIsOn()
    {
        using var temp = new TempDir();
        var (service, config, ini) = Build(temp, "rgon", "plain");
        config.UseRepentogon.Add("rgon");

        config.PerProfileBuild = false;
        Assert.Null(service.Activate(config, "rgon").BuildSelected);
        Assert.Equal(LaunchMode.Vanilla, ini.GetLaunchMode());

        config.PerProfileBuild = true;
        Assert.Equal(LaunchMode.Repentogon, service.Activate(config, "rgon").BuildSelected);
        Assert.Equal(LaunchMode.Repentogon, ini.GetLaunchMode());

        Assert.Equal(LaunchMode.Vanilla, service.Activate(config, "plain").BuildSelected);
        Assert.Equal(LaunchMode.Vanilla, ini.GetLaunchMode());
    }

    [Fact]
    public void List_ReportsCountsAndReadsTheActiveProfileFromTheJunction()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop", "heavy");
        temp.File(@"sync\coop\ModA\main.lua");
        temp.File(@"sync\heavy\ModB\disable.it");
        temp.File(@"sync\heavy\ModC\main.lua");
        new JunctionService().Create(config.ModsDir!, Path.Combine(config.SyncRoot!, "heavy"));

        // Config claims one thing; the junction says another. The junction wins.
        config.ActiveProfile = "coop";
        var profiles = service.List(config);

        Assert.False(profiles.Single(p => p.Name == "coop").IsActive);
        Assert.True(profiles.Single(p => p.Name == "heavy").IsActive);
        Assert.Equal(2, profiles.Single(p => p.Name == "heavy").ModCount);
        Assert.Equal(1, profiles.Single(p => p.Name == "heavy").DisabledCount);
    }

    [Fact]
    public void Add_CreatesTheFolderAndCanSeedFromAnotherProfile()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");
        temp.File(@"sync\coop\ModA\main.lua", "-- a");
        temp.File(@"sync\coop\ModA\disable.it");

        service.Add(config, "challenge", seedFromProfile: "coop");

        Assert.Contains("challenge", config.Profiles);
        Assert.True(File.Exists(temp.Combine("sync", "challenge", "ModA", "main.lua")));
        // A seeded copy must not inherit a disabled state from its source.
        Assert.False(File.Exists(temp.Combine("sync", "challenge", "ModA", "disable.it")));
        // The source keeps its own files.
        Assert.True(File.Exists(temp.Combine("sync", "coop", "ModA", "main.lua")));
    }

    [Fact]
    public void Add_RejectsNamesThatCannotBeFolders()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");

        Assert.Throws<ArgumentException>(() => service.Add(config, @"bad\name"));
        Assert.Throws<ArgumentException>(() => service.Add(config, "with:colon"));
        Assert.Throws<ArgumentException>(() => service.Add(config, "   "));
        Assert.Throws<ArgumentException>(() => service.Add(config, "coop"));
    }

    [Fact]
    public void Remove_ForgetsTheProfileButNeverTouchesItsMods()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop", "heavy");
        temp.File(@"sync\heavy\ModB\main.lua", "-- b");
        config.UseRepentogon.Add("heavy");
        config.ActiveProfile = "coop";

        service.Remove(config, "heavy");

        Assert.DoesNotContain("heavy", config.Profiles);
        Assert.DoesNotContain("heavy", config.UseRepentogon);
        Assert.True(File.Exists(temp.Combine("sync", "heavy", "ModB", "main.lua")));
    }

    [Fact]
    public void Remove_RefusesTheActiveProfile()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp, "coop");
        config.ActiveProfile = "coop";

        Assert.Throws<InvalidOperationException>(() => service.Remove(config, "coop"));
    }
}
