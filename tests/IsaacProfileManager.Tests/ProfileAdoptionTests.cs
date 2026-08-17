using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Turning a profile of real mod copies — how profiles looked before the library
/// existed — into a profile of links, without ever losing a folder.
/// </summary>
public class ProfileAdoptionTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static void GiveLibraryEntry(TempDir temp, string name)
    {
        temp.File($@"sync\.library\{name}\main.lua", $"-- {name}");
    }

    private static void GiveRealProfileFolder(TempDir temp, string profile, string folder)
    {
        temp.File($@"sync\{profile}\{folder}\main.lua", $"-- {folder}");
    }

    [Fact]
    public void SuggestLibraryEntry_MatchesThroughTheWorkshopSuffix()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveLibraryEntry(temp, "astro-items");

        Assert.Equal("astro-items", library.SuggestLibraryEntry("astro-items_3260980911"));
        Assert.Equal("astro-items", library.SuggestLibraryEntry("astro-items"));
        Assert.Null(library.SuggestLibraryEntry("something-else_1234567"));
    }

    [Fact]
    public void SuggestLibraryEntry_DoesNotStripAModsOwnTrailingDigits()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        // The mod's real folder name ends in an id; it is not a workshop suffix.
        GiveLibraryEntry(temp, "golden-items_3338467278");

        Assert.Equal("golden-items_3338467278", library.SuggestLibraryEntry("golden-items_3338467278_3338495603"));
    }

    [Fact]
    public void Analyse_SeparatesLinksFromRedundantCopiesFromHandInstalledMods()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveLibraryEntry(temp, "astro-items");
        GiveLibraryEntry(temp, "minimapi");

        GiveRealProfileFolder(temp, "coop", "astro-items_3260980911");   // duplicates the library
        GiveRealProfileFolder(temp, "coop", "External Item Descriptions"); // hand installed, nowhere else
        new JunctionService().Create(temp.Combine("sync", "coop", "minimapi"),
                                     temp.Combine("sync", ".library", "minimapi"));

        var entries = library.Analyse("coop");

        Assert.True(entries.Single(e => e.Name == "minimapi").IsLink);
        Assert.Equal("minimapi", entries.Single(e => e.Name == "minimapi").LibraryEntry);
        Assert.True(entries.Single(e => e.Name == "astro-items_3260980911").IsRedundantCopy);
        Assert.True(entries.Single(e => e.Name == "External Item Descriptions").NeedsAdopting);
    }

    [Fact]
    public void AdoptIntoLibrary_MovesAHandInstalledModInAndLinksItBack()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveRealProfileFolder(temp, "coop", "External Item Descriptions");

        var entry = library.AdoptIntoLibrary("coop", "External Item Descriptions");

        Assert.Equal("External Item Descriptions", entry);
        // Moved, not copied: it exists in exactly one place, reachable through the link.
        Assert.True(File.Exists(temp.Combine("sync", ".library", entry, "main.lua")));
        Assert.True(new JunctionService().IsJunction(temp.Combine("sync", "coop", entry)));
        Assert.Equal("-- External Item Descriptions",
                     File.ReadAllText(temp.Combine("sync", "coop", entry, "main.lua")));
    }

    [Fact]
    public void AdoptIntoLibrary_DropsTheWorkshopSuffixFromTheNewEntry()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveRealProfileFolder(temp, "coop", "thepunished_2809303353");

        var entry = library.AdoptIntoLibrary("coop", "thepunished_2809303353");

        Assert.Equal("thepunished", entry);
        Assert.True(Directory.Exists(temp.Combine("sync", ".library", "thepunished")));
        Assert.False(Directory.Exists(temp.Combine("sync", "coop", "thepunished_2809303353")));
    }

    [Fact]
    public void AdoptIntoLibrary_RefusesWhenTheLibraryAlreadyHasThatEntry()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveLibraryEntry(temp, "astro-items");
        GiveRealProfileFolder(temp, "coop", "astro-items_3260980911");

        var ex = Assert.Throws<UnsafePathException>(() => library.AdoptIntoLibrary("coop", "astro-items_3260980911"));

        Assert.Contains("Replace the copy with a link", ex.Message);
        Assert.True(File.Exists(temp.Combine("sync", "coop", "astro-items_3260980911", "main.lua")));
    }

    [Fact]
    public void ReplaceWithLink_BacksUpTheCopyRatherThanDeletingIt()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveLibraryEntry(temp, "astro-items");
        GiveRealProfileFolder(temp, "coop", "astro-items_3260980911");

        library.ReplaceWithLink("coop", "astro-items_3260980911", "astro-items");

        Assert.True(new JunctionService().IsJunction(temp.Combine("sync", "coop", "astro-items")));
        Assert.False(Directory.Exists(temp.Combine("sync", "coop", "astro-items_3260980911")));

        // The displaced copy is still on disk, under .backup, in case the match was wrong.
        var backups = Directory.GetFiles(library.BackupRoot, "main.lua", SearchOption.AllDirectories);
        Assert.Single(backups);
        Assert.Equal("-- astro-items_3260980911", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void ReplaceWithLink_RefusesWhenTheLibraryEntryDoesNotExist()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveRealProfileFolder(temp, "coop", "astro-items_3260980911");

        Assert.Throws<UnsafePathException>(() => library.ReplaceWithLink("coop", "astro-items_3260980911", "nope"));
        Assert.True(File.Exists(temp.Combine("sync", "coop", "astro-items_3260980911", "main.lua")));
    }

    [Fact]
    public void AdoptThenMaterialise_ProducesAProfileOfLinksAndAPortableManifest()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        GiveLibraryEntry(temp, "minimapi");
        GiveRealProfileFolder(temp, "coop", "External Item Descriptions");
        GiveRealProfileFolder(temp, "coop", "minimapi_1978904635");

        var adopted = library.AdoptIntoLibrary("coop", "External Item Descriptions");
        library.ReplaceWithLink("coop", "minimapi_1978904635", "minimapi");

        var manifest = new ProfileManifest { Mods = { adopted, "minimapi" } };
        library.SaveManifest("coop", manifest);
        var report = library.Materialise("coop", manifest);

        Assert.True(report.IsClean);
        Assert.Equal(0, report.ChangeCount);   // both links already correct
        Assert.All(library.Analyse("coop"), e => Assert.True(e.IsLink));
    }
}
