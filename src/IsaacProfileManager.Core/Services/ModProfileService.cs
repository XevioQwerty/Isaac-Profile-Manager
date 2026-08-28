using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Storage;

namespace IsaacProfileManager.Core.Services;

public sealed record ActivationResult(
    string ProfileName,
    int ModCount,
    int ClearedMarkers,
    LaunchMode? BuildSelected,
    MaterialiseReport? Materialised = null);

/// <summary>A manifest on disk that the config does not yet know about — typically one synced from someone else.</summary>
public sealed record DiscoveredProfile(string Name, int ModCount, int MissingFromLibrary, string Notes);

/// <summary>What deleting a profile actually did.</summary>
public sealed record ProfileRemoval(
    string Name,
    int LinksRemoved,
    IReadOnlyList<string> MovedToBackup,
    string? BackupPath,
    bool FolderDeleted,
    bool ManifestDeleted)
{
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"Removed '{Name}'" };
            if (LinksRemoved > 0) parts.Add($"{LinksRemoved} link(s) unlinked");
            if (FolderDeleted) parts.Add("folder deleted");
            if (ManifestDeleted) parts.Add("manifest deleted");
            if (MovedToBackup.Count > 0) parts.Add($"{MovedToBackup.Count} real folder(s) moved to .backup");
            return string.Join(", ", parts) + ".";
        }
    }
}

public interface IModProfileService
{
    IReadOnlyList<ModProfile> List(AppConfig config);
    ActivationResult Activate(AppConfig config, string name);
    void Add(AppConfig config, string name, string? seedFromProfile = null);
    ProfileRemoval Remove(AppConfig config, string name, bool deleteFolder = true);
    string? GetActiveProfileFromDisk(AppConfig config);
}

public sealed class ModProfileService : IModProfileService
{
    private const string DisableMarker = "disable.it";

    private readonly IJunctionService _junctions;
    private readonly ILauncherIniService _launcherIni;
    private readonly IConfigStore _store;

    public ModProfileService(IJunctionService junctions, ILauncherIniService launcherIni, IConfigStore store)
    {
        _junctions = junctions;
        _launcherIni = launcherIni;
        _store = store;
    }

    public static bool IsValidProfileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public IReadOnlyList<ModProfile> List(AppConfig config)
    {
        var active = GetActiveProfileFromDisk(config) ?? config.ActiveProfile;
        var result = new List<ModProfile>(config.Profiles.Count);

        foreach (var name in config.Profiles)
        {
            var path = ProfilePath(config, name);
            var exists = Directory.Exists(path);
            var mods = exists ? Directory.GetDirectories(path).Length : 0;
            var disabled = exists ? CountDisabled(path) : 0;

            result.Add(new ModProfile
            {
                Name = name,
                Path = path,
                FolderExists = exists,
                ModCount = mods,
                DisabledCount = disabled,
                IsActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                UseRepentogon = config.UseRepentogon.Contains(name, StringComparer.OrdinalIgnoreCase),
                Notes = config.ProfileNotes.TryGetValue(name, out var note) ? note : string.Empty,
                LastModified = exists ? Directory.GetLastWriteTime(path) : null,
            });
        }

        return result;
    }

