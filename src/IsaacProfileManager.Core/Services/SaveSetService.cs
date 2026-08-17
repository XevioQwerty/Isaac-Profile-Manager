using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>Everything that must be true before save files may be replaced.</summary>
public sealed record SaveSwapPreconditions(
    bool IsaacClosed,
    bool CloudDisabled,
    SteamCloudState CloudState,
    bool RemoteDirFound,
    bool BuildMatches,
    string? BuildProblem)
{
    public bool CanActivate => IsaacClosed && CloudDisabled && RemoteDirFound && BuildMatches;

    public IReadOnlyList<string> Blockers
    {
        get
        {
            var blockers = new List<string>();
            if (!IsaacClosed) blockers.Add("Isaac is running. It writes save state on exit, so a swap now would be lost.");
            if (!RemoteDirFound) blockers.Add("Steam's save folder could not be found.");
            if (!CloudDisabled)
                blockers.Add(CloudState == SteamCloudState.Unknown
                    ? "Steam Cloud state could not be read. Turn Cloud off for Isaac to be sure."
                    : "Steam Cloud is on for Isaac. It would restore the saves you replace — turn it off in the game's properties.");
            if (!BuildMatches && BuildProblem is not null) blockers.Add(BuildProblem);
            return blockers;
        }
    }
}

public sealed record SaveFileState(string FileName, long Length, string Sha1, DateTime Modified);

/// <summary>
/// Captures, backs up and restores sets of Isaac save files.
///
/// The saves live in Steam's cloud folder
/// (<c>userdata\&lt;id&gt;\250900\remote\</c>) as a handful of ~5 KB files, with
/// the two builds separated by filename prefix rather than by folder. So a set
/// is a file copy — the folder itself is Steam's and must not be junctioned.
///
/// Every mutation is preceded by a timestamped backup. REPENTOGON's own
/// <c>save_backups\</c> is one snapshot per day and gets overwritten by a later
/// run the same day, so it is not a substitute.
/// </summary>
public sealed class SaveSetService
{
    public const string SetsFolderName = ".saves";
    public const string MetadataFileName = "set.json";
    public const string SyncStatusFileName = "rgon_savesyncstatus.json";

    /// <summary>REPENTOGON's saves. Vanilla's use the <c>rep+</c> prefix.</summary>
    public const string RepentogonPrefix = "rgon_steam_";
    public const string VanillaPrefix = "rep+";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGameProcessService _process;
    private readonly SteamCloudService _cloud;
    private readonly string _syncRoot;

    public SaveSetService(IGameProcessService process, SteamCloudService cloud, string syncRoot)
    {
        _process = process;
        _cloud = cloud;
        _syncRoot = syncRoot;
    }

    public string SetsRoot => Path.Combine(_syncRoot, SetsFolderName);
    public string BackupRoot => Path.Combine(_syncRoot, ".backup", "saves");

    /// <summary>Files in the live folder that belong to a save set. Never touches anything else.</summary>
    public static bool IsSaveFile(string fileName) =>
        (fileName.StartsWith(RepentogonPrefix, StringComparison.OrdinalIgnoreCase) ||
         fileName.StartsWith(VanillaPrefix, StringComparison.OrdinalIgnoreCase) ||
         fileName.Equals(SyncStatusFileName, StringComparison.OrdinalIgnoreCase)) &&
        (fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
         fileName.Equals(SyncStatusFileName, StringComparison.OrdinalIgnoreCase));

    public static GameBuild BuildOf(IEnumerable<string> fileNames)
    {
        var repentogon = false;
        var vanilla = false;
        foreach (var name in fileNames)
        {
            if (name.StartsWith(RepentogonPrefix, StringComparison.OrdinalIgnoreCase)) repentogon = true;
            else if (name.StartsWith(VanillaPrefix, StringComparison.OrdinalIgnoreCase)) vanilla = true;
        }
        return (repentogon, vanilla) switch
        {
            (true, true) => GameBuild.Both,
            (true, false) => GameBuild.Repentogon,
            (false, true) => GameBuild.Vanilla,
            _ => GameBuild.Unknown,
        };
    }

    /// <summary>Slot numbers present, from the trailing digit of persistentgamedata files.</summary>
    public static IReadOnlyList<int> SlotsOf(IEnumerable<string> fileNames) =>
        fileNames
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => n.Contains("persistentgamedata", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[^1])
            .Where(char.IsDigit)
            .Select(c => c - '0')
            .Distinct()
            .OrderBy(n => n)
            .ToList();

    // --- The live folder ----------------------------------------------------

    public string? LiveFolder => _cloud.GetStatus().RemoteDir;

