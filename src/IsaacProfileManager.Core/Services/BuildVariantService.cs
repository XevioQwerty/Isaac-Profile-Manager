using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Storage;

namespace IsaacProfileManager.Core.Services;

public enum BuildLinkState
{
    /// <summary>No <c>Repentogon\</c> in the game directory at all.</summary>
    Absent,

    /// <summary>A real folder sits where the link belongs. Needs migrating before anything can switch.</summary>
    RealFolder,

    /// <summary>A junction pointing at one of the known variants.</summary>
    Linked,

    /// <summary>A junction pointing somewhere outside the build root. Left alone.</summary>
    LinkedElsewhere,
}

public sealed record BuildVariantStatus(
    string LinkPath,
    string BuildRoot,
    BuildLinkState State,
    string? LinkTarget,
    string? ActiveVariant,
    IReadOnlyList<string> Variants)
{
    /// <summary>True when switching is possible right now without any migration step.</summary>
    public bool IsReady => State is BuildLinkState.Linked && Variants.Count > 1;
}

public interface IBuildVariantService
{
    BuildVariantStatus GetStatus(AppConfig config);
    void Initialize(AppConfig config, IProgress<string>? progress = null);
    void Switch(AppConfig config, string variantName);
}

/// <summary>
/// Swaps what the game's <c>Repentogon\</c> folder contains, by re-pointing it
/// at one of several build folders held in a build root (<c>&lt;GameDir&gt;\~</c>
/// by default), one subfolder per variant.
///
/// This is the same indirection the mod profiles use: nothing is copied at
/// switch time and no file in the game directory is modified, so a switch is
/// instant and reversible. Which files a given variant folder contains is
/// entirely the user's business — this service only moves a link.
///
/// The launcher refuses to run <c>Repentogon\isaac-ng.exe</c> directly, so a
/// switch only takes effect through REPENTOGONLauncher.exe.
/// </summary>
public sealed class BuildVariantService : IBuildVariantService
{
    /// <summary>The folder the launcher loads the downgraded build from.</summary>
    public const string LinkFolderName = "Repentogon";

    /// <summary>Default build root, kept inside the game dir so a Steam verify does not orphan it.</summary>
    public const string DefaultBuildRootName = "~";

    public const string BaselineVariant = "Vanilla";
    public const string AlternateVariant = "OnlineFix";

    private readonly IJunctionService _junctions;
    private readonly IGameProcessService _process;
    private readonly IConfigStore _store;

    public BuildVariantService(IJunctionService junctions, IGameProcessService process, IConfigStore store)
    {
        _junctions = junctions;
        _process = process;
        _store = store;
    }

    public static string ResolveBuildRoot(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.BuildRoot)
            ? config.BuildRoot!
            : Path.Combine(config.GameDir ?? string.Empty, DefaultBuildRootName);

    /// <summary>
    /// The folder the build link lives at. Normally <c>&lt;GameDir&gt;\Repentogon</c>,
    /// but the folder name is configurable: an absolute value in config wins
    /// outright, a bare name is taken relative to the game directory.
    /// </summary>
    public static string ResolveLinkPath(AppConfig config)
    {
        var configured = config.BuildLinkFolder;
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(config.GameDir ?? string.Empty, LinkFolderName);

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(config.GameDir ?? string.Empty, configured);
    }

    public BuildVariantStatus GetStatus(AppConfig config)
    {
        var linkPath = ResolveLinkPath(config);
        var buildRoot = ResolveBuildRoot(config);

        var variants = Directory.Exists(buildRoot)
            ? new DirectoryInfo(buildRoot).GetDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        if (!Directory.Exists(linkPath))
            return new BuildVariantStatus(linkPath, buildRoot, BuildLinkState.Absent, null, null, variants);

        if (!_junctions.IsJunction(linkPath))
            return new BuildVariantStatus(linkPath, buildRoot, BuildLinkState.RealFolder, null, null, variants);

        var target = _junctions.GetTarget(linkPath);
        var active = variants.FirstOrDefault(v => SamePath(Path.Combine(buildRoot, v), target));

        return active is null
            ? new BuildVariantStatus(linkPath, buildRoot, BuildLinkState.LinkedElsewhere, target, null, variants)
            : new BuildVariantStatus(linkPath, buildRoot, BuildLinkState.Linked, target, active, variants);
    }

