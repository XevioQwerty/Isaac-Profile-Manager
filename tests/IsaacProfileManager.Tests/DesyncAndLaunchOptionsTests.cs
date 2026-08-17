using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class DesyncTableTests
{
    /// <summary>The table straight from the README, where Player2 is the odd one.</summary>
    private const string DesyncLog = """
        [INFO] - Checksums:
        [INFO] -  - Player0: Checksum (1000fc7b), Global RNG checksum (f4d2dca3)
        [INFO] -  - Player1: Checksum (1000fc7b), Global RNG checksum (f4d2dca3)
        [INFO] -  - Player2: Checksum (fb9494d0), Global RNG checksum (0dbd257b)
        """;

    private static IReadOnlyList<LogLine> Read(TempDir temp, string log) =>
        new LogReaderService(temp.File("log.txt", log)).Read();

    [Fact]
    public void Checksums_MarksTheRowThatDisagreesWithTheOthers()
    {
        using var temp = new TempDir();
        var rows = LogReaderService.Checksums(Read(temp, DesyncLog));

        Assert.Equal(3, rows.Count);
        Assert.False(rows.Single(r => r.Player == "Player0").IsOdd);
        Assert.False(rows.Single(r => r.Player == "Player1").IsOdd);
        // Whoever's row differs is the machine to investigate.
        Assert.True(rows.Single(r => r.Player == "Player2").IsOdd);
    }

    [Fact]
    public void Checksums_CatchesAGlobalRngMismatchWhenEntityChecksumsAgree()
    {
        using var temp = new TempDir();
        var rows = LogReaderService.Checksums(Read(temp, """
            [INFO] -  - Player0: Checksum (aaaa), Global RNG checksum (1111)
            [INFO] -  - Player1: Checksum (aaaa), Global RNG checksum (1111)
            [INFO] -  - Player2: Checksum (aaaa), Global RNG checksum (2222)
            """));

        // "No entity desyncs but global RNG differs" is its own diagnosis.
        Assert.True(rows.Single(r => r.Player == "Player2").IsOdd);
    }

    [Fact]
    public void Checksums_MarksNobodyWhenEveryoneAgrees()
    {
        using var temp = new TempDir();
        var rows = LogReaderService.Checksums(Read(temp, """
            [INFO] -  - Player0: Checksum (aaaa), Global RNG checksum (1111)
            [INFO] -  - Player1: Checksum (aaaa), Global RNG checksum (1111)
            """));

        Assert.All(rows, r => Assert.False(r.IsOdd));
    }

    [Fact]
    public void Checksums_IsEmptyForALogWithoutATable()
    {
        using var temp = new TempDir();
        Assert.Empty(LogReaderService.Checksums(Read(temp, "[INFO] - nothing to see")));
    }

    [Fact]
    public void Checksums_DoesNotGuessFromASinglePlayerRow()
    {
        using var temp = new TempDir();
        var rows = LogReaderService.Checksums(Read(temp,
            "[INFO] -  - Player0: Checksum (aaaa), Global RNG checksum (1111)"));

        Assert.Single(rows);
        Assert.False(rows[0].IsOdd);
    }
}

public class SteamLaunchOptionsServiceTests
{
    private static string GiveSteam(TempDir temp, string? launchOptions)
    {
        var root = temp.Dir("Steam");
        temp.Dir("Steam", "userdata", "351019201", "250900", "remote");
        temp.File(@"Steam\userdata\351019201\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore"
            {
            	"Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } }
            }
            """);

        var options = launchOptions is null ? "" : $"\"LaunchOptions\"\t\t\"{launchOptions}\"";
        temp.File($@"Steam\userdata\351019201\config\localconfig.vdf", $$"""
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
            						"Playtime"		"4"
            						{{options}}
            					}
            				}
            			}
            		}
            	}
            }
            """);
        return root;
    }

    private static SteamLaunchOptionsService ServiceFor(string steamRoot) =>
        new(new SteamCloudService(steamRoot, () => false));

    [Fact]
    public void RecognisesTheLauncherLine()
    {
        using var temp = new TempDir();
        // Escaped exactly as Steam stores it: \" around the path, \\ separators.
        var root = GiveSteam(temp,
            @"\""A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth\\REPENTOGONLauncher\\REPENTOGONLauncher.exe\"" --isaac=%command%");

        var status = ServiceFor(root).Check(@"A:\rgon\REPENTOGONLauncher.exe");

        Assert.Equal(LaunchOptionsState.LauncherConfigured, status.State);
        Assert.True(status.IsCorrect);
    }

    [Fact]
    public void FlagsEmptyLaunchOptions_BecauseTheBuildThenNeverFollowsTheProfile()
    {
        using var temp = new TempDir();
        var status = ServiceFor(GiveSteam(temp, null)).Check(@"A:\rgon\REPENTOGONLauncher.exe");

        Assert.Equal(LaunchOptionsState.Empty, status.State);
        Assert.False(status.IsCorrect);
        Assert.Equal(@"""A:\rgon\REPENTOGONLauncher.exe"" --isaac=%command%", status.Suggested);
    }

    [Fact]
    public void FlagsSomethingElseEntirely()
    {
        using var temp = new TempDir();
        var status = ServiceFor(GiveSteam(temp, "-windowed -novid")).Check(@"A:\rgon\REPENTOGONLauncher.exe");

        Assert.Equal(LaunchOptionsState.Other, status.State);
        Assert.Equal("-windowed -novid", status.Current);
    }

    [Fact]
    public void FlagsTheLauncherWithoutCommand_WhichSilentlyDoesNotWork()
    {
        using var temp = new TempDir();
        // Missing %command%, so Steam never passes the game through.
        var status = ServiceFor(GiveSteam(temp, @"\""A:\\rgon\\REPENTOGONLauncher.exe\""")).Check(@"A:\rgon\REPENTOGONLauncher.exe");

        Assert.Equal(LaunchOptionsState.Other, status.State);
    }

    [Fact]
    public void UnknownWhenSteamCannotBeFound()
    {
        using var temp = new TempDir();
        var service = new SteamLaunchOptionsService(new SteamCloudService(temp.Combine("nope"), () => false));

        Assert.Equal(LaunchOptionsState.Unknown, service.Check(null).State);
    }
}
