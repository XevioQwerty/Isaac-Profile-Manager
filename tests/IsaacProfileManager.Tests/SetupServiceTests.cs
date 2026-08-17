using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SetupServiceTests
{
    private readonly SetupService _setup = new(new JunctionService());

    /// <summary>A game directory with mods already installed, as a new user has it.</summary>
    private static string GiveGame(TempDir temp, bool withMods = true)
    {
        var exe = temp.File(@"game\isaac-ng.exe", "MZ");
        if (withMods)
        {
            temp.File(@"game\mods\ModA\main.lua", "-- a");
            temp.File(@"game\mods\ModA\disable.it");
            temp.File(@"game\mods\ModB\main.lua", "-- b");
        }
        return exe;
    }

    private static SetupPlan PlanFor(TempDir temp, string exe, MigrationMode migration = MigrationMode.CopyIntoProfile) =>
        new(exe, temp.Combine("sync"), "first-profile", Migration: migration);

    [Fact]
    public void Run_ProducesAWorkingInstallFromNothing()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp);
        var configPath = temp.Combine("isaac-profiles.json");

        var result = _setup.Run(PlanFor(temp, exe), configPath);

        var junctions = new JunctionService();
        Assert.True(junctions.IsJunction(result.Config.ModsDir!));
        Assert.Equal(result.Config.SyncRoot + @"\first-profile",
                     junctions.GetTarget(result.Config.ModsDir!), ignoreCase: true);
        Assert.Equal("first-profile", result.Config.ActiveProfile);
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public void Run_WritesAConfigThePowerShellScriptStillAccepts()
    {
        using var temp = new TempDir();
        var configPath = temp.Combine("isaac-profiles.json");

        _setup.Run(PlanFor(temp, GiveGame(temp)), configPath);

        // Assert-Config in the script refuses anything below ConfigVersion 3.
        var reloaded = new ConfigStore(configPath).Load();
        Assert.Equal(3, reloaded.ConfigVersion);
        Assert.Equal(AppConfig.SupportedConfigVersion, reloaded.ConfigVersion);
        Assert.Contains("first-profile", reloaded.Profiles);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.GameDir));
    }

    [Fact]
    public void Run_RenamesAnExistingModsFolderRatherThanDeletingIt()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp);

        var result = _setup.Run(PlanFor(temp, exe), temp.Combine("isaac-profiles.json"));

        Assert.NotNull(result.ModsBackupPath);
        // The user's entire mod collection must survive setup.
        Assert.True(File.Exists(Path.Combine(result.ModsBackupPath!, "ModA", "main.lua")));
        Assert.True(File.Exists(Path.Combine(result.ModsBackupPath!, "ModB", "main.lua")));
        Assert.StartsWith("mods.backup-", Path.GetFileName(result.ModsBackupPath!));
    }

    [Fact]
    public void Run_CopiesTheInstalledModsIntoTheFirstProfileAndEnablesThem()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp);

        var result = _setup.Run(PlanFor(temp, exe), temp.Combine("isaac-profiles.json"));

        Assert.Equal(2, result.ModsCopied);
        Assert.True(File.Exists(temp.Combine("sync", "first-profile", "ModA", "main.lua")));
        // A migrated mod carrying disable.it would be present but silently inert.
        Assert.Equal(1, result.MarkersCleared);
        Assert.False(File.Exists(temp.Combine("sync", "first-profile", "ModA", "disable.it")));
    }

    [Fact]
    public void Run_CanLeaveTheExistingModsAloneAndStartEmpty()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp);

        var result = _setup.Run(PlanFor(temp, exe, MigrationMode.None), temp.Combine("isaac-profiles.json"));

        Assert.Equal(0, result.ModsCopied);
        Assert.Empty(Directory.GetDirectories(temp.Combine("sync", "first-profile")));
        // Still preserved, just not copied in.
        Assert.NotNull(result.ModsBackupPath);
    }

    [Fact]
    public void Run_ReplacesAnExistingJunctionWithoutTouchingItsTarget()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp, withMods: false);
        var oldTarget = temp.Dir("old-profile");
        temp.File(@"old-profile\KeepMe\main.lua", "-- precious");
        new JunctionService().Create(temp.Combine("game", "mods"), oldTarget);

        var result = _setup.Run(PlanFor(temp, exe), temp.Combine("isaac-profiles.json"));

        Assert.Null(result.ModsBackupPath);
        Assert.True(File.Exists(Path.Combine(oldTarget, "KeepMe", "main.lua")));
        Assert.Equal(temp.Combine("sync", "first-profile"),
                     new JunctionService().GetTarget(result.Config.ModsDir!), ignoreCase: true);
    }

    [Fact]
    public void Run_WritesTheIgnoreFilesThatStopSyncthingAndGitFighting()
    {
        using var temp = new TempDir();

        _setup.Run(PlanFor(temp, GiveGame(temp)), temp.Combine("isaac-profiles.json"));

        var stignore = File.ReadAllText(temp.Combine("sync", ".stignore"));
        Assert.Contains("/.git", stignore);
        Assert.Contains("/.backup", stignore);
        Assert.Contains(".stfolder/", File.ReadAllText(temp.Combine("sync", ".gitignore")));
        Assert.Contains("* -text", File.ReadAllText(temp.Combine("sync", ".gitattributes")));
    }

    [Fact]
    public void Run_DoesNotOverwriteIgnoreFilesTheUserAlreadyHas()
    {
        using var temp = new TempDir();
        temp.File(@"sync\.stignore", "// mine, hands off");

        _setup.Run(PlanFor(temp, GiveGame(temp)), temp.Combine("isaac-profiles.json"));

        Assert.Equal("// mine, hands off", File.ReadAllText(temp.Combine("sync", ".stignore")));
    }

    [Fact]
    public void Run_WorksWhenTheGameHasNoModsFolderAtAll()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp, withMods: false);

        var result = _setup.Run(PlanFor(temp, exe), temp.Combine("isaac-profiles.json"));

        Assert.Null(result.ModsBackupPath);
        Assert.True(new JunctionService().IsJunction(result.Config.ModsDir!));
    }

    // --- Refusals -----------------------------------------------------------

    [Fact]
    public void Validate_RejectsAProfilesFolderInsideTheGameDirectory()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp, withMods: false);
        var plan = new SetupPlan(exe, temp.Combine("game", "profiles"), "p");

        // It would sit under the very folder that becomes a junction.
        Assert.Contains(SetupService.Validate(plan), p => p.Contains("inside the game directory"));
        Assert.Throws<UnsafePathException>(() => _setup.Run(plan, temp.Combine("c.json")));
    }

    [Fact]
    public void Validate_RejectsAMissingOrWrongExecutable()
    {
        using var temp = new TempDir();
        Assert.NotEmpty(SetupService.Validate(new SetupPlan(temp.Combine("nope.exe"), temp.Combine("sync"), "p")));

        var wrong = temp.File(@"game\other.exe", "MZ");
        Assert.Contains(SetupService.Validate(new SetupPlan(wrong, temp.Combine("sync"), "p")),
                        p => p.Contains("not isaac-ng.exe"));
    }

    [Fact]
    public void Validate_RejectsAnUnusableProfileNameAndABadLauncherPath()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp, withMods: false);

        Assert.Contains(SetupService.Validate(new SetupPlan(exe, temp.Combine("sync"), @"bad\name")),
                        p => p.Contains("usable as a folder"));

        var notLauncher = temp.File(@"x\something.exe", "MZ");
        Assert.Contains(SetupService.Validate(new SetupPlan(exe, temp.Combine("sync"), "p", LauncherExe: notLauncher)),
                        p => p.Contains("REPENTOGONLauncher.exe"));
    }

    [Fact]
    public void Validate_AcceptsAGoodPlan()
    {
        using var temp = new TempDir();
        var exe = GiveGame(temp, withMods: false);
        var launcher = temp.File(@"rgon\REPENTOGONLauncher.exe", "MZ");

        Assert.Empty(SetupService.Validate(new SetupPlan(exe, temp.Combine("sync"), "coop", LauncherExe: launcher)));
    }
}
