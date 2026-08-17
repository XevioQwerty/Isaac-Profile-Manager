using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class LibraryInspectionTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string name, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", new string('x', 2048));
        temp.File($@"content\{id}\thumb.png", "PNG");
        return new WorkshopItem
        {
            Id = id, Name = name, Directory = directory, Description = $"what {name} does",
            ContentPath = content, LocalImagePath = System.IO.Path.Combine(content, "thumb.png"),
        };
    }

    [Fact]
    public void Describe_ReportsEverythingCapturedAtImport()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        var entry = library.Import(Item(temp, "3127536138", "[BETA] REPENTOGON", "repentogon"));

        var info = library.Describe(entry);

        Assert.Equal("repentogon", info.Entry);
        Assert.Equal("[BETA] REPENTOGON", info.Name);
        Assert.Equal("what [BETA] REPENTOGON does", info.Description);
        Assert.Equal("3127536138", info.WorkshopId);
        Assert.True(info.HasWorkshopOrigin);
        Assert.NotNull(info.PreviewPath);
        Assert.Equal(2, info.FileCount);
        Assert.True(info.SizeBytes > 2000);
        Assert.NotNull(info.ImportedUtc);
    }

    [Fact]
    public void Describe_SkipsMeasuringWhenAsked()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        var entry = library.Import(Item(temp, "1", "Alpha", "alpha"));

        var info = library.Describe(entry, measure: false);

        // Walking every mod on each refresh would mean scanning gigabytes.
        Assert.Equal(0, info.SizeBytes);
        Assert.Equal(0, info.FileCount);
        Assert.Equal("Alpha", info.Name);
    }

    [Fact]
    public void Describe_FallsBackToTheFolderNameForAHandAddedMod()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        temp.File(@"sync\.library\hand made\main.lua", "-- local only");

        var info = library.Describe("hand made");

        Assert.Equal("hand made", info.Name);
        Assert.False(info.HasWorkshopOrigin);
        Assert.Equal(string.Empty, info.Description);
    }

    [Fact]
    public void ProfilesUsing_FindsEveryManifestReferencingAnEntry()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        library.Import(Item(temp, "2", "Beta", "beta"));

        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha" } });
        library.SaveManifest("solo", new ProfileManifest { Mods = { "alpha", "beta" } });

        Assert.Equal(new[] { "coop", "solo" }, library.ProfilesUsing("alpha"));
        Assert.Equal(new[] { "solo" }, library.ProfilesUsing("beta"));
    }

    [Fact]
    public void ProfilesUsing_IsEmptyForAModNoProfileWants()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        library.SaveManifest("coop", new ProfileManifest());

        Assert.Empty(library.ProfilesUsing("alpha"));
    }

    [Fact]
    public void RemoveFromLibrary_MovesTheModToABackupRatherThanDeletingIt()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));

        var moved = library.RemoveFromLibrary("alpha");

        Assert.False(Directory.Exists(Path.Combine(library.LibraryRoot, "alpha")));
        Assert.True(File.Exists(Path.Combine(moved, "main.lua")));
        Assert.Empty(library.ListEntries());
    }

    [Fact]
    public void RemoveFromLibrary_RefusesWhileAProfileStillReferencesIt()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "1", "Alpha", "alpha"));
        library.SaveManifest("coop", new ProfileManifest { Mods = { "alpha" } });

        var ex = Assert.Throws<UnsafePathException>(() => library.RemoveFromLibrary("alpha"));

        Assert.Contains("coop", ex.Message);
        Assert.True(Directory.Exists(Path.Combine(library.LibraryRoot, "alpha")));
    }

    [Fact]
    public void RemoveFromLibrary_RefusesAnEntryThatIsNotThere()
    {
        using var temp = new TempDir();
        Assert.Throws<UnsafePathException>(() => Build(temp).RemoveFromLibrary("ghost"));
    }
}
