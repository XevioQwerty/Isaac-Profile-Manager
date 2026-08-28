using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IsaacProfileManager.Core.Services;

public sealed record LibraryVerification(string Entry, string? Recorded, string Actual)
{
    public bool IsRecorded => Recorded is not null;
    public bool Matches => Recorded is not null && Recorded == Actual;

    public string StatusText => Recorded is null ? "not recorded" : Matches ? "unchanged" : "CHANGED";
}

/// <summary>
/// A profile packaged for someone else: which mods, and what each one should
/// hash to. Contains no machine-local paths, so it is a single small file to
/// send — the receiving copy rebuilds the profile against its own library and
/// can prove the contents match byte for byte.
/// </summary>
public sealed class SharedProfile
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("ExportedUtc")]
    public string ExportedUtc { get; set; } = string.Empty;

    [JsonPropertyName("Mods")]
    public List<string> Mods { get; set; } = new();

    /// <summary>Entry name to content hash. The whole point of the file.</summary>
    [JsonPropertyName("Hashes")]
    public Dictionary<string, string> Hashes { get; set; } = new();

    /// <summary>
    /// Library entry name to Workshop published file id. Added after the format
    /// shipped, so it is optional: an export without it can still be compared
    /// against, it just cannot be fetched. Kept additive rather than bumping the
    /// schema so older exports stay readable.
    /// </summary>
    [JsonPropertyName("WorkshopIds")]
    public Dictionary<string, string> WorkshopIds { get; set; } = new();

    /// <summary>Entries with an id, which are the ones an import can download.</summary>
    public bool IsFetchable => WorkshopIds.Count > 0;
}

public sealed record ProfileDiffEntry(string Entry, ProfileDiffKind Kind, string? MyHash, string? TheirHash);

public enum ProfileDiffKind
{
    /// <summary>Both have it and the contents hash the same. The only good outcome.</summary>
    Identical,

    /// <summary>Both have it, but the files differ — same name, different mod.</summary>
    ContentDiffers,

    /// <summary>Only in this profile.</summary>
    OnlyMine,

    /// <summary>Only in theirs.</summary>
    OnlyTheirs,

    /// <summary>Both list it, but one side has no hash to compare.</summary>
    Unverified,
}

public sealed record ProfileDiff(IReadOnlyList<ProfileDiffEntry> Entries)
{
    public IEnumerable<ProfileDiffEntry> Problems =>
        Entries.Where(e => e.Kind is not ProfileDiffKind.Identical);

    /// <summary>True when both sides hold exactly the same mods with the same bytes.</summary>
    public bool IsIdentical => Entries.All(e => e.Kind == ProfileDiffKind.Identical);

    public int Count(ProfileDiffKind kind) => Entries.Count(e => e.Kind == kind);

    public string Summary => IsIdentical
        ? $"Identical — {Entries.Count} mods, same contents on both sides."
        : $"{Count(ProfileDiffKind.ContentDiffers)} differ, " +
          $"{Count(ProfileDiffKind.OnlyMine)} only yours, " +
          $"{Count(ProfileDiffKind.OnlyTheirs)} only theirs, " +
          $"{Count(ProfileDiffKind.Unverified)} unverified.";
}

/// <summary>
/// Hashes library mods so two people can prove they are running the same files.
///
/// Identical folder names are not enough: same name with different contents is
/// one of the listed causes of a desync, and it is invisible to a folder listing.
/// A hash makes it a single value to compare.
///
/// Hashes cover relative path and file content, so they are stable across
/// machines and independent of timestamps or where the library lives.
/// </summary>
public sealed class LibraryHashService
{
    public const string HashesFileName = "hashes.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ModLibraryService _library;

    public LibraryHashService(ModLibraryService library) => _library = library;

    public string HashesPath => Path.Combine(_library.MetadataRoot, HashesFileName);

    /// <summary>
    /// A recorded hash plus a cheap fingerprint of the folder it came from.
    /// Hashing the whole library reads every byte — measured at nearly four
    /// minutes for 1.7 GB — so repeat runs compare the stamp first and only
    /// re-read mods whose files actually changed.
    /// </summary>
    public sealed record HashRecord(
        [property: JsonPropertyName("Hash")] string Hash,
        [property: JsonPropertyName("Stamp")] string Stamp);

    /// <summary>File count, total bytes and newest write time — cheap, and enough to notice an edit.</summary>
    public string ComputeStamp(string entry)
    {
        var folder = Path.Combine(_library.LibraryRoot, entry);
        if (!Directory.Exists(folder)) return string.Empty;

        long count = 0, bytes = 0, newest = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            count++;
            bytes += file.Length;
            newest = Math.Max(newest, file.LastWriteTimeUtc.Ticks);
        }

