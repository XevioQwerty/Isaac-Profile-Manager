using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Switching a mod off inside a profile, and seeding a profile from another one.
///
/// Both are about the manifest being what a profile means: a profile folder is
/// junctions, so anything reasoning about the folder alone gets these wrong.
/// </summary>
public class ProfileDisableTests
{
    private static (ModProfileService Service, ModLibraryService Library, AppConfig Config) Build(
        TempDir temp, params string[] libraryMods)
    {
        var gameDir = temp.Dir("game");
        var syncRoot = temp.Dir("sync");

        var junctions = new JunctionService();
        var library = new ModLibraryService(junctions, syncRoot);

        foreach (var mod in libraryMods)
        {
            Directory.CreateDirectory(Path.Combine(library.LibraryRoot, mod));
            File.WriteAllText(Path.Combine(library.LibraryRoot, mod, "main.lua"), "-- " + mod);
        }

        var config = new AppConfig
        {
            GameDir = gameDir,
            ModsDir = Path.Combine(gameDir, "mods"),
            SyncRoot = syncRoot,
            Profiles = new List<string>(),
            ActiveProfile = null,
        };

        var ini = new LauncherIniService(temp.File("launcher.ini", "[Shared]\nLaunchMode = 0\n"));
        var store = new ConfigStore(Path.Combine(temp.Path, ConfigStore.FileName));
        return (new ModProfileService(junctions, ini, store), library, config);
    }

    private static void SeedProfile(ModProfileService service, ModLibraryService library, AppConfig config,
                                    string name, params string[] mods)
    {
        service.Add(config, name);
        var manifest = new ProfileManifest { Mods = mods.ToList() };
        library.SaveManifest(name, manifest);
        library.Materialise(name, manifest);
    }

    [Fact]
    public void Disabling_UnlinksTheFolderButKeepsTheMembership()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB", "ModC");
        SeedProfile(service, library, config, "coop", "ModA", "ModB", "ModC");

        var result = service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        var profileDir = Path.Combine(config.SyncRoot!, "coop");
        Assert.False(Directory.Exists(Path.Combine(profileDir, "ModB")));
        Assert.True(Directory.Exists(Path.Combine(profileDir, "ModA")));

