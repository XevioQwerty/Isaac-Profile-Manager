using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SteamCloudServiceTests
{
    /// <summary>Builds a Steam tree shaped like the real one.</summary>
    private static string GiveSteam(TempDir temp, string account, string? cloudEnabled, bool withApp = true, string? lastSync = "changeslocally")
    {
        var root = temp.Dir("Steam");
        if (withApp) temp.Dir("Steam", "userdata", account, "250900", "remote");

        var appEntry = cloudEnabled is null
            ? "\"252950\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"cloudenabled\"\t\t\"1\"\n\t\t\t\t\t}"
            : $"\"250900\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"cloudenabled\"\t\t\"{cloudEnabled}\"\n\t\t\t\t\t}}";

        temp.File($@"Steam\userdata\{account}\7\remote\sharedconfig.vdf", $$"""
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
            					{{appEntry}}
            				}
            			}
            		}
            	}
            }
            """);

        if (lastSync is not null)
        {
            temp.File($@"Steam\userdata\{account}\config\localconfig.vdf", $$"""
                "UserLocalConfigStore"
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
                						"LastPlayed"		"1786960040"
                						"cloud"
                						{
                							"last_sync_state"		"{{lastSync}}"
                						}
                					}
                				}
                			}
                		}
                	}
                }
                """);
        }

        return root;
    }

    [Fact]
    public void CloudExplicitlyOff_IsTheOnlyStateThatAllowsSwapping()
    {
        using var temp = new TempDir();
        var status = new SteamCloudService(GiveSteam(temp, "351019201", "0")).GetStatus();

        Assert.Equal(SteamCloudState.Disabled, status.State);
        Assert.True(status.SafeToSwapSaves);
        Assert.True(status.ExplicitSetting);
        Assert.Equal("351019201", status.AccountId);
    }

    [Fact]
    public void CloudExplicitlyOn_BlocksSwapping()
    {
        using var temp = new TempDir();
        var status = new SteamCloudService(GiveSteam(temp, "351019201", "1")).GetStatus();

        Assert.Equal(SteamCloudState.Enabled, status.State);
        Assert.False(status.SafeToSwapSaves);
        Assert.True(status.ExplicitSetting);
    }

    [Fact]
    public void AbsentSettingIsTreatedAsOn_BecauseThatIsSteamsDefault()
    {
        using var temp = new TempDir();
        // Only another game has an entry — Isaac has never been toggled.
        var status = new SteamCloudService(GiveSteam(temp, "351019201", cloudEnabled: null)).GetStatus();

        Assert.Equal(SteamCloudState.Enabled, status.State);
        Assert.False(status.SafeToSwapSaves);
        // Being wrong this way costs a warning; the other way costs achievements.
        Assert.False(status.ExplicitSetting);
    }

    [Fact]
    public void ReportsSteamsOwnViewOfTheFolder()
    {
        using var temp = new TempDir();
        var status = new SteamCloudService(GiveSteam(temp, "351019201", "0")).GetStatus();

        Assert.Equal("changeslocally", status.LastSyncState);
    }

    [Fact]
    public void PicksTheAccountThatActuallyOwnsTheGame()
    {
        using var temp = new TempDir();
        var root = GiveSteam(temp, "351019201", "0");
        // Other accounts exist without the app, as on the reference machine.
        temp.Dir("Steam", "userdata", "182703941");
        temp.Dir("Steam", "userdata", "387437005");

        Assert.Equal("351019201", new SteamCloudService(root).GetStatus().AccountId);
    }

    [Fact]
    public void UnknownWhenSteamOrTheAccountCannotBeFound()
    {
        using var temp = new TempDir();

        var noSteam = new SteamCloudService(temp.Combine("nothing-here")).GetStatus();
        Assert.Equal(SteamCloudState.Unknown, noSteam.State);
        Assert.False(noSteam.SafeToSwapSaves);

        var noApp = new SteamCloudService(GiveSteam(temp, "351019201", "0", withApp: false)).GetStatus();
        Assert.Equal(SteamCloudState.Unknown, noApp.State);
    }

    [Fact]
    public void UnknownWhenSharedConfigIsMissing_AndStillNotSafe()
    {
        using var temp = new TempDir();
        var root = temp.Dir("Steam");
        temp.Dir("Steam", "userdata", "351019201", "250900", "remote");

        var status = new SteamCloudService(root).GetStatus();

        Assert.Equal(SteamCloudState.Unknown, status.State);
        Assert.False(status.SafeToSwapSaves);
        Assert.NotNull(status.RemoteDir);
    }

    [Fact]
    public void ADecoyAppsSectionElsewhereInTheFileDoesNotWin()
    {
        using var temp = new TempDir();
        var root = temp.Dir("Steam");
        temp.Dir("Steam", "userdata", "351019201", "250900", "remote");

        // The real localconfig.vdf is 400 KB and contains more than one node
        // called "apps"; a blind tree search can match the wrong one.
        temp.File(@"Steam\userdata\351019201\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore"
            {
            	"Decoy"
            	{
            		"apps"
            		{
            			"250900"
            			{
            				"cloudenabled"		"1"
            			}
            		}
            	}
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

        var status = new SteamCloudService(root).GetStatus();

        // The documented path says off; only the decoy says on.
        Assert.Equal(SteamCloudState.Disabled, status.State);
        Assert.True(status.SafeToSwapSaves);
    }

    [Fact]
    public void ReportsWhenTheSettingWasWritten_SoAStaleReadIsVisible()
    {
        using var temp = new TempDir();
        var root = GiveSteam(temp, "351019201", "1");

        // Steam holds this in memory and flushes on exit, so a value read while
        // it runs can disagree with what the properties dialog shows.
        var running = new SteamCloudService(root, () => true).GetStatus();
        Assert.NotNull(running.SettingWritten);
        Assert.True(running.SteamRunning);
        Assert.True(running.SettingMayBeStale);

        var closed = new SteamCloudService(root, () => false, temp.Dir("cfgbackups")).GetStatus();
        Assert.False(closed.SettingMayBeStale);
    }

    [Fact]
    public void SetCloudEnabled_RefusesWhileSteamIsRunning_BecauseSteamWouldOverwriteIt()
    {
        using var temp = new TempDir();
        var root = GiveSteam(temp, "351019201", "1");
        var service = new SteamCloudService(root, () => true);

        var ex = Assert.Throws<UnsafePathException>(() => service.SetCloudEnabled(false));

        Assert.Contains("Exit Steam completely", ex.Message);
        // Untouched, so nothing is half-changed.
        Assert.Equal(SteamCloudState.Enabled, service.GetStatus().State);
    }

    [Fact]
    public void SetCloudEnabled_FlipsAnExistingEntryAndLeavesOtherGamesAlone()
    {
        using var temp = new TempDir();
        var root = GiveSteam(temp, "351019201", "1");
        var service = new SteamCloudService(root, () => false, temp.Dir("cfgbackups"));

        service.SetCloudEnabled(false);

        Assert.Equal(SteamCloudState.Disabled, service.GetStatus().State);
        // Everything else Steam wrote survives untouched.
        var text = File.ReadAllText(Path.Combine(root, "userdata", "351019201", "7", "remote", "sharedconfig.vdf"));
        Assert.Contains("UserRoamingConfigStore", text);
    }

    [Fact]
    public void SetCloudEnabled_AddsAnEntryWhenTheGameHasNoneYet()
    {
        using var temp = new TempDir();
        // Only another game has an entry, which is the common starting state.
        var root = GiveSteam(temp, "351019201", cloudEnabled: null);
        var service = new SteamCloudService(root, () => false, temp.Dir("cfgbackups"));

        service.SetCloudEnabled(false);

        Assert.Equal(SteamCloudState.Disabled, service.GetStatus().State);
        Assert.True(service.GetStatus().ExplicitSetting);
        // The other game's setting is not disturbed.
        var text = File.ReadAllText(Path.Combine(root, "userdata", "351019201", "7", "remote", "sharedconfig.vdf"));
        Assert.Contains("252950", text);
    }

    [Fact]
    public void SetCloudEnabled_IsReversible()
    {
        using var temp = new TempDir();
        var service = new SteamCloudService(GiveSteam(temp, "351019201", "1"), () => false, temp.Dir("cfgbackups"));

        service.SetCloudEnabled(false);
        Assert.Equal(SteamCloudState.Disabled, service.GetStatus().State);

        service.SetCloudEnabled(true);
        Assert.Equal(SteamCloudState.Enabled, service.GetStatus().State);
    }

    [Fact]
    public void SetCloudEnabled_KeepsACopyOfTheOriginal()
    {
        using var temp = new TempDir();
        var service = new SteamCloudService(GiveSteam(temp, "351019201", "1"), () => false, temp.Dir("cfgbackups"));

        var backup = service.SetCloudEnabled(false);

        Assert.True(File.Exists(backup));
        Assert.Contains("\"cloudenabled\"\t\t\"1\"", File.ReadAllText(backup));
    }

    [Fact]
    public void PropertiesUrlOpensTheDialogHoldingTheToggle()
    {
        Assert.Equal("steam://gameproperties/250900", SteamCloudService.PropertiesUrl());
    }
}
