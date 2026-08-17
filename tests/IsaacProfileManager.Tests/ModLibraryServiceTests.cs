using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class ModLibraryServiceTests
{
    private static ModLibraryService Build(TempDir temp) =>
        new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string name, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {name}");
        temp.File($@"content\{id}\thumb.png", "PNG");
        return new WorkshopItem { Id = id, Name = name, Directory = directory, Description = $"about {name}", ContentPath = content, LocalImagePath = Path.Combine(content, "thumb.png") };
    }

    [Fact]
    public void Import_CopiesContentUnderASuffixFreeName()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var entry = library.Import(Item(temp, "3127536138", "[BETA] REPENTOGON", "repentogon"));

        // Suffix-free is the whole point: Steam has no claim on this folder.
        Assert.Equal("repentogon", entry);
        Assert.True(File.Exists(Path.Combine(library.LibraryRoot, "repentogon", "main.lua")));
        Assert.Equal(new[] { "repentogon" }, library.ListEntries());
    }

    [Fact]
    public void Import_CachesNameDescriptionAndPreviewOutsideTheModFolder()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var entry = library.Import(Item(temp, "835236871", "Better Character Menu", "better character menu"));

        Assert.Equal("Better Character Menu", library.GetCachedName(entry));
        Assert.Equal("about Better Character Menu", library.GetCachedDescription(entry));
        Assert.NotNull(library.GetCachedImage(entry));

        // Nothing may be added inside the mod folder — co-op needs those bytes to match.
        var modFiles = Directory.GetFiles(Path.Combine(library.LibraryRoot, entry)).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "main.lua", "thumb.png" }, modFiles.OrderBy(f => f).ToArray());
    }

    [Fact]
    public void Import_IsSkippedWhenTheEntryAlreadyExists()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        var item = Item(temp, "835236871", "Better Character Menu", "better character menu");
        library.Import(item);
        File.WriteAllText(Path.Combine(library.LibraryRoot, "better character menu", "main.lua"), "-- user edited");

        library.Import(item);

        Assert.Equal("-- user edited", File.ReadAllText(Path.Combine(library.LibraryRoot, "better character menu", "main.lua")));
    }

    [Fact]
    public void Import_KeepsTwoDifferentModsApartWhenTheirFolderNamesCollide()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "111111", "First", "sharedname"));

        var second = library.Import(Item(temp, "222222", "Second", "sharedname"));

        Assert.Equal("sharedname_222222", second);
        Assert.Equal(2, library.ListEntries().Count);
    }

    [Fact]
    public void Import_RefusesAnItemSteamHasNotDownloaded()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        var item = new WorkshopItem { Id = "1", Name = "Nope", Directory = "nope", ContentPath = temp.Combine("missing") };

        Assert.Throws<UnsafePathException>(() => library.Import(item));
    }

    [Fact]
    public void ListEntries_IgnoresTheBookkeepingFolders()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "835236871", "Better Character Menu", "bcm"));

        Assert.Equal(new[] { "bcm" }, library.ListEntries());
    }

    [Fact]
    public void ManifestRoundTripsAndIsPortable()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        library.SaveManifest("coop", new ProfileManifest { Mods = { "bcm", "eid" }, Notes = "friday group" });
        var loaded = library.LoadManifest("coop");

        Assert.Equal(new[] { "bcm", "eid" }, loaded.Mods);
        Assert.Equal("friday group", loaded.Notes);
        Assert.Equal(new[] { "coop" }, library.ListManifests());

        // No machine-local paths, so it can be synced to another person.
        var json = File.ReadAllText(Path.Combine(library.ManifestRoot, "coop.json"));
        Assert.DoesNotContain(":\\", json);
    }

    [Fact]
    public void LoadManifest_RefusesAnUnknownSchema()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        Directory.CreateDirectory(library.ManifestRoot);
        File.WriteAllText(Path.Combine(library.ManifestRoot, "coop.json"), """{"SchemaVersion": 99, "Mods": []}""");

        Assert.Throws<ConfigSchemaMismatchException>(() => library.LoadManifest("coop"));
    }

    [Fact]
    public void Materialise_BuildsAFolderOfJunctionsIntoTheLibrary()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        library.Import(Item(temp, "2", "Beta", "beta"));

        var report = library.Materialise("coop", new ProfileManifest { Mods = { "alpha", "beta" } });

        var profileDir = temp.Combine("sync", "coop");
        var junctions = new JunctionService();
        Assert.Equal(2, report.Created.Count);
        Assert.True(junctions.IsJunction(Path.Combine(profileDir, "alpha")));
        Assert.Equal(Path.Combine(library.LibraryRoot, "beta"), junctions.GetTarget(Path.Combine(profileDir, "beta")), ignoreCase: true);
        // Reachable through the link, which is what Isaac needs.
        Assert.Equal("-- Alpha", File.ReadAllText(Path.Combine(profileDir, "alpha", "main.lua")));
    }

    [Fact]
    public void Materialise_RemovesLinksNoLongerInTheManifestAndLeavesTheLibraryWhole()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        library.Import(Item(temp, "2", "Beta", "beta"));
        library.Materialise("coop", new ProfileManifest { Mods = { "alpha", "beta" } });

        var report = library.Materialise("coop", new ProfileManifest { Mods = { "alpha" } });

        Assert.Equal(new[] { "beta" }, report.Removed);
        Assert.False(Directory.Exists(temp.Combine("sync", "coop", "beta")));
        // Removing a link must never reach the mod it pointed at.
        Assert.True(File.Exists(Path.Combine(library.LibraryRoot, "beta", "main.lua")));
    }

    [Fact]
    public void Materialise_NeverDeletesARealFolderTheUserPutThere()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        var handInstalled = temp.Dir("sync", "coop", "my hand made mod");
        File.WriteAllText(Path.Combine(handInstalled, "main.lua"), "-- precious");

        var report = library.Materialise("coop", new ProfileManifest { Mods = { "alpha" } });

        Assert.Contains("my hand made mod", report.LeftAlone);
        Assert.False(report.IsClean);
        Assert.True(File.Exists(Path.Combine(handInstalled, "main.lua")));
    }

    [Fact]
    public void Materialise_ReportsManifestEntriesThatAreNotInTheLibrary()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));

        var report = library.Materialise("coop", new ProfileManifest { Mods = { "alpha", "not-imported-yet" } });

        Assert.Equal(new[] { "not-imported-yet" }, report.MissingFromLibrary);
        Assert.Equal(new[] { "alpha" }, report.Created);
        Assert.False(report.IsClean);
    }

    [Fact]
    public void Materialise_IsIdempotent()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        var manifest = new ProfileManifest { Mods = { "alpha" } };

        library.Materialise("coop", manifest);
        var second = library.Materialise("coop", manifest);

        Assert.Equal(0, second.ChangeCount);
        Assert.True(second.IsClean);
    }

    [Fact]
    public void Materialise_RepointsALinkThatAimsSomewhereStale()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        var stale = temp.Dir("elsewhere");
        new JunctionService().Create(temp.Combine("sync", "coop", "alpha"), stale);

        var report = library.Materialise("coop", new ProfileManifest { Mods = { "alpha" } });

        Assert.Equal(new[] { "alpha" }, report.Repointed);
        Assert.Equal(Path.Combine(library.LibraryRoot, "alpha"),
                     new JunctionService().GetTarget(temp.Combine("sync", "coop", "alpha")), ignoreCase: true);
        Assert.True(Directory.Exists(stale));
    }
}
