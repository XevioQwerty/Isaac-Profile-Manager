using System.Text;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Storage;

namespace IsaacProfileManager.Core.Services;

public enum MigrationMode
{
    /// <summary>Leave the existing mods\ folder alone; the new profile starts empty.</summary>
    None,

    /// <summary>Copy the mods currently installed into the first profile.</summary>
    CopyIntoProfile,
}

/// <summary>What first-run setup has been asked to do, before any of it happens.</summary>
public sealed record SetupPlan(
    string IsaacExe,
    string SyncRoot,
    string FirstProfile,
    string? LauncherExe = null,
    bool PerProfileBuild = false,
    bool OwnsOnSteam = true,
    MigrationMode Migration = MigrationMode.CopyIntoProfile)
{
    public string GameDir => Path.GetDirectoryName(Path.GetFullPath(IsaacExe)) ?? string.Empty;
    public string ModsDir => Path.Combine(GameDir, "mods");
    public string ProfileDir => Path.Combine(SyncRoot, FirstProfile);
}

public sealed record SetupResult(
    AppConfig Config,
    string ConfigPath,
    string? ModsBackupPath,
    int ModsCopied,
    int MarkersCleared,
    IReadOnlyList<string> Notes);

/// <summary>
/// First-run setup, done in the app rather than only in IsaacProfiles.ps1.
///
/// Writes the same <c>ConfigVersion 3</c> file the script reads, so the two stay
/// interchangeable. Everything destructive is a rename: an existing real
/// <c>mods\</c> folder becomes <c>mods.backup-&lt;timestamp&gt;</c> and is never
/// deleted, because it is the user's entire mod collection.
/// </summary>
public sealed class SetupService
{
    private readonly IJunctionService _junctions;

    public SetupService(IJunctionService junctions) => _junctions = junctions;