    /// <summary>
    /// Prepare a first-time install: move the existing build folder into the
    /// build root as the baseline variant, then seed a second variant from it,
    /// and leave the game pointed at the baseline.
    ///
    /// Both variants start as identical copies of what was already installed.
    /// Whatever the user chooses to put in the alternate one afterwards is up to
    /// them; this only builds the switching scaffold.
    /// </summary>
    public void Initialize(AppConfig config, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(config.GameDir))
            throw new InvalidOperationException("Config has no GameDir. Re-run setup.");
        RequireGameClosed();

        var linkPath = ResolveLinkPath(config);
        var buildRoot = ResolveBuildRoot(config);
        var baseline = Path.Combine(buildRoot, BaselineVariant);
        var alternate = Path.Combine(buildRoot, AlternateVariant);

        Directory.CreateDirectory(buildRoot);

        // 1. Get the real build out of the game directory and into the build root.
        if (Directory.Exists(linkPath) && !_junctions.IsJunction(linkPath))
        {
            if (Directory.Exists(baseline))
                throw new UnsafePathException(
                    $"Both '{linkPath}' (a real folder) and '{baseline}' exist. Refusing to guess which build is current — " +
                    "move one aside yourself, then run this again.");

            progress?.Report($"Moving the installed build into {baseline}");
            // A move, not a copy-then-delete: nothing is ever deleted, and if it
            // fails the original is still exactly where it was.
            Directory.Move(linkPath, baseline);
        }

        if (!Directory.Exists(baseline))
            throw new UnsafePathException(
                $"No build to work from: '{linkPath}' is not a real folder and '{baseline}' does not exist. " +
                "Install REPENTOGON first, then run this again.");

        // 2. Seed the second variant from the baseline.
        if (!Directory.Exists(alternate))
        {
            progress?.Report($"Copying {BaselineVariant} into {AlternateVariant} (this takes a moment)");
            DirectoryCopier.Copy(baseline, alternate, overwrite: false, progress: progress);
        }

        // 3. Point the game at the baseline.
        var status = GetStatus(config);
        if (status.State is BuildLinkState.Absent)
        {
            progress?.Report($"Linking {LinkFolderName} -> {BaselineVariant}");
            _junctions.Create(linkPath, baseline);
        }

        config.BuildRoot = buildRoot;
        config.ActiveBuildVariant = GetStatus(config).ActiveVariant;
        _store.Save(config);
        progress?.Report("Ready");
    }

    /// <summary>Re-point the build link at another variant.</summary>
    public void Switch(AppConfig config, string variantName)
    {
        RequireGameClosed();

        var linkPath = ResolveLinkPath(config);
        var buildRoot = ResolveBuildRoot(config);
        var target = Path.Combine(buildRoot, variantName);

        if (!Directory.Exists(target))
            throw new UnsafePathException($"Build variant folder does not exist: {target}");

        if (Directory.Exists(linkPath) && !_junctions.IsJunction(linkPath))
            throw new UnsafePathException(
                $"'{linkPath}' is a real folder, not a junction. Refusing to touch it — run first-time setup for the build switcher instead.");

        // RemoveLink refuses on anything that is not a junction, and the delete
        // it performs cannot recurse into the variant folder.
        _junctions.RemoveLink(linkPath);
        _junctions.Create(linkPath, target);

        config.ActiveBuildVariant = variantName;
        _store.Save(config);
    }

    private void RequireGameClosed()
    {
        if (_process.IsIsaacRunning())
            throw new InvalidOperationException("Isaac is running. Close the game before changing the build.");
    }

    private static bool SamePath(string a, string? b) =>
        b is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