        // Still a member, so the profile remembers what it is meant to contain.
        var manifest = library.LoadManifest("coop");
        Assert.Contains("ModB", manifest.Mods);
        Assert.True(manifest.IsDisabled("ModB"));
        Assert.Equal(new[] { "ModB" }, result.Changed);
    }

    [Fact]
    public void Disabling_NeverTouchesTheLibraryCopy()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");

        service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        // The library is shared; unlinking one profile must not empty it.
        Assert.True(File.Exists(Path.Combine(library.LibraryRoot, "ModB", "main.lua")));
    }

    [Fact]
    public void Reenabling_LinksTheFolderBack()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");
        service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        var result = service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: false);

        Assert.True(Directory.Exists(Path.Combine(config.SyncRoot!, "coop", "ModB")));
        Assert.False(library.LoadManifest("coop").IsDisabled("ModB"));
        Assert.Equal(1, result.LinksCreated);
    }

    [Fact]
    public void Disabling_ClearsAStaleMarkerOutOfTheLibraryEntry()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA");
        SeedProfile(service, library, config, "coop", "ModA");

        // Written through the junction by the in-game menu: it lands in the
        // library and would switch ModA off in every other profile too.
        File.WriteAllText(Path.Combine(library.LibraryRoot, "ModA", "disable.it"), "");

        var result = service.SetDisabled(config, "coop", new[] { "ModA" }, disabled: true);

        Assert.False(File.Exists(Path.Combine(library.LibraryRoot, "ModA", "disable.it")));
        Assert.Equal(1, result.MarkersCleared);
    }

    [Fact]
    public void DisablingSomethingTheProfileDoesNotHave_ChangesNothing()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA");

        var result = service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        Assert.Empty(result.Changed);
        Assert.Empty(library.LoadManifest("coop").Disabled);
    }

    [Fact]
    public void ActivatingRebuilds_WithoutTheDisabledMods()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");
        service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        var result = service.Activate(config, "coop");

        // The rebuild on activate must not quietly bring a disabled mod back.
        Assert.False(Directory.Exists(Path.Combine(config.SyncRoot!, "coop", "ModB")));
        Assert.Equal(1, result.ModCount);
    }

    [Fact]
    public void ListReportsDisabledMembers()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB", "ModC");
        SeedProfile(service, library, config, "coop", "ModA", "ModB", "ModC");
        service.SetDisabled(config, "coop", new[] { "ModB", "ModC" }, disabled: true);

        var profile = service.List(config).Single(p => p.Name == "coop");

        Assert.Equal(1, profile.ModCount);
        Assert.Equal(2, profile.DisabledCount);
    }

    // --- Seeding -----------------------------------------------------------

    [Fact]
    public void SeedingCopiesTheMods_EvenThoughTheyAreAllJunctions()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");

        service.Add(config, "coop-copy", seedFromProfile: "coop");

        // The old behaviour skipped reparse points, so this was an empty folder.
        var copyDir = Path.Combine(config.SyncRoot!, "coop-copy");
        Assert.True(Directory.Exists(Path.Combine(copyDir, "ModA")));
        Assert.True(Directory.Exists(Path.Combine(copyDir, "ModB")));
        Assert.Equal(new[] { "ModA", "ModB" }, library.LoadManifest("coop-copy").Mods);
    }

    [Fact]
    public void SeedingWithoutDisabled_DropsThemEntirely()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");
        service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        service.Add(config, "coop-trimmed", seedFromProfile: "coop", seedDisabled: false);

        var manifest = library.LoadManifest("coop-trimmed");
        Assert.Equal(new[] { "ModA" }, manifest.Mods);
        Assert.Empty(manifest.Disabled);
        Assert.False(Directory.Exists(Path.Combine(config.SyncRoot!, "coop-trimmed", "ModB")));
    }

    [Fact]
    public void SeedingWithDisabled_CarriesThemAcrossStillOff()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA", "ModB");
        SeedProfile(service, library, config, "coop", "ModA", "ModB");
        service.SetDisabled(config, "coop", new[] { "ModB" }, disabled: true);

        service.Add(config, "coop-same", seedFromProfile: "coop", seedDisabled: true);

        var manifest = library.LoadManifest("coop-same");
        Assert.Equal(new[] { "ModA", "ModB" }, manifest.Mods);
        Assert.True(manifest.IsDisabled("ModB"));
        Assert.False(Directory.Exists(Path.Combine(config.SyncRoot!, "coop-same", "ModB")));
    }

    [Fact]
    public void SeedingStillCopiesHandInstalledFolders()
    {
        using var temp = new TempDir();
        var (service, library, config) = Build(temp, "ModA");
        SeedProfile(service, library, config, "coop", "ModA");

        // A real folder the library does not own has no other copy.
        var handmade = Path.Combine(config.SyncRoot!, "coop", "HandMade");
        Directory.CreateDirectory(handmade);
        File.WriteAllText(Path.Combine(handmade, "main.lua"), "-- local only");

        service.Add(config, "coop-copy", seedFromProfile: "coop");

        Assert.True(File.Exists(Path.Combine(config.SyncRoot!, "coop-copy", "HandMade", "main.lua")));
    }

    [Fact]
    public void AManifestWrittenBeforeDisablingExisted_StillLoads()
    {
        using var temp = new TempDir();
        var (_, library, _) = Build(temp, "ModA");

        // Schema stayed at 1 when Disabled was added, so an older file is valid.
        Directory.CreateDirectory(library.ManifestRoot);
        File.WriteAllText(Path.Combine(library.ManifestRoot, "old.json"),
                          "{\"SchemaVersion\":1,\"Mods\":[\"ModA\"],\"Notes\":\"from an older build\"}");

        var manifest = library.LoadManifest("old");

        Assert.Equal(new[] { "ModA" }, manifest.Mods);
        Assert.Empty(manifest.Disabled);
        Assert.Equal(new[] { "ModA" }, manifest.EnabledMods);
    }
}
