using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class LaunchGuardTests
{
    private static LiveIdentity Live(SaveSet set, LiveSaveState state = LiveSaveState.Exact) =>
        new(state, set, Array.Empty<string>(), set.Files.Count);

    private static SaveSet Set(GameBuild build, string profile = "RPTG", string? version = "J273") => new()
    {
        Name = "friday",
        Build = build,
        ModProfile = profile,
        GameVersion = version,
        Files = new List<string> { "rgon_steam_persistentgamedata1.dat" },
    };

    private static LaunchContext Context(LiveIdentity identity, string? activeProfile = "RPTG",
                                         GameBuild launcher = GameBuild.Repentogon, string? machineVersion = "J273",
                                         bool running = false, bool anySets = true) =>
        new(identity, activeProfile, launcher, machineVersion, running, anySets);

    [Fact]
    public void EverythingMatches_IsClean()
    {
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon))));

        Assert.True(verdict.IsClean);
        Assert.True(verdict.CanLaunch);
        Assert.Null(verdict.Worst);
    }

    [Fact]
    public void BuildMismatch_Blocks_WithTheFixAttached()
    {
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon)), launcher: GameBuild.Vanilla));

        Assert.False(verdict.CanLaunch);
        var finding = Assert.Single(verdict.Findings, f => f.Severity == GuardSeverity.Block);
        Assert.Equal(GuardFix.SwitchBuild, finding.Fix);
        Assert.Equal("REPENTOGON", finding.FixTarget);
    }

    [Fact]
    public void BothBuildsSet_MatchesEitherLauncher()
    {
        Assert.True(LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Both)), launcher: GameBuild.Vanilla)).CanLaunch);
        Assert.True(LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Both)), launcher: GameBuild.Repentogon)).CanLaunch);
    }

    [Fact]
    public void GameVersionDiffers_IsANote_NeverABlock()
    {
        // Same build, different patch: a retail update migrates the save. The
        // cross-build case is what blocks, and that is the Build check.
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Vanilla, version: "J460")), launcher: GameBuild.Vanilla, machineVersion: "J470"));

        Assert.True(verdict.CanLaunch);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(GuardSeverity.Warn, finding.Severity);
        Assert.Contains("J460", finding.Title);
        Assert.Contains("J470", finding.Title);
    }

    [Fact]
    public void TheVersionComparedIsTheLaunchedBuilds_NotTheLastRunOfAnyBuild()
    {
        // The caller passes the version for the build being launched. A vanilla
        // set captured on J460, launching vanilla whose last run here was J460,
        // is clean even though the machine's most recent session was REPENTOGON.
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Vanilla, version: "J460")), launcher: GameBuild.Vanilla, machineVersion: "J460"));
        Assert.True(verdict.IsClean);
    }

    [Fact]
    public void GameVersionUnknownOnEitherSide_OnlyWarns()
    {
        var setUnknown = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon, version: null))));
        var machineUnknown = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon)), machineVersion: null));

        Assert.True(setUnknown.CanLaunch);
        Assert.True(machineUnknown.CanLaunch);
        Assert.All(setUnknown.Findings.Concat(machineUnknown.Findings), f => Assert.Equal(GuardSeverity.Warn, f.Severity));
    }

    [Fact]
    public void ProfileMismatch_Recommends_AndNeverBlocks()
    {
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon, profile: "Vanilla+")), activeProfile: "RPTG"));

        Assert.True(verdict.CanLaunch);
        var finding = Assert.Single(verdict.Findings);
        Assert.Equal(GuardSeverity.Recommend, finding.Severity);
        Assert.Equal(GuardFix.SwitchProfile, finding.Fix);
        Assert.Equal("Vanilla+", finding.FixTarget);
    }

    [Fact]
    public void ProfileComparison_IgnoresCase()
    {
        var verdict = LaunchGuardService.Evaluate(Context(Live(Set(GameBuild.Repentogon, profile: "rptg")), activeProfile: "RPTG"));
        Assert.True(verdict.IsClean);
    }

    [Fact]
    public void UnrecognisedSaves_Warn_OnlyWhenThereAreSetsToMatch()
    {
        var unknown = new LiveIdentity(LiveSaveState.Unrecognised, null, Array.Empty<string>(), 2);

        Assert.Single(LaunchGuardService.Evaluate(Context(unknown, anySets: true)).Findings);
        Assert.True(LaunchGuardService.Evaluate(Context(unknown, anySets: false)).IsClean);
    }

    [Fact]
    public void NoSaves_Warns_AndSaysAFreshSaveWillBeWritten()
    {
        var none = new LiveIdentity(LiveSaveState.NoSaves, null, Array.Empty<string>(), 0);
        var verdict = LaunchGuardService.Evaluate(Context(none));

        Assert.True(verdict.CanLaunch);
        Assert.Contains(verdict.Findings, f => f.Severity == GuardSeverity.Warn && f.Detail.Contains("fresh"));
    }

    [Fact]
    public void Findings_AreOrderedMostSevereFirst()
    {
        var verdict = LaunchGuardService.Evaluate(
            Context(Live(Set(GameBuild.Repentogon, profile: "Other")), launcher: GameBuild.Vanilla, running: true));

        Assert.Equal(GuardSeverity.Block, verdict.Findings[0].Severity);
        Assert.Equal(GuardSeverity.Block, verdict.Worst);
        Assert.Equal(GuardSeverity.Warn, verdict.Findings[^1].Severity);
    }
}
