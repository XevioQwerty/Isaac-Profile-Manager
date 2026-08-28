using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// A profile's contents, as a portable list of library folder names. Contains no
/// machine-local paths, so it can be synced to another person and materialised
/// against their copy of the library.
/// </summary>
public sealed class ProfileManifest
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("Mods")]
    public List<string> Mods { get; set; } = new();

    [JsonPropertyName("Notes")]
    public string Notes { get; set; } = string.Empty;
}

/// <summary>Outcome of reconciling a profile folder against its manifest.</summary>
public sealed record MaterialiseReport(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Repointed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> MissingFromLibrary,
    IReadOnlyList<string> LeftAlone)
{
    public bool IsClean => MissingFromLibrary.Count == 0 && LeftAlone.Count == 0;
    public int ChangeCount => Created.Count + Repointed.Count + Removed.Count;
}

/// <summary>
/// Owns the shared mod library and the profiles built from it.
///
/// The library holds one real copy of each mod under a suffix-free name, so
/// Steam has no claim on it. A profile is a folder of junctions into the
/// library, rebuilt from a manifest — verified 2026-08-16 that Isaac loads mods
/// through per-mod junctions, including the two hops from <c>mods\</c> through
/// the profile folder to the library.
///
/// Only the library and the manifests are meant to sync. The materialised
/// profile folders are machine-local: a sync client cannot represent a junction,
/// and following one would replicate the entire library again.
/// </summary>
public sealed class ModLibraryService
{
    public const string LibraryFolderName = ".library";
    public const string ManifestFolderName = ".profiles";
    public const string MetadataFolderName = ".meta";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IJunctionService _junctions;
    private readonly string _syncRoot;

    public ModLibraryService(IJunctionService junctions, string syncRoot)
    {
        _junctions = junctions;
        _syncRoot = syncRoot;
    }

    public string LibraryRoot => Path.Combine(_syncRoot, LibraryFolderName);
    public string ManifestRoot => Path.Combine(_syncRoot, ManifestFolderName);
    public string MetadataRoot => Path.Combine(LibraryRoot, MetadataFolderName);

    // --- Library -----------------------------------------------------------

    /// <summary>Library entries, i.e. real mod folders. Dot-prefixed folders are bookkeeping, not mods.</summary>
    public IReadOnlyList<string> ListEntries()
    {
        if (!Directory.Exists(LibraryRoot)) return Array.Empty<string>();

        return new DirectoryInfo(LibraryRoot).GetDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Copy an item's content into the library under a suffix-free name.
    /// Returns the library entry name.
    /// </summary>
    public string Import(WorkshopItem item, bool overwrite = false, IProgress<string>? progress = null)
    {
        if (!item.ContentPresent)
            throw new UnsafePathException(
                $"'{item.Name}' has no downloaded content at {item.ContentPath}. Let Steam finish downloading it first.");

        Directory.CreateDirectory(LibraryRoot);
        var entry = ResolveEntryName(item);
        var destination = Path.Combine(LibraryRoot, entry);

        if (Directory.Exists(destination) && !overwrite)
        {
            progress?.Report($"Already in library: {entry}");
            return entry;
        }

        progress?.Report($"Importing {item.Name}");
        DirectoryCopier.Copy(item.ContentPath, destination, overwrite: true, progress: progress);
        SaveMetadata(item);
        return entry;
    }

    /// <summary>
    /// Replace a library entry's contents with a freshly downloaded revision.
    ///
    /// A copy is not enough: <see cref="DirectoryCopier"/> merges, so files the
    /// author deleted upstream would survive and our bytes would no longer match
    /// a partner's fresh install — which is the exact desync this library exists
    /// to prevent. The old copy is moved to a timestamped backup, never deleted,
    /// so a bad update can be walked back.
    ///
    /// Returns the backup path, or null when the entry was new.
    /// </summary>
    public string? UpdateFromContent(string entry, string sourceDir, long timeUpdated, long fileSize,
                                     IProgress<string>? progress = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new UnsafePathException($"No downloaded content at {sourceDir}.");

        var destination = Path.Combine(LibraryRoot, entry);
        string? backup = null;

        if (Directory.Exists(destination))
        {
            if (_junctions.IsJunction(destination))
                throw new UnsafePathException(
                    $"Library entry '{entry}' is a link, not a real folder. Refusing to replace it.");

            backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), LibraryFolderName, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            progress?.Report($"Backing up the old {entry}");
            Directory.Move(destination, backup);
        }

        progress?.Report($"Installing the new {entry}");
        Directory.CreateDirectory(LibraryRoot);
        DirectoryCopier.Copy(sourceDir, destination, overwrite: true, progress: progress);
        RecordUpstreamStamp(entry, timeUpdated, fileSize);
        return backup;
    }

