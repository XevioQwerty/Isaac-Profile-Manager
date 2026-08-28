using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Editing Steam's localconfig.vdf.
///
/// This file is ~440 KB of Steam's own state, it holds several sections named
/// "apps", and getting the escaping wrong silently breaks the launch options
/// for a game. Every test here works on text, never a real Steam install.
/// </summary>
public class SteamLaunchOptionsWriteTests
{
    /// <summary>
    /// Shaped like the real thing: the game's node lives under a specific key
    /// path, and there are two decoy "apps" sections — one before it under a
    /// different store, and a nested one deeper in.
    /// </summary>
    private const string Config = """
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
					"440"
					{
						"LastPlayed"		"1787906829"
						"LaunchOptions"		"-novid"
					}
					"250900"
					{
						"LastPlayed"		"1787906829"
						"cloud"
						{
							"last_sync_state"		"changeslocally"
							"LaunchOptions"		"decoy, nested one level down"
						}
						"BadgeData"		"020000000825"
					}
				}
			}
		}
	}
	"apps"
	{
		"250900"
		{
			"LaunchOptions"		"decoy, wrong key path"
		}
	}
}
""";

    private static string Value(string text, string appId)
    {
        var root = VdfParser.Parse(text);
        return root["UserLocalConfigStore"]!["Software"]!["Valve"]!["Steam"]!["apps"]![appId]!["LaunchOptions"]!.Value!;
    }

    [Fact]
    public void SetLaunchOptions_AddsTheKeyToTheRightAppNode()
    {
        var edited = SteamLaunchOptionsService.SetLaunchOptions(
            Config, "250900", @"""C:\Games\REPENTOGONLauncher.exe"" --isaac=%command%", out var changed);

        Assert.True(changed);
        Assert.NotNull(edited);

        // The decoys must be untouched, and the real node must have the value.
        Assert.Contains(@"""LaunchOptions""		""-novid""", edited);
        Assert.Contains("decoy, nested one level down", edited);
        Assert.Contains("decoy, wrong key path", edited);
    }

    [Fact]
    public void SetLaunchOptions_EscapesQuotesAndBackslashesTheWaySteamDoes()
    {
        // Verified against a real localconfig.vdf: quotes are \" and every
        // backslash in a path is doubled.
        var edited = SteamLaunchOptionsService.SetLaunchOptions(
            Config, "250900",
            @"""A:\SteamLibrary\steamapps\common\The Binding of Isaac Rebirth\REPENTOGONLauncher\REPENTOGONLauncher.exe"" --isaac=%command%",
            out _);

        Assert.Contains(
            @"""\""A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth\\REPENTOGONLauncher\\REPENTOGONLauncher.exe\"" --isaac=%command%""",
            edited);
    }

    [Fact]
    public void SetLaunchOptions_ReplacesAnExistingValueRatherThanAddingASecond()
    {
        var once = SteamLaunchOptionsService.SetLaunchOptions(Config, "250900", "first", out _)!;
        var twice = SteamLaunchOptionsService.SetLaunchOptions(once, "250900", "second", out var changed)!;

        Assert.True(changed);
        Assert.Equal("second", Value(twice, "250900"));

        // One key, not two stacked on top of each other.
        var occurrences = twice.Split("\"LaunchOptions\"").Length - 1;
        Assert.Equal(4, occurrences);   // 440, the two decoys, and ours
    }

    [Fact]
    public void SetLaunchOptions_LeavesTheFileAloneWhenItIsAlreadyRight()
    {
        var once = SteamLaunchOptionsService.SetLaunchOptions(Config, "250900", "already set", out _)!;
        var again = SteamLaunchOptionsService.SetLaunchOptions(once, "250900", "already set", out var changed);

        Assert.False(changed);
        Assert.Equal(once, again);
    }

    [Fact]
    public void SetLaunchOptions_DoesNotTouchAnotherGamesOptions()
    {
        var edited = SteamLaunchOptionsService.SetLaunchOptions(Config, "250900", "ours", out _)!;

        Assert.Equal("-novid", Value(edited, "440"));
        Assert.Equal("ours", Value(edited, "250900"));
    }

    [Fact]
    public void SetLaunchOptions_ReportsNothingWhenTheGameHasNoNode()
    {
        // Steam only records a game once it has been launched. Inventing the
        // node would mean guessing at a structure Steam owns.
        Assert.Null(SteamLaunchOptionsService.SetLaunchOptions(Config, "9999999", "x", out var changed));
        Assert.False(changed);
    }

    [Fact]
    public void SetLaunchOptions_KeepsEveryOtherByteIdentical()
    {
        var edited = SteamLaunchOptionsService.SetLaunchOptions(Config, "250900", "ours", out _)!;

        // Removing the one line we added must give back exactly the original.
        var withoutAddition = string.Join("\n",
            edited.ReplaceLineEndings("\n").Split('\n')
                  .Where(line => !line.Contains("\"ours\"")));

        Assert.Equal(Config.ReplaceLineEndings("\n"), withoutAddition);
    }

    [Fact]
    public void Suggest_ProducesTheLineTheLauncherDocsAskFor()
    {
        Assert.Equal(@"""C:\Games\REPENTOGONLauncher.exe"" --isaac=%command%",
                     SteamLaunchOptionsService.Suggest(@"C:\Games\REPENTOGONLauncher.exe"));
    }
}
