using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The receiving half of sharing: a manifest that arrived by sync or as a file
/// must be visible, buildable and activatable against the local library.
/// </summary>
public class ProfileSharingTests
{
    private static (ModProfileService Profiles, ModLibraryService Library, AppConfig Config) Build(TempDir temp)
    {
        var gameDir = temp.Dir("game");
        var syncRoot = temp.Dir("sync");
        var config = new AppConfig
        {
            GameDir = gameDir,
            ModsDir = Path.Combine(gameDir, "mods"),
            SyncRoot = syncRoot,
        };

        var junctions = new JunctionService();
        var store = new ConfigStore(Path.Combine(temp.Path, ConfigStore.FileName));
        var ini = new LauncherIniService(temp.File("launcher.ini", "[Shared]\nLaunchMode = 0\n"));

        return (new ModProfileService(junctions, ini, store), new ModLibraryService(junctions, syncRoot), config);
    }

    private static void GiveLibraryMod(TempDir temp, string entry) =>
        temp.File($@"sync\.library\{entry}\main.lua", $"-- {entry}");

    [Fact]
    public void AManifestThatArrivedBySyncIsFound()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        GiveLibraryMod(temp, "beta");

        // As if Syncthing dropped it in: a manifest with no config entry.
        library.SaveManifest("their-coop", new ProfileManifest { Mods = { "alpha", "beta" } });

        var found = profiles.FindUnregisteredProfiles(config).Single();

        Assert.Equal("their-coop", found.Name);
        Assert.Equal(2, found.ModCount);
        Assert.Equal(0, found.MissingFromLibrary);
        Assert.Contains("already in your library", found.Notes);
    }

    [Fact]
    public void ADiscoveredProfileReportsModsYouDoNotHave()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        library.SaveManifest("their-coop", new ProfileManifest { Mods = { "alpha", "not-synced-yet" } });

        var found = profiles.FindUnregisteredProfiles(config).Single();

        Assert.Equal(1, found.MissingFromLibrary);
        Assert.Contains("not in your library", found.Notes);
    }

    [Fact]
    public void ProfilesAlreadyInTheConfigAreNotReportedAsNew()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        library.SaveManifest("mine", new ProfileManifest { Mods = { "alpha" } });
        config.Profiles.Add("mine");

        Assert.Empty(profiles.FindUnregisteredProfiles(config));
    }

    [Fact]
    public void RegisterProfile_AddsItToTheConfigAndBuildsTheFolder()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        library.SaveManifest("their-coop", new ProfileManifest { Mods = { "alpha" } });

        var report = profiles.RegisterProfile(config, "their-coop");

        Assert.Contains("their-coop", config.Profiles);
        Assert.Equal(new[] { "alpha" }, report!.Created);
        Assert.True(new JunctionService().IsJunction(temp.Combine("sync", "their-coop", "alpha")));
    }

    [Fact]
    public void ImportSharedProfile_TakesAFileSomeoneSentAndMakesItUsable()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        GiveLibraryMod(temp, "beta");

        var hashes = new LibraryHashService(library);
        hashes.RecordAll();
        var export = hashes.Export("friday-group", new ProfileManifest { Mods = { "alpha", "beta" }, Notes = "the good set" });
        var file = temp.Combine("friday-group.ipmprofile.json");
        hashes.WriteExport(export, file);

        // A fresh machine that has the library but not the profile.
        config.Profiles.Clear();
        var (name, report, missing) = profiles.ImportSharedProfile(config, file);

        Assert.Equal("friday-group", name);
        Assert.Empty(missing);
        Assert.Equal(2, report!.Created.Count);
        Assert.Contains("friday-group", config.Profiles);
        Assert.Equal("the good set", library.LoadManifest("friday-group").Notes);
    }

    [Fact]
    public void ImportSharedProfile_NamesTheModsTheReceiverIsMissing()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");

        var hashes = new LibraryHashService(library);
        var file = temp.Combine("x.json");
        hashes.WriteExport(new SharedProfile { Name = "theirs", Mods = { "alpha", "beta", "gamma" } }, file);

        var (_, report, missing) = profiles.ImportSharedProfile(config, file);

        // Reported, not silently skipped.
        Assert.Equal(new[] { "beta", "gamma" }, missing);
        Assert.Equal(new[] { "alpha" }, report!.Created);
        Assert.Equal(new[] { "beta", "gamma" }, report.MissingFromLibrary.OrderBy(m => m).ToArray());
    }

    [Fact]
    public void Activate_RebuildsFromTheManifestFirst()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        GiveLibraryMod(temp, "beta");

        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha" } });
        profiles.RegisterProfile(config, "coop");

        // The manifest changes without going through the UI — exactly what a
        // sync from someone else looks like.
        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha", "beta" } });

        var result = profiles.Activate(config, "coop");

        Assert.Equal(new[] { "beta" }, result.Materialised!.Created);
        Assert.Equal(2, result.ModCount);
        Assert.True(new JunctionService().IsJunction(temp.Combine("sync", "coop", "beta")));
    }

    [Fact]
    public void Activate_RemovesLinksTheManifestNoLongerLists()
    {
        using var temp = new TempDir();
        var (profiles, library, config) = Build(temp);
        GiveLibraryMod(temp, "alpha");
        GiveLibraryMod(temp, "beta");
        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha", "beta" } });
        profiles.RegisterProfile(config, "coop");

        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha" } });
        var result = profiles.Activate(config, "coop");

        Assert.Equal(new[] { "beta" }, result.Materialised!.Removed);
        Assert.Equal(1, result.ModCount);
        // Unlinking must never reach the library copy.
        Assert.True(File.Exists(temp.Combine("sync", ".library", "beta", "main.lua")));
    }

    [Fact]
    public void Activate_LeavesAProfileWithNoManifestExactlyAsItIs()
    {
        using var temp = new TempDir();
        var (profiles, _, config) = Build(temp);
        temp.File(@"sync\legacy\SomeMod\main.lua", "-- hand made");
        config.Profiles.Add("legacy");

        var result = profiles.Activate(config, "legacy");

        // Profiles that predate manifests must not be touched.
        Assert.Null(result.Materialised);
        Assert.Equal(1, result.ModCount);
        Assert.True(File.Exists(temp.Combine("sync", "legacy", "SomeMod", "main.lua")));
    }
}