    /// <summary>
    /// Which profile the mods junction actually points at. The config records
    /// what we last set, but the junction is the truth — the PowerShell script
    /// or the user may have moved it since.
    /// </summary>
    public string? GetActiveProfileFromDisk(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ModsDir)) return null;
        var target = _junctions.GetTarget(config.ModsDir);
        if (target is null) return null;

        foreach (var name in config.Profiles)
        {
            if (string.Equals(Path.GetFullPath(ProfilePath(config, name)).TrimEnd('\\'),
                              Path.GetFullPath(target).TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    public ActivationResult Activate(AppConfig config, string name)
    {
        if (!config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown profile '{name}'.", nameof(name));
        if (string.IsNullOrWhiteSpace(config.ModsDir))
            throw new InvalidOperationException("Config has no ModsDir. Re-run setup.");

        var target = ProfilePath(config, name);

        // Rebuild from the manifest first: it is the source of truth, and it may
        // have changed since this folder was last built — most obviously when it
        // arrived from someone else via sync.
        var materialised = MaterialiseFromManifest(config, name);

        if (!Directory.Exists(target))
            throw new UnsafePathException($"Profile folder is missing: {target}");

        // Refuses if mods\ is a real folder. Never delete what the user owns.
        _junctions.RemoveLink(config.ModsDir);

        var cleared = ClearDisableMarkers(target);
        _junctions.Create(config.ModsDir, target);

        LaunchMode? selected = null;
        if (config.PerProfileBuild)
        {
            var mode = config.UseRepentogon.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? LaunchMode.Repentogon
                : LaunchMode.Vanilla;
            if (_launcherIni.TrySetLaunchMode(mode)) selected = mode;
        }

        config.ActiveProfile = name;
        _store.Save(config);

        return new ActivationResult(name, Directory.GetDirectories(target).Length, cleared, selected, materialised);
    }

    private ModLibraryService? LibraryFor(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.SyncRoot) ? null : new ModLibraryService(_junctions, config.SyncRoot!);

    /// <summary>
    /// Rebuild a profile's folder from its manifest, if it has one. Profiles
    /// created before manifests existed have none, and are left exactly as they are.
    /// </summary>
    public MaterialiseReport? MaterialiseFromManifest(AppConfig config, string name)
    {
        var library = LibraryFor(config);
        if (library is null) return null;
        if (!library.ListManifests().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

        var manifest = library.LoadManifest(name);
        if (manifest.Mods.Count == 0) return null;

        return library.Materialise(name, manifest);
    }

    // --- Profiles that arrived from somewhere else --------------------------

    /// <summary>
    /// Manifests on disk with no entry in the config. A profile synced from
    /// another person lands here, and without this it would be invisible.
    /// </summary>
    public IReadOnlyList<DiscoveredProfile> FindUnregisteredProfiles(AppConfig config)
    {
        var library = LibraryFor(config);
        if (library is null) return Array.Empty<DiscoveredProfile>();

        var known = config.Profiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = library.ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<DiscoveredProfile>();

        foreach (var name in library.ListManifests())
        {
            if (known.Contains(name)) continue;

            ProfileManifest manifest;
            try { manifest = library.LoadManifest(name); }
            catch (ConfigSchemaMismatchException ex) { found.Add(new DiscoveredProfile(name, 0, 0, ex.Message)); continue; }

            var missing = manifest.Mods.Count(m => !entries.Contains(m));
            found.Add(new DiscoveredProfile(
                name,
                manifest.Mods.Count,
                missing,
                missing == 0
                    ? "Every mod it needs is already in your library."
                    : $"{missing} of its mods are not in your library yet."));
        }

        return found;
    }

    /// <summary>Adopt a discovered manifest as a profile and build its folder.</summary>
    public MaterialiseReport? RegisterProfile(AppConfig config, string name)
    {
        var library = LibraryFor(config) ?? throw new InvalidOperationException("Config has no SyncRoot.");
        if (!library.ListManifests().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new UnsafePathException($"No manifest called '{name}'.");

        if (!config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            config.Profiles.Add(name);
            _store.Save(config);
        }

        return MaterialiseFromManifest(config, name);
    }

    /// <summary>
    /// Take a profile someone sent as a file: write it as a manifest, register
    /// it, and build what can be built. Mods missing from the library are
    /// reported rather than silently skipped.
    /// </summary>
    public (string Name, MaterialiseReport? Report, IReadOnlyList<string> Missing) ImportSharedProfile(
        AppConfig config, string exportPath, string? nameOverride = null)
    {
        var library = LibraryFor(config) ?? throw new InvalidOperationException("Config has no SyncRoot.");
        var shared = LibraryHashService.ReadExport(exportPath);

        var name = (nameOverride ?? shared.Name).Trim();
        if (!IsValidProfileName(name))
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(nameOverride));

        library.SaveManifest(name, new ProfileManifest { Mods = shared.Mods.ToList(), Notes = shared.Notes });

        var entries = library.ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = shared.Mods.Where(m => !entries.Contains(m)).ToList();

        return (name, RegisterProfile(config, name), missing);
    }

    public void Add(AppConfig config, string name, string? seedFromProfile = null)
    {
        if (!IsValidProfileName(name))
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(name));
        if (config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Profile '{name}' already exists.", nameof(name));
        if (string.IsNullOrWhiteSpace(config.SyncRoot))
            throw new InvalidOperationException("Config has no SyncRoot. Re-run setup.");

        var dir = ProfilePath(config, name);
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrWhiteSpace(seedFromProfile))
        {
            var source = ProfilePath(config, seedFromProfile);
            if (!Directory.Exists(source))
                throw new UnsafePathException($"Cannot seed from '{seedFromProfile}' — folder not found: {source}");

            foreach (var modDir in new DirectoryInfo(source).GetDirectories())
            {
                if ((modDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                DirectoryCopier.Copy(modDir.FullName, Path.Combine(dir, modDir.Name));
            }
            ClearDisableMarkers(dir);
        }

        config.Profiles.Add(name);
        _store.Save(config);
    }

    /// <summary>
    /// Delete a profile: its folder, its manifest, and its entry in the config.
    ///
    /// A profile folder is mostly junctions into the shared library, so this can
    /// never be a recursive delete — that would follow the links and take the
    /// library with it. Links are removed one at a time with
    /// <c>recursive: false</c>, which unlinks without touching the target.
    ///
    /// A real folder inside a profile is a different matter: a hand-installed mod
    /// has no other copy, so those are moved to <c>.backup</c> rather than
    /// deleted. The profile stops existing either way; nothing irreplaceable does.
    /// </summary>
    public ProfileRemoval Remove(AppConfig config, string name, bool deleteFolder = true)
    {
        if (!config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown profile '{name}'.", nameof(name));
        if (string.Equals(config.ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{name}' is active. Switch to another profile first.");

        var links = 0;
        var moved = new List<string>();
        string? backupPath = null;
        var folderDeleted = false;
        var manifestDeleted = false;

        var directory = ProfilePath(config, name);

        if (deleteFolder && Directory.Exists(directory))
        {
            // A profile folder that is itself a link is not ours to delete; the
            // real data lives wherever it points.
            if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnsafePathException(
                    $"'{directory}' is a link, not a real folder. Refusing to delete it.");

            foreach (var child in Directory.GetDirectories(directory))
            {
                if (_junctions.IsJunction(child))
                {
                    _junctions.RemoveLink(child);   // never recursive: the target is the library
                    links++;
                    continue;
                }

                backupPath ??= Path.Combine(BackupRoot(config), DateTime.Now.ToString("yyyyMMdd-HHmmss"), name);
                Directory.CreateDirectory(backupPath);
                Directory.Move(child, Path.Combine(backupPath, Path.GetFileName(child)));
                moved.Add(Path.GetFileName(child));
            }

            foreach (var file in Directory.GetFiles(directory))
            {
                backupPath ??= Path.Combine(BackupRoot(config), DateTime.Now.ToString("yyyyMMdd-HHmmss"), name);
                Directory.CreateDirectory(backupPath);
                File.Move(file, Path.Combine(backupPath, Path.GetFileName(file)), overwrite: true);
            }

            try
            {
                Directory.Delete(directory, recursive: false);
                folderDeleted = true;
            }
            catch (IOException)
            {
                // Something arrived while we worked, or a handle is open. Leaving
                // the folder is a far better outcome than forcing a delete.
            }
        }

        var manifest = ManifestPath(config, name);
        if (deleteFolder && manifest is not null && File.Exists(manifest))
        {
            File.Delete(manifest);
            manifestDeleted = true;
        }

        config.Profiles.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        config.UseRepentogon.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        config.ProfileNotes.Remove(name);
        _store.Save(config);

        return new ProfileRemoval(name, links, moved, backupPath, folderDeleted, manifestDeleted);
    }

    private static string BackupRoot(AppConfig config) =>
        Path.Combine(config.SyncRoot!, ".backup");

    private static string? ManifestPath(AppConfig config, string name) =>
        string.IsNullOrWhiteSpace(config.SyncRoot)
            ? null
            : Path.Combine(config.SyncRoot, ModLibraryService.ManifestFolderName, name + ".json");

    public void SetUseRepentogon(AppConfig config, string name, bool useRepentogon)
    {
        config.UseRepentogon.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (useRepentogon) config.UseRepentogon.Add(name);
        _store.Save(config);
    }

    public void SetNotes(AppConfig config, string name, string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) config.ProfileNotes.Remove(name);
        else config.ProfileNotes[name] = notes;
        _store.Save(config);
    }

    private static string ProfilePath(AppConfig config, string name) =>
        Path.Combine(config.SyncRoot ?? string.Empty, name);

    private static int CountDisabled(string profileDir)
    {
        var count = 0;
        foreach (var modDir in Directory.GetDirectories(profileDir))
        {
            if (File.Exists(Path.Combine(modDir, DisableMarker))) count++;
        }
        return count;
    }

    /// <summary>
    /// Delete every <c>disable.it</c> in the profile. A disabled mod is present
    /// on disk but inert, so two players who synced identical folders can still
    /// desync — and nothing in the log says why.
    /// </summary>
    public static int ClearDisableMarkers(string profileDir)
    {
        if (!Directory.Exists(profileDir)) return 0;

        var cleared = 0;
        foreach (var marker in Directory.EnumerateFiles(profileDir, DisableMarker, SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(marker, FileAttributes.Normal);
                File.Delete(marker);
                cleared++;
            }
            catch (IOException)
            {
                // Locked by a sync client mid-write; the next activation gets it.
            }
        }
        return cleared;
    }
}
