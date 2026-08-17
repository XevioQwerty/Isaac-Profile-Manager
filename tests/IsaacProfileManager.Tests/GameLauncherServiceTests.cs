using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class GameLauncherServiceTests
{
    private readonly GameLauncherService _launcher = new();

    [Fact]
    public void ResolveMethod_FollowsTheExplicitSetting()
    {
        Assert.Equal(GameLaunchMethod.File, _launcher.ResolveMethod(new AppConfig { LaunchMethod = "File" }));
        Assert.Equal(GameLaunchMethod.Steam, _launcher.ResolveMethod(new AppConfig { LaunchMethod = "steam" }));
    }

    [Fact]
    public void ResolveMethod_FallsBackToHowSetupRecordedTheInstall()
    {
        Assert.Equal(GameLaunchMethod.Steam, _launcher.ResolveMethod(new AppConfig { OwnsOnSteam = true }));
        Assert.Equal(GameLaunchMethod.File, _launcher.ResolveMethod(new AppConfig { OwnsOnSteam = false }));
    }

    [Fact]
    public void Resolve_SteamUsesTheProtocolUrlAndNeedsNoPathsOnDisk()
    {
        var plan = _launcher.Resolve(new AppConfig { LaunchMethod = "Steam" });

        Assert.Equal("steam://rungameid/250900", plan.Target);
        Assert.Equal(string.Empty, plan.Arguments);
    }

    [Fact]
    public void Resolve_TheLauncherIsGivenTheVanillaExePath()
    {
        using var temp = new TempDir();
        var launcherExe = temp.File(@"REPENTOGONLauncher\REPENTOGONLauncher.exe", "MZ");
        var isaac = temp.File(@"game\isaac-ng.exe", "MZ");

        var plan = _launcher.Resolve(new AppConfig
        {
            LaunchMethod = "File", LaunchTarget = launcherExe, IsaacExe = isaac,
        });

        // The launcher resolves the Repentogon build itself from the vanilla path.
        Assert.Equal(launcherExe, plan.Target);
        Assert.Equal($"--isaac=\"{isaac}\"", plan.Arguments);
    }

    [Fact]
    public void Resolve_RefusesTheRepentogonBuildExeWithTheReasonItWouldFail()
    {
        using var temp = new TempDir();
        var buildExe = temp.File(@"game\Repentogon\isaac-ng.exe", "MZ");
        temp.File(@"game\Repentogon\.repentogon", "");

        var ex = Assert.Throws<UnsafePathException>(() => _launcher.Resolve(new AppConfig
        {
            LaunchMethod = "File", LaunchTarget = buildExe,
        }));

        Assert.Contains("refuses to be started directly", ex.Message);
        Assert.Contains("REPENTOGONLauncher.exe", ex.Message);
    }

    [Fact]
    public void Resolve_AllowsTheVanillaExeWhichHasNoSuchMarker()
    {
        using var temp = new TempDir();
        var isaac = temp.File(@"game\isaac-ng.exe", "MZ");

        var plan = _launcher.Resolve(new AppConfig { LaunchMethod = "File", LaunchTarget = isaac });

        Assert.Equal(isaac, plan.Target);
        Assert.Equal(string.Empty, plan.Arguments);
    }

    [Fact]
    public void Resolve_FallsBackToTheConfiguredLauncherThenTheGameExe()
    {
        using var temp = new TempDir();
        var launcherExe = temp.File(@"rgon\REPENTOGONLauncher.exe", "MZ");
        var isaac = temp.File(@"game\isaac-ng.exe", "MZ");

        Assert.Equal(launcherExe, _launcher.Resolve(new AppConfig
        {
            LaunchMethod = "File", LauncherExe = launcherExe, IsaacExe = isaac,
        }).Target);

        Assert.Equal(isaac, _launcher.Resolve(new AppConfig
        {
            LaunchMethod = "File", IsaacExe = isaac,
        }).Target);
    }

    [Fact]
    public void Resolve_RefusesAMissingOrUnsetTarget()
    {
        using var temp = new TempDir();

        Assert.Throws<UnsafePathException>(() => _launcher.Resolve(new AppConfig { LaunchMethod = "File" }));

        var ex = Assert.Throws<UnsafePathException>(() => _launcher.Resolve(new AppConfig
        {
            LaunchMethod = "File", LaunchTarget = temp.Combine("gone.exe"),
        }));
        Assert.Contains("does not exist", ex.Message);
    }
}