    public IReadOnlyList<SaveFileState> ReadLive()
    {
        var folder = LiveFolder;
        if (folder is null || !Directory.Exists(folder)) return Array.Empty<SaveFileState>();

        return new DirectoryInfo(folder).GetFiles()
            .Where(f => IsSaveFile(f.Name))
            .Select(f => new SaveFileState(f.Name, f.Length, Sha1Of(f.FullName), f.LastWriteTime))
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    // --- Sets ---------------------------------------------------------------

    public IReadOnlyList<string> ListSets()
    {
        if (!Directory.Exists(SetsRoot)) return Array.Empty<string>();
        return new DirectoryInfo(SetsRoot).GetDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, MetadataFileName)))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public SaveSet? LoadSet(string name)
    {
        var path = Path.Combine(SetsRoot, name, MetadataFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var set = JsonSerializer.Deserialize<SaveSet>(File.ReadAllText(path), SerializerOptions);
            if (set is null) return null;
            if (set.SchemaVersion != SaveSet.CurrentSchemaVersion)
                throw new ConfigSchemaMismatchException(
                    $"{path} has SchemaVersion {set.SchemaVersion}; this build understands {SaveSet.CurrentSchemaVersion}.");
            return set;
        }
        catch (JsonException ex)
        {
            throw new ConfigSchemaMismatchException($"{path} is not readable as JSON: {ex.Message}");
        }
    }

    public void SaveSetMetadata(SaveSet set)
    {
        var folder = Path.Combine(SetsRoot, set.Name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MetadataFileName),
                          JsonSerializer.Serialize(set, SerializerOptions), new UTF8Encoding(false));
    }

    /// <summary>
    /// Copy the current live saves into a new set. Read-only with respect to the
    /// live folder, so it is safe whatever Steam Cloud is doing.
    /// </summary>
    public SaveSet Capture(string name, string modProfile, IEnumerable<string>? players = null, string notes = "")
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(name));

        var folder = LiveFolder;
        if (folder is null || !Directory.Exists(folder))
            throw new UnsafePathException("Steam's save folder could not be found.");

        var live = ReadLive();
        if (live.Count == 0)
            throw new UnsafePathException($"No save files found in {folder}.");

        var destination = Path.Combine(SetsRoot, name);
        if (Directory.Exists(destination))
            throw new UnsafePathException($"A save set called '{name}' already exists.");

        Directory.CreateDirectory(destination);
        foreach (var file in live)
            File.Copy(Path.Combine(folder, file.FileName), Path.Combine(destination, file.FileName), overwrite: false);

        var set = new SaveSet
        {
            Name = name,
            Build = BuildOf(live.Select(f => f.FileName)),
            ModProfile = modProfile,
            Players = players?.ToList() ?? new List<string>(),
            Notes = notes,
            Files = live.Select(f => f.FileName).ToList(),
            Slots = SlotsOf(live.Select(f => f.FileName)).ToList(),
            Sha1 = live.ToDictionary(f => f.FileName, f => f.Sha1, StringComparer.OrdinalIgnoreCase),
            CapturedUtc = DateTime.UtcNow.ToString("o"),
        };

        SaveSetMetadata(set);
        return set;
    }

    /// <summary>
    /// Update the descriptive fields of an existing set. Never touches the save
    /// files, the recorded build, or the hashes — those describe what was
    /// captured and editing them would make the set lie about itself.
    /// </summary>
    public SaveSet EditMetadata(
        string name,
        string? notes = null,
        IEnumerable<string>? players = null,
        string? modProfile = null,
        IReadOnlyDictionary<string, string>? slotNotes = null)
    {
        var set = LoadSet(name) ?? throw new UnsafePathException($"No save set called '{name}'.");

        if (notes is not null) set.Notes = notes;
        if (players is not null) set.Players = players.Where(p => p.Length > 0).ToList();
        if (modProfile is not null) set.ModProfile = modProfile;

        if (slotNotes is not null)
        {
            set.SlotNotes = slotNotes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
        }

        SaveSetMetadata(set);
        return set;
    }

    /// <summary>Rename a set, moving its folder. The files inside are untouched.</summary>
    public SaveSet Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{newName}' is not usable as a folder name.", nameof(newName));

        var set = LoadSet(oldName) ?? throw new UnsafePathException($"No save set called '{oldName}'.");
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return set;

        var source = Path.Combine(SetsRoot, oldName);
        var destination = Path.Combine(SetsRoot, newName);
        if (Directory.Exists(destination))
            throw new UnsafePathException($"A save set called '{newName}' already exists.");

        Directory.Move(source, destination);
        set.Name = newName;
        SaveSetMetadata(set);
        return set;
    }

    /// <summary>Forget a set. The folder is moved to a backup, never deleted.</summary>
    public string DeleteSet(string name)
    {
        var source = Path.Combine(SetsRoot, name);
        if (!Directory.Exists(source))
            throw new UnsafePathException($"No save set called '{name}'.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var destination = Path.Combine(BackupRoot, $"{stamp}-removed-{name}");
        for (var n = 2; Directory.Exists(destination); n++)
            destination = Path.Combine(BackupRoot, $"{stamp}-removed-{name}-{n}");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
        return destination;
    }

    /// <summary>Copy the live saves aside, timestamped to the second. Returns the folder used.</summary>
    public string BackupLive(string reason = "swap")
    {
        var folder = LiveFolder;
        if (folder is null || !Directory.Exists(folder))
            throw new UnsafePathException("Steam's save folder could not be found.");

        // Two backups in the same second must not land in the same folder, or the
        // second one throws on an existing file and no backup is taken at all.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var destination = Path.Combine(BackupRoot, $"{stamp}-{reason}");
        for (var n = 2; Directory.Exists(destination); n++)
            destination = Path.Combine(BackupRoot, $"{stamp}-{reason}-{n}");

        Directory.CreateDirectory(destination);

        foreach (var file in ReadLive())
            File.Copy(Path.Combine(folder, file.FileName), Path.Combine(destination, file.FileName), overwrite: false);

        return destination;
    }

    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupRoot)) return Array.Empty<string>();
        return new DirectoryInfo(BackupRoot).GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => d.Name)
            .ToList();
    }

    // --- Activation ---------------------------------------------------------

    /// <summary>
    /// Check everything before touching a file. <paramref name="selectedBuild"/> is
    /// what the launcher is currently set to start.
    /// </summary>
    /// <param name="cloudAcknowledged">
    /// The user has confirmed in Steam's own dialog that Cloud is off. Steam only
    /// flushes that setting to disk when it exits, so the file we read can lag
    /// behind what they are looking at — without this the feature is unusable on
    /// a machine where the setting never appears to take.
    /// </param>
    public SaveSwapPreconditions Check(SaveSet set, GameBuild selectedBuild, bool cloudAcknowledged = false)
    {
        var cloud = _cloud.GetStatus();

        var buildMatches = true;
        string? buildProblem = null;

        if (set.Build == GameBuild.Unknown)
        {
            buildMatches = false;
            buildProblem = "This set has no recorded build, so it cannot be matched against the one you are launching.";
        }
        else if (selectedBuild != GameBuild.Unknown && set.Build != GameBuild.Both && set.Build != selectedBuild)
        {
            // Blocked, not warned: a cross-build load can destroy achievements.
            buildMatches = false;
            buildProblem =
                $"This set is from {set.BuildText}, but the launcher is set to start " +
                $"{(selectedBuild == GameBuild.Repentogon ? "REPENTOGON" : "vanilla")}. " +
                "Loading a save on the wrong build can destroy every achievement.";
        }

        return new SaveSwapPreconditions(
            IsaacClosed: !_process.IsIsaacRunning(),
            CloudDisabled: cloud.SafeToSwapSaves || cloudAcknowledged,
            CloudState: cloud.State,
            RemoteDirFound: cloud.RemoteDir is not null && Directory.Exists(cloud.RemoteDir),
            BuildMatches: buildMatches,
            BuildProblem: buildProblem);
    }

    /// <summary>Live files that differ from what the set recorded — i.e. unsaved progress.</summary>
    public IReadOnlyList<string> DetectDrift(SaveSet set)
    {
        var live = ReadLive().ToDictionary(f => f.FileName, f => f.Sha1, StringComparer.OrdinalIgnoreCase);

        return set.Sha1
            .Where(pair => live.TryGetValue(pair.Key, out var sha) && sha != pair.Value)
            .Select(pair => pair.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Back up the live saves, then copy the set over them. Refuses unless every
    /// precondition passes — the caller must not be able to force it.
    /// </summary>
    public string Activate(SaveSet set, GameBuild selectedBuild, bool cloudAcknowledged = false)
    {
        var checks = Check(set, selectedBuild, cloudAcknowledged);
        if (!checks.CanActivate)
            throw new UnsafePathException(string.Join("\n\n", checks.Blockers));

        var folder = LiveFolder!;
        var source = Path.Combine(SetsRoot, set.Name);
        if (!Directory.Exists(source))
            throw new UnsafePathException($"Save set folder is missing: {source}");

        var backup = BackupLive($"before-{set.Name}");

        // Remove only the save files this tool recognises, so anything else
        // Steam keeps in that folder is left alone.
        foreach (var existing in ReadLive())
            File.Delete(Path.Combine(folder, existing.FileName));

        foreach (var file in new DirectoryInfo(source).GetFiles())
        {
            if (file.Name.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file.FullName, Path.Combine(folder, file.Name), overwrite: true);
        }

        set.LastUsedUtc = DateTime.UtcNow.ToString("o");
        SaveSetMetadata(set);
        return backup;
    }

    /// <summary>Restore a timestamped backup over the live folder, backing up what is there first.</summary>
    public string RestoreBackup(string backupName)
    {
        var source = Path.Combine(BackupRoot, backupName);
        if (!Directory.Exists(source))
            throw new UnsafePathException($"No such backup: {source}");
        if (_process.IsIsaacRunning())
            throw new UnsafePathException("Isaac is running. Close it before restoring saves.");

        var folder = LiveFolder;
        if (folder is null || !Directory.Exists(folder))
            throw new UnsafePathException("Steam's save folder could not be found.");

        var safety = BackupLive("before-restore");

        foreach (var existing in ReadLive())
            File.Delete(Path.Combine(folder, existing.FileName));

        foreach (var file in new DirectoryInfo(source).GetFiles())
            File.Copy(file.FullName, Path.Combine(folder, file.Name), overwrite: true);

        return safety;
    }
}
