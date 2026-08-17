using System.Diagnostics;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

public enum GameLaunchMethod
{
    /// <summary>Hand off to Steam via <c>steam://rungameid/</c>, honouring its launch options.</summary>
    Steam,

    /// <summary>Run an executable directly — normally REPENTOGONLauncher.exe.</summary>
    File,
}

/// <summary>What a launch would actually do, resolved before anything is started.</summary>
public sealed record LaunchPlan(string Target, string Arguments, string Summary);

public interface IGameLauncherService
{
    GameLaunchMethod ResolveMethod(AppConfig config);
    LaunchPlan Resolve(AppConfig config);
    void Launch(AppConfig config);
}

/// <summary>
/// Starts the game the way the user normally does.
///
/// Deliberately thin: this tool's job is to arrange folders, and the launch path
/// belongs to Steam or to REPENTOGONLauncher.exe. It exists so switching a
/// profile and starting the game are not two different windows.
/// </summary>
public sealed class GameLauncherService : IGameLauncherService
{
    public const string SteamAppId = "250900";
    public const string SteamUrl = $"steam://rungameid/{SteamAppId}";
    private const string LauncherExeName = "REPENTOGONLauncher.exe";

    /// <summary>Marker file REPENTOGON leaves in its build folder.</summary>
    private const string RepentogonMarker = ".repentogon";

    public GameLaunchMethod ResolveMethod(AppConfig config)
    {
        if (string.Equals(config.LaunchMethod, nameof(GameLaunchMethod.File), StringComparison.OrdinalIgnoreCase))
            return GameLaunchMethod.File;
        if (string.Equals(config.LaunchMethod, nameof(GameLaunchMethod.Steam), StringComparison.OrdinalIgnoreCase))
            return GameLaunchMethod.Steam;

        // Unset: follow how setup recorded the install.
        return config.OwnsOnSteam ? GameLaunchMethod.Steam : GameLaunchMethod.File;
    }

    /// <summary>The executable the File method would run, falling back to the configured launcher.</summary>
    public static string? ResolveTarget(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.LaunchTarget) ? config.LaunchTarget
        : !string.IsNullOrWhiteSpace(config.LauncherExe) ? config.LauncherExe
        : config.IsaacExe;

    public LaunchPlan Resolve(AppConfig config)
    {
        if (ResolveMethod(config) == GameLaunchMethod.Steam)
        {
            return new LaunchPlan(
                SteamUrl,
                string.Empty,
                "Steam starts the game, applying whatever launch options you set there.");
        }

        var target = ResolveTarget(config);
        if (string.IsNullOrWhiteSpace(target))
            throw new UnsafePathException("No program chosen to launch. Pick one in Settings.");
        if (!System.IO.File.Exists(target))
            throw new UnsafePathException($"The program to launch does not exist:\n{target}");

        var name = Path.GetFileName(target);
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;

        // The downgraded build refuses to start on its own — it shows "This exe
        // should only be launched using the REPENTOGONLauncher" and exits. Catch
        // that here rather than letting the user meet the dialog.
        if (string.Equals(name, "isaac-ng.exe", StringComparison.OrdinalIgnoreCase) &&
            System.IO.File.Exists(Path.Combine(directory, RepentogonMarker)))
        {
            throw new UnsafePathException(
                $"{target}\n\nThat is the REPENTOGON build, which refuses to be started directly. " +
                "Point this at REPENTOGONLauncher.exe instead.");
        }

        // The launcher takes the *vanilla* exe path and resolves the Repentogon
        // build itself.
        if (string.Equals(name, LauncherExeName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config.IsaacExe))
        {
            return new LaunchPlan(
                target,
                $"--isaac=\"{config.IsaacExe}\"",
                "REPENTOGONLauncher starts the build selected by [Shared] LaunchMode.");
        }

        return new LaunchPlan(target, string.Empty, $"Runs {name} directly.");
    }

    public void Launch(AppConfig config)
    {
        var plan = Resolve(config);

        var info = new ProcessStartInfo(plan.Target) { UseShellExecute = true };
        if (plan.Arguments.Length > 0) info.Arguments = plan.Arguments;

        // steam:// is a protocol handler, not a file, so it has no working directory.
        if (!plan.Target.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            info.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(plan.Target))!;

        Process.Start(info);
    }
}
