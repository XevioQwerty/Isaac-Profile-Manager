namespace IsaacProfileManager.Core.Services;

public enum LaunchOptionsState
{
    /// <summary>Starts the REPENTOGON launcher and hands it the game — what per-profile build selection needs.</summary>
    LauncherConfigured,

    /// <summary>Something is set, but not the launcher line.</summary>
    Other,

    /// <summary>Empty. Steam starts the game directly, so the build never follows the profile.</summary>
    Empty,

    /// <summary>Steam's config could not be read.</summary>
    Unknown,
}

public sealed record LaunchOptionsStatus(LaunchOptionsState State, string? Current, string? Suggested)
{
    public bool IsCorrect => State == LaunchOptionsState.LauncherConfigured;
}

/// <summary>
/// Reads Steam's per-game launch options and checks whether they start the
/// REPENTOGON launcher.
///
/// Without that line Steam launches the game directly, `[Shared] LaunchMode` is
/// never consulted, and per-profile build selection silently does nothing — a
/// setup error that presents as "switching the build didn't work". It is also
/// what the REPENTOGON docs require for Steam Remote Play.
/// </summary>
public sealed class SteamLaunchOptionsService
{
    private readonly SteamCloudService _steam;

    public SteamLaunchOptionsService(SteamCloudService? steam = null) => _steam = steam ?? new SteamCloudService();

    /// <summary>The line Steam needs, given where the launcher actually lives.</summary>
    public static string Suggest(string launcherExePath) => $"\"{launcherExePath}\" --isaac=%command%";

    public LaunchOptionsStatus Check(string? launcherExePath, string appId = SteamCloudService.IsaacAppId)
    {
        var suggested = string.IsNullOrWhiteSpace(launcherExePath) ? null : Suggest(launcherExePath!);
        var status = _steam.GetStatus(appId);

        if (status.SteamRoot is null || status.AccountId is null)
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);

        var localConfig = Path.Combine(status.SteamRoot, "userdata", status.AccountId, "config", "localconfig.vdf");
        if (!File.Exists(localConfig))
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);

        string? current;
        try
        {
            var root = VdfParser.ParseFile(localConfig);
            var store = root["UserLocalConfigStore"] ?? root.Children.Values.FirstOrDefault();
            current = store?["Software"]?["Valve"]?["Steam"]?["apps"]?[appId]?["LaunchOptions"]?.Value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);
        }

        if (string.IsNullOrWhiteSpace(current))
            return new LaunchOptionsStatus(LaunchOptionsState.Empty, current, suggested);

        // Both halves matter: the launcher must run, and %command% is how Steam
        // passes it the game's own executable.
        var looksRight = current.Contains("REPENTOGONLauncher", StringComparison.OrdinalIgnoreCase)
                      && current.Contains("%command%", StringComparison.OrdinalIgnoreCase);

        return new LaunchOptionsStatus(
            looksRight ? LaunchOptionsState.LauncherConfigured : LaunchOptionsState.Other,
            current,
            suggested);
    }
}