    /// <summary>Problems that would stop setup. Empty means it is safe to run.</summary>
    public static IReadOnlyList<string> Validate(SetupPlan plan)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.IsaacExe) || !File.Exists(plan.IsaacExe))
            problems.Add("Choose the game's isaac-ng.exe.");
        else if (!string.Equals(Path.GetFileName(plan.IsaacExe), "isaac-ng.exe", StringComparison.OrdinalIgnoreCase))
            problems.Add("That is not isaac-ng.exe.");

        if (string.IsNullOrWhiteSpace(plan.SyncRoot))
            problems.Add("Choose a folder to keep your mod profiles in.");
        else if (!string.IsNullOrWhiteSpace(plan.GameDir) &&
                 Path.GetFullPath(plan.SyncRoot).TrimEnd('\\')
                     .StartsWith(Path.GetFullPath(plan.GameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            // It would sit under the very folder we replace with a junction.
            problems.Add("The profiles folder must not be inside the game directory.");
        }

        if (!ModProfileService.IsValidProfileName(plan.FirstProfile))
            problems.Add("Give the first profile a name usable as a folder.");

        if (!string.IsNullOrWhiteSpace(plan.LauncherExe) &&
            GameDetectionService.ResolveLauncherPath(plan.LauncherExe) is null)
            problems.Add("The launcher path is not REPENTOGONLauncher.exe.");

        return problems;
    }

    public SetupResult Run(SetupPlan plan, string configPath, IProgress<string>? progress = null)
    {
        var problems = Validate(plan);
        if (problems.Count > 0) throw new UnsafePathException(string.Join("\n", problems));

        var notes = new List<string>();

        // 1. The profiles folder, with sync metadata kept above the profiles so
        //    Isaac never enumerates it as a mod.
        Directory.CreateDirectory(plan.ProfileDir);
        notes.AddRange(WriteSyncMetadata(plan.SyncRoot));

        // 2. Copy the mods that are installed now, before anything moves.
        var copied = 0;
        if (plan.Migration == MigrationMode.CopyIntoProfile &&
            Directory.Exists(plan.ModsDir) && !_junctions.IsJunction(plan.ModsDir))
        {
            foreach (var mod in new DirectoryInfo(plan.ModsDir).GetDirectories())
            {
                if ((mod.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                progress?.Report($"Copying {mod.Name}");
                DirectoryCopier.Copy(mod.FullName, Path.Combine(plan.ProfileDir, mod.Name));
                copied++;
            }
            if (copied > 0) notes.Add($"Copied {copied} mod(s) into '{plan.FirstProfile}'.");
        }

        // 3. Migrated folders usually carry disable.it from whenever the mod was
        //    last switched off, which makes a mod present but silently inert.
        var cleared = ModProfileService.ClearDisableMarkers(plan.ProfileDir);
        if (cleared > 0) notes.Add($"Cleared {cleared} stale disable.it marker(s) — those mods are enabled again.");

        // 4. Free up mods\. A junction is removed; a real folder is renamed, never deleted.
        string? backup = null;
        if (Directory.Exists(plan.ModsDir))
        {
            if (_junctions.IsJunction(plan.ModsDir))
            {
                _junctions.RemoveLink(plan.ModsDir);
                notes.Add("Removed the existing mods junction; its target was untouched.");
            }
            else
            {
                backup = Path.Combine(plan.GameDir, $"mods.backup-{DateTime.Now:yyyyMMdd-HHmmss}");
                for (var n = 2; Directory.Exists(backup); n++)
                    backup = Path.Combine(plan.GameDir, $"mods.backup-{DateTime.Now:yyyyMMdd-HHmmss}-{n}");

                progress?.Report("Preserving your existing mods folder");
                Directory.Move(plan.ModsDir, backup);
                notes.Add($"Your original mods folder was renamed to '{Path.GetFileName(backup)}'. " +
                          "Delete it yourself once everything works.");
            }
        }

        // 5. Point the game at the profile.
        progress?.Report("Linking mods to the profile");
        _junctions.Create(plan.ModsDir, plan.ProfileDir);

        var config = new AppConfig
        {
            ConfigVersion = AppConfig.SupportedConfigVersion,
            IsaacExe = plan.IsaacExe,
            GameDir = plan.GameDir,
            ModsDir = plan.ModsDir,
            SyncRoot = plan.SyncRoot,
            Profiles = new List<string> { plan.FirstProfile },
            ActiveProfile = plan.FirstProfile,
            PerProfileBuild = plan.PerProfileBuild,
            LauncherExe = string.IsNullOrWhiteSpace(plan.LauncherExe) ? null : plan.LauncherExe,
            OwnsOnSteam = plan.OwnsOnSteam,
            LaunchMethod = plan.OwnsOnSteam ? nameof(GameLaunchMethod.Steam) : nameof(GameLaunchMethod.File),
            SetupDate = DateTimeOffset.Now.ToString("o"),
        };

        new ConfigStore(configPath).Save(config);
        return new SetupResult(config, configPath, backup, copied, cleared, notes);
    }

    /// <summary>
    /// The ignore files that stop Syncthing and git fighting over the same
    /// folder. Existing files are left alone.
    /// </summary>
    private static IReadOnlyList<string> WriteSyncMetadata(string syncRoot)
    {
        var written = new List<string>();

        Write(".stignore", new[]
        {
            "// Never let Syncthing replicate a live git directory.",
            "/.git", "/.gitignore", "/.gitattributes", "/.stversions",
            "// Machine-local: backups, and profile folders built from links.",
            "/.backup",
            "(?d)desktop.ini", "(?d)Thumbs.db",
        });

        Write(".gitignore", new[]
        {
            "# Syncthing metadata - machine-local, never commit",
            ".stfolder/", ".stversions/", ".stignore", ".syncthing.*.tmp",
            "",
            "# Backups this tool takes before anything destructive",
            ".backup/",
        });

        Write(".gitattributes", new[]
        {
            "# Byte-for-byte identical checkouts. Without this git rewrites line",
            "# endings in .lua/.xml and a clone differs from a Syncthing copy.",
            "* -text",
        });

        return written;

        void Write(string name, string[] lines)
        {
            var path = Path.Combine(syncRoot, name);
            if (File.Exists(path)) return;
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            written.Add($"Wrote {name}.");
        }
    }
}
