using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>The one severity ladder the whole app uses.</summary>
public enum GuardSeverity
{
    /// <summary>Data destruction. The Launch button is disabled.</summary>
    Block,

    /// <summary>Costs a desync, not data. Shown with the fix attached; launching anyway is allowed.</summary>
    Recommend,

    /// <summary>Worth knowing. Nothing is stopped.</summary>
    Warn,
}

/// <summary>What pressing the button beside a finding would do.</summary>
public enum GuardFix
{
    None,

    /// <summary>Activate the mod profile named in <see cref="GuardFinding.FixTarget"/>.</summary>
    SwitchProfile,

    /// <summary>Set the launcher to the build named in <see cref="GuardFinding.FixTarget"/>.</summary>
    SwitchBuild,
}

public sealed record GuardFinding(
    GuardSeverity Severity,
    string Title,
    string Detail,
    GuardFix Fix = GuardFix.None,
    string? FixTarget = null)
{
    public string SeverityText => Severity switch
    {
        GuardSeverity.Block => "BLOCKED",
        GuardSeverity.Recommend => "RECOMMENDED",
        _ => "NOTE",
    };
}

/// <summary>
/// Everything the guard needs, resolved by the caller from disk — never from
/// remembered config. <paramref name="ExpectedGameVersion"/> is the version
/// the build being launched last ran here, not the last run of any build.
/// </summary>
public sealed record LaunchContext(
    LiveIdentity Identity,
    string? ActiveProfile,
    GameBuild LauncherBuild,
    string? ExpectedGameVersion,
    bool IsaacRunning,
    bool AnySets);

public sealed record LaunchVerdict(IReadOnlyList<GuardFinding> Findings)
{
    public bool CanLaunch => Findings.All(f => f.Severity != GuardSeverity.Block);
    public bool IsClean => Findings.Count == 0;

    /// <summary>The most severe finding, for the status bar's one dot.</summary>
    public GuardSeverity? Worst => Findings.Count == 0 ? null : Findings.Min(f => f.Severity);

    public static readonly LaunchVerdict Clean = new(Array.Empty<GuardFinding>());
}

/// <summary>
/// Decides what pressing Launch means for the saves that are live.
///
/// A save set already records the build and the mod profile it was made with,
/// and until now nothing read either at the moment it mattered. The ladder is
/// the one the project has used since the Saves tab: the dangerous half is
/// blocked, the annoying half is recommended with the fix attached.
/// </summary>
public static class LaunchGuardService
{
    public static LaunchVerdict Evaluate(LaunchContext context)
    {
        var findings = new List<GuardFinding>();
        var identity = context.Identity;

        if (context.IsaacRunning)
            findings.Add(new GuardFinding(GuardSeverity.Warn, "Isaac is already running",
                "A second launch does nothing useful, and nothing here can change the session in progress."));

        switch (identity.State)
        {
            case LiveSaveState.NoSaveFolder:
                findings.Add(new GuardFinding(GuardSeverity.Warn, "Save folder not found",
                    "The saves cannot be checked against a set. Point the app at the folder on the Saves screen."));
                return new LaunchVerdict(findings);

            case LiveSaveState.NoSaves:
                findings.Add(new GuardFinding(GuardSeverity.Warn, "No save files",
                    "The game will write a fresh save. Capture it afterwards if you want to keep it."));
                return new LaunchVerdict(findings);

            case LiveSaveState.Unrecognised:
                if (context.AnySets)
                    findings.Add(new GuardFinding(GuardSeverity.Warn, "Live saves match no set",
                        "Nothing can be checked against them. Capture them as a set, or load one."));
                return new LaunchVerdict(findings);
        }

        var set = identity.Set!;

        // Build: vanilla and REPENTOGON saves are different structures. Blocked, never warned.
        if (set.Build == GameBuild.Unknown)
        {
            findings.Add(new GuardFinding(GuardSeverity.Warn, "Save set has no recorded build",
                $"'{set.Name}' does not say which build made it, so a cross-build load cannot be ruled out."));
        }
        else if (set.Build != GameBuild.Both && context.LauncherBuild != GameBuild.Unknown && set.Build != context.LauncherBuild)
        {
            var wanted = set.Build == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
            var launching = context.LauncherBuild == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
            findings.Add(new GuardFinding(GuardSeverity.Block, $"Build mismatch: {wanted} save, {launching} launch",
                $"'{set.Name}' was made on {wanted}, and the launcher is set to start {launching}. " +
                "Loading a save on the wrong build can destroy every achievement.",
                GuardFix.SwitchBuild, wanted));
        }

        // Version: the J-number separates one retail patch from another, which
        // GameBuild cannot. The comparison is against the build being launched
        // — after a REPENTOGON session the log says J273, and that must not
        // block a vanilla launch. A same-build difference is a note, not a
        // block: a retail patch migrates saves, and the cross-build case is
        // already blocked above. Unknown on either side is only a note too.
        var launchingName = context.LauncherBuild == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
        if (set.GameVersion is { Length: > 0 } recorded)
        {
            if (context.ExpectedGameVersion is { Length: > 0 } expected)
            {
                if (!string.Equals(recorded, expected, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new GuardFinding(GuardSeverity.Warn, $"Game version differs: save {recorded}, {launchingName} here last ran {expected}",
                        $"'{set.Name}' was captured on {recorded}. A patch usually migrates a save, but everyone in an online " +
                        "session must be on the same version — check the game has updated on every machine."));
            }
            else if (context.LauncherBuild != GameBuild.Unknown)
            {
                findings.Add(new GuardFinding(GuardSeverity.Warn, $"{Capitalise(launchingName)} has not run here since versions were tracked",
                    $"'{set.Name}' was captured on {recorded}. The first {launchingName} launch records what this machine runs."));
            }
        }
        else if (context.ExpectedGameVersion is { Length: > 0 })
        {
            findings.Add(new GuardFinding(GuardSeverity.Warn, "Save set has no recorded game version",
                $"'{set.Name}' predates version tracking. The next capture records it."));
        }

        // Profile: the pairing the feature was asked for. A mismatch costs a
        // desync, not data, so it is recommended with the fix attached.
        if (set.ModProfile.Length > 0 &&
            !string.Equals(set.ModProfile, context.ActiveProfile, StringComparison.OrdinalIgnoreCase))
        {
            var active = string.IsNullOrEmpty(context.ActiveProfile) ? "no profile" : $"'{context.ActiveProfile}'";
            findings.Add(new GuardFinding(GuardSeverity.Recommend, $"These saves were made with '{set.ModProfile}'",
                $"{Capitalise(active)} is active. Different mods against the same save state is how two players desync.",
                GuardFix.SwitchProfile, set.ModProfile));
        }

        return new LaunchVerdict(findings.OrderBy(f => f.Severity).ToList());
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
