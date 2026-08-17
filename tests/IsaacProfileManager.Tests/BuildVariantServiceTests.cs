using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

public class BuildVariantServiceTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private static (BuildVariantService Service, AppConfig Config, FakeProcessService Process) Build(TempDir temp)
    {
        var gameDir = temp.Dir("game");
        var config = new AppConfig { GameDir = gameDir, ModsDir = Path.Combine(gameDir, "mods") };
        var process = new FakeProcessService();
        var store = new ConfigStore(Path.Combine(temp.Path, ConfigStore.FileName));
        return (new BuildVariantService(new JunctionService(), process, store), config, process);
    }

    /// <summary>An installed REPENTOGON build sitting in the game directory, as it arrives.</summary>
    private static void GiveRealBuildFolder(TempDir temp)
    {
        temp.File(@"game\Repentogon\isaac-ng.exe", "MZ");
        temp.File(@"game\Repentogon\libzhl.dll", "dll");
        temp.File(@"game\Repentogon\resources\packed\graphics.a", "packed");
    }

    [Fact]
    public void Initialize_MovesTheInstalledBuildIntoTheBuildRootAndSeedsBothVariants()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);

        service.Initialize(config);

        var vanilla = temp.Combine("game", "~", "Vanilla");
        var onlineFix = temp.Combine("game", "~", "OnlineFix");

        Assert.True(File.Exists(Path.Combine(vanilla, "isaac-ng.exe")));
        Assert.True(File.Exists(Path.Combine(vanilla, "resources", "packed", "graphics.a")));
        // Both variants start as identical copies of what was already installed.
        Assert.True(File.Exists(Path.Combine(onlineFix, "isaac-ng.exe")));
        Assert.True(File.Exists(Path.Combine(onlineFix, "resources", "packed", "graphics.a")));

        var status = service.GetStatus(config);
        Assert.Equal(BuildLinkState.Linked, status.State);
        Assert.Equal("Vanilla", status.ActiveVariant);
        Assert.True(status.IsReady);
    }

    [Fact]
    public void Initialize_IsIdempotentOnAnAlreadyLinkedInstall()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        service.Initialize(config);
        File.WriteAllText(temp.Combine("game", "~", "OnlineFix", "OnlineFix.dll"), "user supplied");

        service.Initialize(config);

        // A second run must not re-copy over what the user put in the variant.
        Assert.Equal("user supplied", File.ReadAllText(temp.Combine("game", "~", "OnlineFix", "OnlineFix.dll")));
        Assert.Equal("Vanilla", service.GetStatus(config).ActiveVariant);
    }

    [Fact]
    public void Initialize_RefusesWhenBothARealFolderAndABaselineVariantExist()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        temp.File(@"game\~\Vanilla\isaac-ng.exe", "a different build");

        var ex = Assert.Throws<UnsafePathException>(() => service.Initialize(config));

        Assert.Contains("Refusing to guess", ex.Message);
        // Neither candidate was touched.
        Assert.True(File.Exists(temp.Combine("game", "Repentogon", "isaac-ng.exe")));
        Assert.True(File.Exists(temp.Combine("game", "~", "Vanilla", "isaac-ng.exe")));
    }

    [Fact]
    public void Initialize_RefusesWhenThereIsNoBuildToWorkFrom()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);

        Assert.Throws<UnsafePathException>(() => service.Initialize(config));
    }

    [Fact]
    public void Switch_RepointsTheLinkWithoutCopyingAnything()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        service.Initialize(config);
        File.WriteAllText(temp.Combine("game", "~", "OnlineFix", "marker.txt"), "alternate");

        service.Switch(config, "OnlineFix");

        var status = service.GetStatus(config);
        Assert.Equal("OnlineFix", status.ActiveVariant);
        Assert.Equal("alternate", File.ReadAllText(temp.Combine("game", "Repentogon", "marker.txt")));
        Assert.Equal("OnlineFix", config.ActiveBuildVariant);

        service.Switch(config, "Vanilla");
        Assert.Equal("Vanilla", service.GetStatus(config).ActiveVariant);
        // Round trip leaves both variant folders whole.
        Assert.True(File.Exists(temp.Combine("game", "~", "OnlineFix", "marker.txt")));
        Assert.True(File.Exists(temp.Combine("game", "~", "Vanilla", "isaac-ng.exe")));
    }

    [Fact]
    public void Switch_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, config, process) = Build(temp);
        GiveRealBuildFolder(temp);
        service.Initialize(config);
        process.Running = true;

        var ex = Assert.Throws<InvalidOperationException>(() => service.Switch(config, "OnlineFix"));

        Assert.Contains("Close the game", ex.Message);
        Assert.Equal("Vanilla", service.GetStatus(config).ActiveVariant);
    }

    [Fact]
    public void Initialize_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, config, process) = Build(temp);
        GiveRealBuildFolder(temp);
        process.Running = true;

        Assert.Throws<InvalidOperationException>(() => service.Initialize(config));
        Assert.True(File.Exists(temp.Combine("game", "Repentogon", "isaac-ng.exe")));
    }

    [Fact]
    public void Switch_RefusesWhenTheLinkPathIsARealFolder()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        temp.Dir("game", "~", "OnlineFix");

        Assert.Throws<UnsafePathException>(() => service.Switch(config, "OnlineFix"));

        Assert.True(File.Exists(temp.Combine("game", "Repentogon", "isaac-ng.exe")));
    }

    [Fact]
    public void Switch_RefusesAVariantThatDoesNotExist()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        service.Initialize(config);

        Assert.Throws<UnsafePathException>(() => service.Switch(config, "NotAThing"));
        Assert.Equal("Vanilla", service.GetStatus(config).ActiveVariant);
    }

    [Fact]
    public void GetStatus_DescribesAnUninitialisedInstall()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);

        var status = service.GetStatus(config);

        Assert.Equal(BuildLinkState.RealFolder, status.State);
        Assert.False(status.IsReady);
        Assert.Empty(status.Variants);
    }

    [Fact]
    public void GetStatus_ReportsALinkAimedOutsideTheBuildRoot()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        var elsewhere = temp.Dir("somewhere-else");
        temp.Dir("game", "~", "Vanilla");
        new JunctionService().Create(temp.Combine("game", "Repentogon"), elsewhere);

        var status = service.GetStatus(config);

        Assert.Equal(BuildLinkState.LinkedElsewhere, status.State);
        Assert.Null(status.ActiveVariant);
    }

    [Fact]
    public void GetStatus_IgnoresLinksInsideTheBuildRootWhenListingVariants()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        GiveRealBuildFolder(temp);
        service.Initialize(config);
        // A stray junction in the build root is not a build to switch to.
        new JunctionService().Create(temp.Combine("game", "~", "Shortcut"), temp.Combine("game", "~", "Vanilla"));

        Assert.Equal(new[] { "OnlineFix", "Vanilla" }, service.GetStatus(config).Variants);
    }
}
