using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parse_ReadsNestedSectionsAndValues()
    {
        var node = VdfParser.Parse("""
            "AppWorkshop"
            {
            	"appid"		"250900"
            	"WorkshopItemsInstalled"
            	{
            		"835236871"
            		{
            			"size"		"525286"
            		}
            	}
            }
            """);

        Assert.Equal("250900", node["AppWorkshop"]!["appid"]!.Value);
        Assert.Equal("525286", node.Find("WorkshopItemsInstalled")!["835236871"]!["size"]!.Value);
    }

    [Fact]
    public void Parse_SkipsComments()
    {
        var node = VdfParser.Parse("""
            "Root"
            {
            	// a comment Valve writes into these files
            	"key"		"value"
            }
            """);

        Assert.Equal("value", node["Root"]!["key"]!.Value);
    }

    [Fact]
    public void Parse_RefusesImplausibleNesting()
    {
        var text = string.Concat(Enumerable.Repeat("\"a\"\n{\n", 80));
        Assert.Throws<InvalidDataException>(() => VdfParser.Parse(text));
    }
}

public class WorkshopServiceTests
{
    /// <summary>
    /// Shaped like the real file: every id appears in both sections, which is
    /// what makes a naive id-matching count come out at double.
    /// </summary>
    private const string Acf = """
        "AppWorkshop"
        {
        	"appid"		"250900"
        	"SizeOnDisk"		"1806364231"
        	"WorkshopItemsInstalled"
        	{
        		"835236871"
        		{
        			"size"		"525286"
        			"manifest"		"2107955703174289857"
        		}
        		"3127536138"
        		{
        			"size"		"1021555"
        			"manifest"		"9999999999999999999"
        		}
        	}
        	"WorkshopItemDetails"
        	{
        		"835236871"
        		{
        			"manifest"		"2107955703174289857"
        			"subscribedby"		"351019201"
        		}
        		"3127536138"
        		{
        			"manifest"		"9999999999999999999"
        			"subscribedby"		"351019201"
        		}
        	}
        }
        """;

    private static WorkshopService Build(TempDir temp, string acf = Acf)
    {
        var workshop = temp.Dir("workshop");
        temp.File(@"workshop\appworkshop_250900.acf", acf);
        return new WorkshopService(workshop);
    }

    private static void GiveContent(TempDir temp, string id, string name, string directory, string description = "desc")
    {
        temp.File($@"workshop\content\250900\{id}\metadata.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
                <name>{name}</name>
                <directory>{directory}</directory>
                <description>{description}</description>
                <id>{id}</id>
            </metadata>
            """);
        temp.File($@"workshop\content\250900\{id}\main.lua", "-- mod");
    }

    [Fact]
    public void GetSubscribedIds_CountsEachItemOnce()
    {
        using var temp = new TempDir();
        var service = Build(temp);

        // Both sections list both ids; the answer is two, not four.
        Assert.Equal(new[] { "3127536138", "835236871" }, service.GetSubscribedIds().OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GetItems_ReadsNameDirectoryAndDescriptionFromContent()
    {
        using var temp = new TempDir();
        var service = Build(temp);
        GiveContent(temp, "835236871", "Better Character Menu", "better character menu");
        GiveContent(temp, "3127536138", "[BETA] REPENTOGON", "repentogon");

        var items = service.GetItems();

        var rgon = items.Single(i => i.Id == "3127536138");
        Assert.Equal("[BETA] REPENTOGON", rgon.Name);
        Assert.Equal("repentogon", rgon.Directory);
        // This is the name Isaac materialises into mods\.
        Assert.Equal("repentogon_3127536138", rgon.MaterialisedFolderName);
        Assert.True(rgon.ContentPresent);
    }

    [Fact]
    public void GetItems_FallsBackToTheIdWhenMetadataIsMissingOrBroken()
    {
        using var temp = new TempDir();
        var service = Build(temp);
        temp.File(@"workshop\content\250900\835236871\metadata.xml", "<metadata><name>oops");

        var items = service.GetItems();

        // Broken metadata must not drop the item out of the list.
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public void MissingFromProfile_NamesExactlyWhatSteamWouldPutBack()
    {
        using var temp = new TempDir();
        var service = Build(temp);
        GiveContent(temp, "835236871", "Better Character Menu", "better character menu");
        GiveContent(temp, "3127536138", "[BETA] REPENTOGON", "repentogon");

        var profile = temp.Dir("profile");
        Directory.CreateDirectory(Path.Combine(profile, "better character menu_835236871"));

        var missing = service.MissingFromProfile(profile, service.GetItems());

        Assert.Single(missing);
        Assert.Equal("3127536138", missing[0].Id);
    }

    [Fact]
    public void FindDuplicatePairs_SpotsABakedCopyBesideItsWorkshopOriginal()
    {
        using var temp = new TempDir();
        var profile = temp.Dir("profile");
        foreach (var name in new[] { "repentogon", "repentogon_3127536138", "eid", "unrelated_mod" })
            Directory.CreateDirectory(Path.Combine(profile, name));

        var duplicates = WorkshopService.FindDuplicatePairs(profile);

        Assert.Single(duplicates);
        Assert.Contains("repentogon", duplicates[0]);
    }

    [Fact]
    public void FindDuplicatePairs_DoesNotFlagAModWhoseOwnNameEndsInDigits()
    {
        using var temp = new TempDir();
        var profile = temp.Dir("profile");
        // golden-items_3338467278 is a real mod name; without a bare twin it is not a duplicate.
        Directory.CreateDirectory(Path.Combine(profile, "golden-items_3338467278_3338495603"));
        Directory.CreateDirectory(Path.Combine(profile, "stageapi15_1348031964"));

        Assert.Empty(WorkshopService.FindDuplicatePairs(profile));
    }

    [Fact]
    public void ResolveWorkshopRoot_DerivesItFromTheGameDirectory()
    {
        using var temp = new TempDir();
        var gameDir = temp.Dir("SteamLibrary", "steamapps", "common", "The Binding of Isaac Rebirth");
        var workshop = temp.Dir("SteamLibrary", "steamapps", "workshop");

        Assert.Equal(workshop, WorkshopService.ResolveWorkshopRoot(gameDir), ignoreCase: true);
    }

    [Fact]
    public void ResolveWorkshopRoot_ReturnsNullForANonSteamLayout()
    {
        using var temp = new TempDir();
        Assert.Null(WorkshopService.ResolveWorkshopRoot(temp.Dir("Games", "Isaac")));
        Assert.Null(WorkshopService.ResolveWorkshopRoot(null));
    }

    [Fact]
    public void MissingAcfIsReportedNotThrown()
    {
        using var temp = new TempDir();
        var service = new WorkshopService(temp.Combine("no-workshop-here"));

        Assert.False(service.IsAvailable);
        Assert.Empty(service.GetSubscribedIds());
        Assert.Empty(service.GetItems());
    }
}
