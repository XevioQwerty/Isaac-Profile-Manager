using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SaveSetEditingTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private static (SaveSetService Service, string Remote) Build(TempDir temp)
    {
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", "351019201", "250900", "remote");
        temp.File(@"Steam\userdata\351019201\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore"
            {
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"apps"
            				{
            					"250900"
            					{
            						"cloudenabled"		"0"
            					}
            				}
            			}
            		}
            	}
            }
            """);

        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata1.dat"), "slot 1");
        File.WriteAllText(Path.Combine(remote, "rgon_steam_persistentgamedata2.dat"), "slot 2");

        return (new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync")), remote);
    }

    [Fact]
    public void EditMetadata_ChangesTheDescriptiveFieldsAndNothingElse()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        var original = service.Capture("friday", "RPTG_v1.0", new[] { "alex" }, "first pass");

        var edited = service.EditMetadata("friday",
            notes: "slot 2 is the no-mods run",
            players: new[] { "alex", "sam" },
            modProfile: "Vanilla+_v1.0");

        Assert.Equal("slot 2 is the no-mods run", edited.Notes);
        Assert.Equal(new[] { "alex", "sam" }, edited.Players);
        Assert.Equal("Vanilla+_v1.0", edited.ModProfile);

        // What the set is a record of must not change.
        Assert.Equal(original.Build, edited.Build);
        Assert.Equal(original.Files, edited.Files);
        Assert.Equal(original.Sha1, edited.Sha1);
        Assert.Equal(original.CapturedUtc, edited.CapturedUtc);
    }

    [Fact]
    public void EditMetadata_KeepsPerSlotNotes()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        service.Capture("friday", "p");

        service.EditMetadata("friday", slotNotes: new Dictionary<string, string>
        {
            ["1"] = "main co-op save",
            ["2"] = "new run, mods disabled",
            ["3"] = "   ",
        });

        var reloaded = service.LoadSet("friday")!;
        Assert.Equal("main co-op save", reloaded.SlotNotes["1"]);
        Assert.Equal("new run, mods disabled", reloaded.SlotNotes["2"]);
        // Blank notes are dropped rather than stored as empty strings.
        Assert.False(reloaded.SlotNotes.ContainsKey("3"));
    }

    [Fact]
    public void EditMetadata_LeavesOmittedFieldsAlone()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        service.Capture("friday", "RPTG_v1.0", new[] { "alex" }, "keep me");

        service.EditMetadata("friday", notes: "changed");

        var reloaded = service.LoadSet("friday")!;
        Assert.Equal("changed", reloaded.Notes);
        Assert.Equal(new[] { "alex" }, reloaded.Players);
        Assert.Equal("RPTG_v1.0", reloaded.ModProfile);
    }

    [Fact]
    public void EditMetadata_RefusesAnUnknownSet()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);

        Assert.Throws<UnsafePathException>(() => service.EditMetadata("nope", notes: "x"));
    }

    [Fact]
    public void Rename_MovesTheFolderAndKeepsTheFiles()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        service.Capture("friday", "p", notes: "the good one");

        var renamed = service.Rename("friday", "friday-coop");

        Assert.Equal("friday-coop", renamed.Name);
        Assert.Equal(new[] { "friday-coop" }, service.ListSets());
        Assert.Equal("the good one", service.LoadSet("friday-coop")!.Notes);
        Assert.True(File.Exists(Path.Combine(service.SetsRoot, "friday-coop", "rgon_steam_persistentgamedata1.dat")));
    }

    [Fact]
    public void Rename_RefusesACollisionOrAnUnusableName()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        service.Capture("one", "p");
        service.Capture("two", "p");

        Assert.Throws<UnsafePathException>(() => service.Rename("one", "two"));
        Assert.Throws<ArgumentException>(() => service.Rename("one", @"bad\name"));
        Assert.Equal(2, service.ListSets().Count);
    }

    [Fact]
    public void DeleteSet_MovesItToABackupRatherThanDeletingIt()
    {
        using var temp = new TempDir();
        var (service, _) = Build(temp);
        service.Capture("obsolete", "p");

        var moved = service.DeleteSet("obsolete");

        Assert.Empty(service.ListSets());
        Assert.True(File.Exists(Path.Combine(moved, "rgon_steam_persistentgamedata1.dat")));
    }
}

public class WorkshopUrlTests
{
    [Fact]
    public void SubscribedItemsUrl_ConvertsTheAccountIdToTheProfileForm()
    {
        // 76561197960265728 + 351019201, the reference account.
        var url = WorkshopService.SubscribedItemsUrl("351019201");

        Assert.Contains("76561198311284929", url);
        Assert.Contains("appid=250900", url);
        Assert.Contains("browsefilter=mysubscriptions", url);
    }

    [Fact]
    public void SubscribedItemsUrl_IsNullWithoutAUsableAccountId()
    {
        Assert.Null(WorkshopService.SubscribedItemsUrl(null));
        Assert.Null(WorkshopService.SubscribedItemsUrl(""));
        Assert.Null(WorkshopService.SubscribedItemsUrl("not-a-number"));
    }

    [Fact]
    public void BrowseAndItemUrlsPointAtTheRightApp()
    {
        Assert.Equal("https://steamcommunity.com/app/250900/workshop/", WorkshopService.BrowseUrl);
        Assert.Contains("id=3127536138", WorkshopService.ItemUrl("3127536138"));
    }

    [Fact]
    public void InSteamClient_WrapsTheUrlSoItOpensInsideSteam()
    {
        // Subscribing needs a logged-in Steam session, so these must not open in
        // a system browser.
        Assert.Equal("steam://openurl/https://steamcommunity.com/app/250900/workshop/",
                     WorkshopService.InSteamClient(WorkshopService.BrowseUrl));

        Assert.StartsWith("steam://openurl/https://steamcommunity.com/profiles/",
                          WorkshopService.InSteamClient(WorkshopService.SubscribedItemsUrl("351019201")!));

        Assert.Equal("steam://openurl/https://steamcommunity.com/sharedfiles/filedetails/?id=3127536138",
                     WorkshopService.InSteamClient(WorkshopService.ItemUrl("3127536138")));
    }
}