        return $"{count}:{bytes}:{newest}";
    }

    /// <summary>
    /// Content hash of one library entry: every file's relative path and bytes,
    /// in a fixed order. Timestamps and absolute paths are deliberately excluded
    /// so two machines that synced the same mod agree.
    /// </summary>
    public string ComputeHash(string entry)
    {
        var folder = Path.Combine(_library.LibraryRoot, entry);
        if (!Directory.Exists(folder))
            throw new UnsafePathException($"Library has no entry '{entry}'.");

        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(folder, f.FullName).Replace('\\', '/').ToLowerInvariant(), File: f))
            .OrderBy(x => x.Relative, StringComparer.Ordinal)
            .ToList();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];

        foreach (var (relative, file) in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{relative}:{file.Length}\n"));

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                                              FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public Dictionary<string, HashRecord> LoadRecords()
    {
        var empty = new Dictionary<string, HashRecord>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(HashesPath)) return empty;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, HashRecord>>(File.ReadAllText(HashesPath));
            return loaded is null ? empty : new Dictionary<string, HashRecord>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    /// <summary>Entry to hash, which is the form shared with other people.</summary>
    public Dictionary<string, string> LoadHashes() =>
        LoadRecords().ToDictionary(p => p.Key, p => p.Value.Hash, StringComparer.OrdinalIgnoreCase);

    public void SaveRecords(IReadOnlyDictionary<string, HashRecord> records)
    {
        Directory.CreateDirectory(_library.MetadataRoot);
        var ordered = records.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(p => p.Key, p => p.Value);
        File.WriteAllText(HashesPath, JsonSerializer.Serialize(ordered, SerializerOptions), new UTF8Encoding(false));
    }

    /// <summary>
    /// Hash every entry and record the result, skipping mods whose stamp shows
    /// nothing changed. Run after importing.
    /// </summary>
    public IReadOnlyList<LibraryVerification> RecordAll(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var records = LoadRecords();
        var results = new List<LibraryVerification>();

        foreach (var entry in _library.ListEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stamp = ComputeStamp(entry);
            records.TryGetValue(entry, out var existing);

            if (existing is not null && existing.Stamp == stamp)
            {
                results.Add(new LibraryVerification(entry, existing.Hash, existing.Hash));
                continue;
            }

            progress?.Report(entry);
            var actual = ComputeHash(entry);
            results.Add(new LibraryVerification(entry, existing?.Hash, actual));
            records[entry] = new HashRecord(actual, stamp);
        }

        SaveRecords(records);
        return results;
    }

    /// <summary>
    /// Re-read every byte and compare against what was recorded, changing
    /// nothing. Deliberately ignores the stamp — this is the "prove it" pass, so
    /// it must not trust a fingerprint that an edit could preserve.
    /// </summary>
    public IReadOnlyList<LibraryVerification> VerifyAll(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var records = LoadRecords();
        var results = new List<LibraryVerification>();

        foreach (var entry in _library.ListEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(entry);
            records.TryGetValue(entry, out var existing);
            results.Add(new LibraryVerification(entry, existing?.Hash, ComputeHash(entry)));
        }

        return results;
    }

    // --- Sharing ------------------------------------------------------------

    public SharedProfile Export(string profileName, ProfileManifest manifest)
    {
        var hashes = LoadHashes();

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
        {
            // A hand-installed mod has no id. It travels in the list so the
            // recipient is told about it, but nothing can fetch it for them.
            var id = _library.GetCachedId(mod);
            if (!string.IsNullOrWhiteSpace(id)) ids[mod] = id;
        }

        return new SharedProfile
        {
            WorkshopIds = ids,
            Name = profileName,
            Notes = manifest.Notes,
            ExportedUtc = DateTime.UtcNow.ToString("o"),
            Mods = manifest.Mods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            Hashes = manifest.Mods
                .Where(hashes.ContainsKey)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(m => m, m => hashes[m], StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The entire library as one shareable set, for "here is everything I have"
    /// rather than a single profile.
    /// </summary>
    public SharedProfile ExportLibrary(string name, string notes = "")
    {
        var entries = _library.ListEntries().ToList();
        return Export(name, new ProfileManifest { Mods = entries, Notes = notes });
    }

    public void WriteExport(SharedProfile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, SerializerOptions), new UTF8Encoding(false));
    }

    public static SharedProfile ReadExport(string path)
    {
        SharedProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<SharedProfile>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigSchemaMismatchException($"{path} is not a readable profile export: {ex.Message}");
        }

        if (profile is null) throw new ConfigSchemaMismatchException($"{path} is empty.");
        if (profile.SchemaVersion != SharedProfile.CurrentSchemaVersion)
            throw new ConfigSchemaMismatchException(
                $"{path} has SchemaVersion {profile.SchemaVersion}; this build understands {SharedProfile.CurrentSchemaVersion}.");

        return profile;
    }

    /// <summary>
    /// Compare a local profile against someone else's export. Same name with a
    /// different hash is the case that matters — it looks correct in every
    /// folder listing and desyncs anyway.
    /// </summary>
    public ProfileDiff Compare(ProfileManifest mine, SharedProfile theirs)
    {
        var myHashes = LoadHashes();
        var myMods = mine.Mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var theirMods = theirs.Mods.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<ProfileDiffEntry>();

        foreach (var entry in myMods.Union(theirMods, StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            var mineHas = myMods.Contains(entry);
            var theirsHas = theirMods.Contains(entry);

            myHashes.TryGetValue(entry, out var myHash);
            theirs.Hashes.TryGetValue(entry, out var theirHash);

            var kind =
                mineHas && !theirsHas ? ProfileDiffKind.OnlyMine
                : !mineHas && theirsHas ? ProfileDiffKind.OnlyTheirs
                : myHash is null || theirHash is null ? ProfileDiffKind.Unverified
                : myHash == theirHash ? ProfileDiffKind.Identical
                : ProfileDiffKind.ContentDiffers;

            entries.Add(new ProfileDiffEntry(entry, kind, myHash, theirHash));
        }

        return new ProfileDiff(entries);
    }
}