    /// <summary>
    /// Put freshly downloaded content into the library under a name chosen by
    /// the sender, not derived locally.
    ///
    /// Deriving the name from metadata.xml the way an import does would be wrong
    /// here: the manifest in the share refers to the sender's entry names, and a
    /// name that drifts leaves the profile pointing at nothing. Collision
    /// suffixes are the common case where the two differ.
    /// </summary>
    public void InstallFromShare(string entry, string sourceDir, string workshopId,
                                 long timeUpdated, long fileSize, IProgress<string>? progress = null)
    {
        UpdateFromContent(entry, sourceDir, timeUpdated, fileSize, progress);

        // Read the mod's own metadata for the display name, so a shared library
        // looks the same as a directly imported one.
        string name = entry, description = string.Empty;
        var metadataPath = Path.Combine(sourceDir, "metadata.xml");
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadata = System.Xml.Linq.XDocument.Load(metadataPath).Root;
                name = metadata?.Element("name")?.Value.Trim() is { Length: > 0 } n ? n : entry;
                description = metadata?.Element("description")?.Value.Trim() ?? string.Empty;
            }
            catch (System.Xml.XmlException)
            {
                // Hand-edited metadata is common; the entry name is a fine fallback.
            }
        }

        var payload = ReadMetadata(entry) ?? new CachedMetadata();
        payload.Id = workshopId;
        payload.Name = name;
        payload.Description = description;
        if (payload.ImportedUtc.Length == 0) payload.ImportedUtc = DateTime.UtcNow.ToString("o");
        WriteMetadata(entry, payload);

        // Cache the preview now — after the unsubscribe the content store is gone.
        var preview = FindPreview(sourceDir);
        if (preview is not null)
        {
            Directory.CreateDirectory(MetadataRoot);
            try { File.Copy(preview, Path.Combine(MetadataRoot, entry + Path.GetExtension(preview)), overwrite: true); }
            catch (IOException) { }
        }
    }

    private static string? FindPreview(string contentPath)
    {
        foreach (var candidate in new[] { "thumb.png", "thumbnail.png", "icon.png", "preview.png", "cover.png" })
        {
            var path = Path.Combine(contentPath, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The library entry an already-imported workshop id lives in, if any.</summary>
    public string? FindEntryByWorkshopId(string workshopId) =>
        ListEntries().FirstOrDefault(e => GetCachedId(e) == workshopId);

    /// <summary>
    /// The library name for an item: its own folder name, without the workshop
    /// suffix Isaac would append. Collisions get the id back so two different
    /// mods can never silently become one.
    /// </summary>
    public string ResolveEntryName(WorkshopItem item)
    {
        var name = Sanitise(item.Directory);
        if (name.Length == 0) name = item.Id;

        var candidate = Path.Combine(LibraryRoot, name);
        if (!Directory.Exists(candidate)) return name;

        // Same mod re-imported: the recorded id matches, so reuse the folder.
        var recorded = ReadMetadata(name)?.Id;
        if (recorded is null || recorded == item.Id) return name;

        return $"{name}_{item.Id}";
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            builder.Append(invalid.Contains(c) ? '-' : c);
        return builder.ToString().Trim().TrimEnd('.');
    }

    // --- Cached item metadata ----------------------------------------------
    // Kept beside the library rather than inside the mod folders: anything added
    // inside a mod changes its bytes, and co-op requires those to match exactly.

    private sealed class CachedMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ImportedUtc { get; set; } = string.Empty;

        /// <summary>
        /// The Workshop's <c>time_updated</c> for the content we hold, as unix
        /// seconds. Zero for entries imported before this was recorded — those
        /// fall back to <see cref="ImportedUtc"/>, which is a weaker baseline
        /// because Steam may have downloaded the content some time before the
        /// import happened.
        /// </summary>
        public long TimeUpdated { get; set; }

        /// <summary>The Workshop's reported size for that revision, in bytes.</summary>
        public long FileSize { get; set; }
    }

    private void SaveMetadata(WorkshopItem item)
    {
        Directory.CreateDirectory(MetadataRoot);
        var entry = ResolveEntryName(item);

        // Carry forward the upstream stamp: an import knows the bytes it copied
        // but not which Workshop revision they are. Overwriting it with zero
        // would make a freshly updated mod look like it had never been checked.
        var existing = ReadMetadata(entry);
        var payload = new CachedMetadata
        {
            Id = item.Id,
            Name = item.Name,
            Description = item.Description,
            ImportedUtc = DateTime.UtcNow.ToString("o"),
            TimeUpdated = existing?.Id == item.Id ? existing.TimeUpdated : 0,
            FileSize = existing?.Id == item.Id ? existing.FileSize : 0,
        };
        WriteMetadata(entry, payload);

        // Cache the preview now: after unsubscribing, the content store is gone.
        if (item.LocalImagePath is not null && File.Exists(item.LocalImagePath))
        {
            var target = Path.Combine(MetadataRoot, entry + Path.GetExtension(item.LocalImagePath));
            try { File.Copy(item.LocalImagePath, target, overwrite: true); }
            catch (IOException) { }
        }
    }

    private void WriteMetadata(string entry, CachedMetadata payload)
    {
        Directory.CreateDirectory(MetadataRoot);
        File.WriteAllText(Path.Combine(MetadataRoot, entry + ".json"),
                          JsonSerializer.Serialize(payload, SerializerOptions), new UTF8Encoding(false));
    }

    /// <summary>
    /// Record which Workshop revision an entry's content corresponds to. Called
    /// after pulling an update, so the next check compares against the revision
    /// we actually hold rather than the date we happened to import it.
    /// </summary>
    public void RecordUpstreamStamp(string entry, long timeUpdated, long fileSize)
    {
        var payload = ReadMetadata(entry) ?? new CachedMetadata { Name = entry };
        payload.TimeUpdated = timeUpdated;
        payload.FileSize = fileSize;
        WriteMetadata(entry, payload);
    }

    private CachedMetadata? ReadMetadata(string entry)
    {
        var path = Path.Combine(MetadataRoot, entry + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CachedMetadata>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public string? GetCachedName(string entry) => ReadMetadata(entry)?.Name;
    public string? GetCachedDescription(string entry) => ReadMetadata(entry)?.Description;

    public string? GetCachedImage(string entry) => WorkshopPreviewService.FindCached(MetadataRoot, entry);

    /// <summary>The workshop id an entry was imported from, for re-fetching its preview later.</summary>
    public string? GetCachedId(string entry) => ReadMetadata(entry)?.Id;

    // --- Manifests ---------------------------------------------------------

    public IReadOnlyList<string> ListManifests()
    {
        if (!Directory.Exists(ManifestRoot)) return Array.Empty<string>();
        return Directory.GetFiles(ManifestRoot, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProfileManifest LoadManifest(string profileName)
    {
        var path = ManifestPath(profileName);
        if (!File.Exists(path)) return new ProfileManifest();

        try
        {
            var manifest = JsonSerializer.Deserialize<ProfileManifest>(File.ReadAllText(path));
            if (manifest is null) return new ProfileManifest();

            if (manifest.SchemaVersion != ProfileManifest.CurrentSchemaVersion)
                throw new ConfigSchemaMismatchException(
                    $"{path} has SchemaVersion {manifest.SchemaVersion}; this build understands {ProfileManifest.CurrentSchemaVersion}.");

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new ConfigSchemaMismatchException($"{path} is not readable as JSON: {ex.Message}");
        }
    }

    public void SaveManifest(string profileName, ProfileManifest manifest)
    {
        Directory.CreateDirectory(ManifestRoot);
        File.WriteAllText(ManifestPath(profileName),
                          JsonSerializer.Serialize(manifest, SerializerOptions), new UTF8Encoding(false));
    }

    private string ManifestPath(string profileName) => Path.Combine(ManifestRoot, profileName + ".json");

    // --- Materialisation ---------------------------------------------------

    /// <summary>
    /// Rebuild a profile folder so it holds exactly one junction per manifest
    /// entry. Real folders are never deleted — if one occupies a name we want,
    /// it is reported and left in place.
    /// </summary>
    public MaterialiseReport Materialise(string profileName, ProfileManifest manifest)
    {
        var profileDir = Path.Combine(_syncRoot, profileName);
        Directory.CreateDirectory(profileDir);

        var created = new List<string>();
        var repointed = new List<string>();
        var removed = new List<string>();
        var missing = new List<string>();
        var leftAlone = new List<string>();

        var wanted = manifest.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in wanted)
        {
            var target = Path.Combine(LibraryRoot, entry);
            if (!Directory.Exists(target)) { missing.Add(entry); continue; }

            var link = Path.Combine(profileDir, entry);

            if (!Directory.Exists(link))
            {
                _junctions.Create(link, target);
                created.Add(entry);
                continue;
            }

            if (!_junctions.IsJunction(link))
            {
                // A real folder of the user's, sitting where a link belongs.
                leftAlone.Add(entry);
                continue;
            }

            if (!SamePath(_junctions.GetTarget(link), target))
            {
                _junctions.Repoint(link, target);
                repointed.Add(entry);
            }
        }

        foreach (var directory in Directory.GetDirectories(profileDir))
        {
            var name = Path.GetFileName(directory);
            if (wanted.Contains(name)) continue;

            if (_junctions.IsJunction(directory))
            {
                _junctions.RemoveLink(directory);   // cannot recurse into the library
                removed.Add(name);
            }
            else
            {
                leftAlone.Add(name);                // real folder, not ours to delete
            }
        }

        return new MaterialiseReport(created, repointed, removed, missing, leftAlone);
    }

    private static bool SamePath(string? a, string b) =>
        a is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // --- Adopting existing profiles ----------------------------------------
    // Profiles built before the library existed hold real copies. Editing one
    // means turning those copies into links without ever losing a folder.

    public string BackupRoot => Path.Combine(_syncRoot, ".backup");

    /// <summary>What a profile folder currently holds, entry by entry.</summary>
    public IReadOnlyList<ProfileEntry> Analyse(string profileName)
    {
        var profileDir = Path.Combine(_syncRoot, profileName);
        if (!Directory.Exists(profileDir)) return Array.Empty<ProfileEntry>();

        var entries = ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProfileEntry>();

        foreach (var directory in Directory.GetDirectories(profileDir))
        {
            var name = Path.GetFileName(directory);

            if (_junctions.IsJunction(directory))
            {
                var target = _junctions.GetTarget(directory);
                var linked = entries.FirstOrDefault(e => SamePath(target, Path.Combine(LibraryRoot, e)));
                result.Add(new ProfileEntry(name, IsLink: true, LibraryEntry: linked, Suggestion: null));
                continue;
            }

            result.Add(new ProfileEntry(name, IsLink: false, LibraryEntry: null, Suggestion: SuggestLibraryEntry(name)));
        }

        return result;
    }

    /// <summary>
    /// The library entry a real profile folder corresponds to, matching first by
    /// name and then by name with the workshop suffix stripped.
    /// </summary>
    public string? SuggestLibraryEntry(string folderName)
    {
        var entries = ListEntries();

        var exact = entries.FirstOrDefault(e => string.Equals(e, folderName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var underscore = folderName.LastIndexOf('_');
        if (underscore > 0)
        {
            var suffix = folderName[(underscore + 1)..];
            if (suffix.Length >= 6 && suffix.All(char.IsDigit))
            {
                var stripped = folderName[..underscore];
                return entries.FirstOrDefault(e => string.Equals(e, stripped, StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    /// <summary>
    /// Move a real folder out of a profile and into the library, then link it
    /// back. For mods that were never Workshop items — a hand-installed copy has
    /// no other home, so this is a move, never a copy.
    /// </summary>
    public string AdoptIntoLibrary(string profileName, string folderName)
    {
        var source = Path.Combine(_syncRoot, profileName, folderName);
        if (!Directory.Exists(source))
            throw new UnsafePathException($"No such folder: {source}");
        if (_junctions.IsJunction(source))
            throw new UnsafePathException($"'{folderName}' is already a link.");

        var entry = StripWorkshopSuffix(folderName);
        var destination = Path.Combine(LibraryRoot, entry);
        if (Directory.Exists(destination))
            throw new UnsafePathException(
                $"The library already has '{entry}'. Replace the copy with a link instead of adopting it.");

        Directory.CreateDirectory(LibraryRoot);
        Directory.Move(source, destination);
        _junctions.Create(Path.Combine(_syncRoot, profileName, entry), destination);
        return entry;
    }

    /// <summary>
    /// Swap a redundant real copy for a link to the library entry it duplicates.
    /// The copy is moved to a timestamped backup, never deleted — nothing here
    /// can lose a mod if the match was wrong.
    /// </summary>
    public string ReplaceWithLink(string profileName, string folderName, string libraryEntry)
    {
        var profileDir = Path.Combine(_syncRoot, profileName);
        var source = Path.Combine(profileDir, folderName);
        var target = Path.Combine(LibraryRoot, libraryEntry);

        if (!Directory.Exists(source)) throw new UnsafePathException($"No such folder: {source}");
        if (_junctions.IsJunction(source)) throw new UnsafePathException($"'{folderName}' is already a link.");
        if (!Directory.Exists(target)) throw new UnsafePathException($"Library has no entry '{libraryEntry}'.");

        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), profileName);
        Directory.CreateDirectory(backup);
        Directory.Move(source, Path.Combine(backup, folderName));

        var link = Path.Combine(profileDir, libraryEntry);
        if (!Directory.Exists(link)) _junctions.Create(link, target);
        return libraryEntry;
    }

    // --- Inspecting the library --------------------------------------------

    /// <summary>
    /// Everything known about one library entry. Size is measured on demand:
    /// walking every mod would mean scanning gigabytes on each refresh.
    /// </summary>
    public LibraryEntryInfo Describe(string entry, bool measure = true)
    {
        var path = Path.Combine(LibraryRoot, entry);
        var metadata = ReadMetadata(entry);

        long size = 0;
        var files = 0;
        if (measure && Directory.Exists(path))
        {
            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    size += file.Length;
                    files++;
                }
            }
            catch (IOException) { }
        }

        return new LibraryEntryInfo(
            Entry: entry,
            Path: path,
            Name: metadata?.Name ?? entry,
            Description: metadata?.Description ?? string.Empty,
            PreviewPath: GetCachedImage(entry),
            WorkshopId: metadata?.Id,
            ImportedUtc: metadata?.ImportedUtc,
            SizeBytes: size,
            FileCount: files,
            UpstreamTimeUpdated: metadata?.TimeUpdated ?? 0);
    }

    /// <summary>Which profile manifests reference this entry.</summary>
    public IReadOnlyList<string> ProfilesUsing(string entry)
    {
        var using_ = new List<string>();
        foreach (var profile in ListManifests())
        {
            try
            {
                if (LoadManifest(profile).Mods.Contains(entry, StringComparer.OrdinalIgnoreCase))
                    using_.Add(profile);
            }
            catch (ConfigSchemaMismatchException)
            {
                // A manifest we cannot read is not evidence either way.
            }
        }
        return using_;
    }

    /// <summary>
    /// Take a mod out of the library, moving it to a timestamped backup.
    /// Refuses while a profile still references it — removing it underneath a
    /// manifest would leave that profile silently short a mod.
    /// </summary>
    public string RemoveFromLibrary(string entry)
    {
        var path = Path.Combine(LibraryRoot, entry);
        if (!Directory.Exists(path))
            throw new UnsafePathException($"Library has no entry '{entry}'.");

        var inUse = ProfilesUsing(entry);
        if (inUse.Count > 0)
            throw new UnsafePathException(
                $"'{entry}' is still used by: {string.Join(", ", inUse)}. Untick it there first.");

        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), LibraryFolderName);
        Directory.CreateDirectory(backup);
        var destination = Path.Combine(backup, entry);
        Directory.Move(path, destination);
        return destination;
    }

    private static string StripWorkshopSuffix(string folderName)
    {
        var underscore = folderName.LastIndexOf('_');
        if (underscore <= 0) return Sanitise(folderName);

        var suffix = folderName[(underscore + 1)..];
        return suffix.Length >= 6 && suffix.All(char.IsDigit)
            ? Sanitise(folderName[..underscore])
            : Sanitise(folderName);
    }
}

/// <summary>A mod in the shared library, with whatever was captured at import time.</summary>
public sealed record LibraryEntryInfo(
    string Entry,
    string Path,
    string Name,
    string Description,
    string? PreviewPath,
    string? WorkshopId,
    string? ImportedUtc,
    long SizeBytes,
    int FileCount,
    long UpstreamTimeUpdated = 0)
{
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);

    /// <summary>Entries imported before workshop ids were recorded, or added by hand.</summary>
    public bool HasWorkshopOrigin => !string.IsNullOrWhiteSpace(WorkshopId);
}

/// <summary>One folder inside a profile: a link into the library, or a real copy.</summary>
public sealed record ProfileEntry(string Name, bool IsLink, string? LibraryEntry, string? Suggestion)
{
    /// <summary>A real copy that duplicates something already in the library.</summary>
    public bool IsRedundantCopy => !IsLink && Suggestion is not null;

    /// <summary>A real copy with no library counterpart — hand-installed, and only here.</summary>
    public bool NeedsAdopting => !IsLink && Suggestion is null;
}

public sealed class ConfigSchemaMismatchException : Exception
{
    public ConfigSchemaMismatchException(string message) : base(message) { }
}
