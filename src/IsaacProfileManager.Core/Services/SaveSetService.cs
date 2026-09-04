using System.IO.Compression;
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
    string? BuildProblem,
    bool CloudApplies = true)
{
    public bool CanActivate => IsaacClosed && CloudDisabled && RemoteDirFound && BuildMatches;

    public IReadOnlyList<string> Blockers
    {
        get
        {
            var blockers = new List<string>();
            if (!IsaacClosed) blockers.Add("Isaac is running. It writes save state on exit, so a swap now would be lost.");
            if (!RemoteDirFound) blockers.Add("The game's save folder could not be found.");
            if (CloudApplies && !CloudDisabled)
                blockers.Add(CloudState == SteamCloudState.Unknown
                    ? "Steam Cloud state could not be read. Turn Cloud off for Isaac to be sure."
                    : "Steam Cloud is on for Isaac. It would restore the saves you replace — turn it off in the game's properties.");
            if (!BuildMatches && BuildProblem is not null) blockers.Add(BuildProblem);
            return blockers;
        }
    }
}

public sealed record SaveFileState(string FileName, long Length, string Sha1, DateTime Modified);

/// <summary>What an activation did beyond the save files themselves.</summary>
public sealed record SaveActivation(string Backup, CarryReport? ModData, CarryReport? RepentogonState);

/// <summary>One earlier revision of a set, filed before it was overwritten.</summary>
public sealed record HistoryEntry(
    string Name,
    string Path,
    DateTime When,
    string? Device,
    string? GameVersion,
    int Revision,
    int FileCount,
    long SizeBytes)
{
    public double SizeKb => Math.Round(SizeBytes / 1024d, 1);

    public string Label
    {
        get
        {
            var parts = new List<string> { When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") };
            if (Revision > 0) parts.Add($"rev {Revision}");
            if (GameVersion is { Length: > 0 }) parts.Add(GameVersion);
            if (Device is { Length: > 0 }) parts.Add(Device);
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// The machine-specific pieces a <see cref="SaveSetService"/> needs beyond the
/// folders. All optional, so a test or the PowerShell side can leave them out.
/// </summary>
public sealed class SaveSetOptions
{
    /// <summary>The game's <c>data\</c> folder. Null derives it from the game directory.</summary>
    public string? ModDataRoot { get; init; }

    /// <summary>REPENTOGON's settings folder under Documents. Null disables carrying its state.</summary>
    public string? RepentogonStateFolder { get; init; }

    /// <summary>This machine's id, stamped on every capture. Null leaves the clock alone.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Short device name for history folder names. Null uses the id.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Reads the game's J-number from log.txt. Null leaves the recorded version alone.</summary>
    public Func<string?>? ReadGameVersion { get; init; }

    /// <summary>File the previous revision into <c>.history</c> before a capture overwrites it.</summary>
    public bool KeepHistory { get; init; } = true;

    /// <summary>How many revisions to keep per set. History entries are copies, so pruning loses nothing unique.</summary>
    public int HistoryKeep { get; init; } = 30;
}

/// <summary>
/// Captures, backs up and restores sets of Isaac save files.
///
/// The saves live in Steam's cloud folder
/// (<c>userdata\&lt;id&gt;\250900\remote\</c>) as a handful of ~5 KB files, with
/// the two builds separated by filename prefix rather than by folder. So a set
/// is a file copy — the folder itself is Steam's and must not be junctioned.
///
/// Since 2.0 a set also carries the per-slot state the game keeps elsewhere
/// (see <see cref="SlotStateCarrier"/>), and files its previous revision into
/// <c>.history\</c> before every overwrite. That history is the undo for
/// everything else, and it exists before any of the sync code does.
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
    public const string HistoryFolderName = ".history";

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
    private readonly string? _configuredSaveFolder;
    private readonly string? _gameDir;
    private readonly SaveSetOptions _options;

    public SaveSetService(IGameProcessService process, SteamCloudService cloud, string syncRoot,
                          string? configuredSaveFolder = null, string? gameDir = null,
                          SaveSetOptions? options = null)
    {
        _process = process;
        _cloud = cloud;
        _syncRoot = syncRoot;
        _configuredSaveFolder = configuredSaveFolder;
        _gameDir = gameDir;
        _options = options ?? new SaveSetOptions();

        ModData = new ModDataCarrier(
            _options.ModDataRoot ?? (string.IsNullOrWhiteSpace(gameDir) ? null : Path.Combine(gameDir, ModDataCarrier.GameSubfolder)));
        RepentogonState = new RepentogonStateCarrier(_options.RepentogonStateFolder);
    }

    /// <summary>Each mod's per-slot save data, carried with a set.</summary>
    public ModDataCarrier ModData { get; }

    /// <summary>REPENTOGON's per-slot modded achievement state, carried with a set.</summary>
    public RepentogonStateCarrier RepentogonState { get; }

    private IEnumerable<SlotStateCarrier> Carriers
    {
        get
        {
            yield return ModData;
            yield return RepentogonState;
        }
    }

    /// <summary>Which folder was chosen for the live saves, and on what grounds.</summary>
    public SaveFolderResolution ResolveLiveFolder() =>
        new SaveLocationService(_cloud).Resolve(_configuredSaveFolder, _gameDir);

    public string SetsRoot => Path.Combine(_syncRoot, SetsFolderName);
    public string BackupRoot => Path.Combine(_syncRoot, ".backup", "saves");
    public string SetFolder(string name) => Path.Combine(SetsRoot, name);
    public string HistoryRoot(string name) => Path.Combine(SetFolder(name), HistoryFolderName);

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

    /// <summary>
    /// The folder the game reads and writes saves in.
    ///
    /// This used to be Steam's cloud folder unconditionally, which is only
    /// correct for a copy running against the real Steam client. Everything
    /// else — anything with an emulated steam_api — writes elsewhere, so the
    /// app was watching a directory the game never touched.
    /// </summary>
    public string? LiveFolder => ResolveLiveFolder().Path;

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

    public SaveSet? LoadSet(string name) => LoadSetFrom(Path.Combine(SetsRoot, name, MetadataFileName));

    private static SaveSet? LoadSetFrom(string path)
    {
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

        CarryLiveState(set, destination);
        Stamp(set);
        SaveSetMetadata(set);
        return set;
    }

    /// <summary>
    /// Start a save set with nothing in it, for a fresh game rather than a copy
    /// of one you already have - "vanilla online" and "vanilla solo" as separate
    /// unlock states, say.
    ///
    /// Activating an empty set clears the live save files and puts nothing back,
    /// and Isaac writes a new save on the next launch. Verified 2026-08-17 that
    /// emptying the folder does not trigger a Cloud restore on this install: the
    /// game produced a fresh 4,068-byte persistentgamedata1.dat and Steam
    /// rewrote remotecache.vdf from local disk.
    ///
    /// The build has to be given rather than derived, because there are no files
    /// to derive it from and a set with no build is refused at activation - the
    /// cross-build check is what stops a J273 save being opened on retail.
    /// </summary>
    public SaveSet CreateEmpty(string name, GameBuild build, string modProfile,
                               IEnumerable<string>? players = null, string notes = "")
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(name));
        if (build == GameBuild.Unknown)
            throw new ArgumentException("A new save set needs a build; it cannot be worked out from an empty folder.", nameof(build));

        var destination = Path.Combine(SetsRoot, name);
        if (Directory.Exists(destination))
            throw new UnsafePathException($"A save set called '{name}' already exists.");

        Directory.CreateDirectory(destination);

        var set = new SaveSet
        {
            Name = name,
            Build = build,
            ModProfile = modProfile,
            Players = players?.ToList() ?? new List<string>(),
            Notes = notes,
            Files = new List<string>(),
            Slots = new List<int>(),
            Sha1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CapturedUtc = DateTime.UtcNow.ToString("o"),
        };

        if (_options.DeviceId is not null) set.Device = _options.DeviceId;
        SaveSetMetadata(set);
        return set;
    }

    /// <summary>
    /// Copy whatever is live now into a set that already exists - the second half
    /// of starting a fresh save, once the game has generated one, and what exit
    /// capture does after every session.
    ///
    /// The recorded build is left alone. The files are what the chosen build
    /// produced, so re-deriving it could only ever agree or mean something went
    /// wrong, and silently rewriting it would erase the check that protects the
    /// achievements. The previous revision is filed into history first.
    /// </summary>
    public SaveSet CaptureInto(SaveSet set)
    {
        var folder = LiveFolder;
        if (folder is null || !Directory.Exists(folder))
            throw new UnsafePathException("Steam's save folder could not be found.");

        var destination = Path.Combine(SetsRoot, set.Name);
        if (!Directory.Exists(destination))
            throw new UnsafePathException($"Save set folder is missing: {destination}");

        var live = ReadLive();
        if (live.Count == 0)
            throw new UnsafePathException($"No save files found in {folder}. Launch the game once so it writes one.");

        var found = BuildOf(live.Select(f => f.FileName));
        if (set.Build != GameBuild.Both && found != GameBuild.Both && found != set.Build)
            throw new UnsafePathException(
                $"'{set.Name}' is a {set.BuildText} set, but the live folder holds {found} saves. " +
                "Capturing them would make the set lie about which build it came from.");

        if (_options.KeepHistory)
        {
            var filed = WriteHistory(set);
            if (filed is not null) set.ParentRevision = filed;
            PruneHistory(set.Name, _options.HistoryKeep);
        }

        ClearSetContents(destination);

        foreach (var file in live)
            File.Copy(Path.Combine(folder, file.FileName), Path.Combine(destination, file.FileName), overwrite: true);

        set.Files = live.Select(f => f.FileName).ToList();
        set.Slots = SlotsOf(live.Select(f => f.FileName)).ToList();
        set.Sha1 = live.ToDictionary(f => f.FileName, f => f.Sha1, StringComparer.OrdinalIgnoreCase);
        set.CapturedUtc = DateTime.UtcNow.ToString("o");

        CarryLiveState(set, destination);
        Stamp(set);
        SaveSetMetadata(set);
        return set;
    }

    /// <summary>
    /// Replace rather than merge: a leftover file from an earlier capture would
    /// travel with the set and be restored over a save it predates. History is
    /// kept; it is the record of exactly those earlier captures.
    /// </summary>
    private static void ClearSetContents(string setFolder)
    {
        foreach (var stale in new DirectoryInfo(setFolder).GetFiles())
        {
            if (stale.Name.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase)) continue;
            stale.Delete();
        }

        foreach (var sub in new DirectoryInfo(setFolder).GetDirectories())
        {
            if (sub.Name.Equals(HistoryFolderName, StringComparison.OrdinalIgnoreCase)) continue;
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            sub.Delete(recursive: true);
        }
    }

    /// <summary>Copy the per-slot state the game keeps outside the save folder into the set.</summary>
    private void CarryLiveState(SaveSet set, string setFolder)
    {
        set.ModDataCaptured = ModData.Available;
        set.ModData = ModData.Available
            ? ModData.CaptureInto(setFolder, set.Slots)
            : new Dictionary<string, string>();

        set.RepentogonStateCaptured = RepentogonState.Available;
        set.RepentogonState = RepentogonState.Available
            ? RepentogonState.CaptureInto(setFolder, set.Slots)
            : new Dictionary<string, string>();
    }

    /// <summary>Record which device made this revision, advance its clock, and note the game version.</summary>
    private void Stamp(SaveSet set)
    {
        if (_options.DeviceId is { Length: > 0 } device)
        {
            set.Device = device;
            set.Clock = VectorClock.Bump(set.Clock, device);
        }

        if (_options.ReadGameVersion is not null)
        {
            try { set.GameVersion = _options.ReadGameVersion(); }
            catch (IOException) { /* the log can be mid-write; the version stays as it was */ }
        }
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

    // --- History ------------------------------------------------------------

    /// <summary>
    /// File the set's current contents under <c>.history\&lt;utc&gt;-&lt;device&gt;\</c>.
    /// Returns the entry name, or null when the set holds nothing to file.
    /// A set is ~24 KB; twenty revisions is half a megabyte.
    /// </summary>
    public string? WriteHistory(SaveSet set)
    {
        var folder = Path.Combine(SetsRoot, set.Name);
        if (!Directory.Exists(folder)) return null;

        var hasContent = new DirectoryInfo(folder).GetFiles().Any(f => !f.Name.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase)) ||
                         Carriers.Any(c => c.ListInFolder(folder).Count > 0);
        if (!hasContent) return null;

        var device = DeviceService.SafeName(_options.DeviceName ?? ShortDevice(_options.DeviceId) ?? "local");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var root = HistoryRoot(set.Name);
        var name = $"{stamp}-{device}";
        var destination = Path.Combine(root, name);
        for (var n = 2; Directory.Exists(destination); n++)
        {
            name = $"{stamp}-{device}-{n}";
            destination = Path.Combine(root, name);
        }

        Directory.CreateDirectory(destination);
        CopySetContents(folder, destination);
        return name;
    }

    private static string? ShortDevice(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Length > 8 ? id[..8] : id;

    /// <summary>Copy a set's files and carried subfolders, never its history and never through a link.</summary>
    private static void CopySetContents(string source, string destination)
    {
        foreach (var file in new DirectoryInfo(source).GetFiles())
            File.Copy(file.FullName, Path.Combine(destination, file.Name), overwrite: true);

        foreach (var sub in new DirectoryInfo(source).GetDirectories())
        {
            if (sub.Name.Equals(HistoryFolderName, StringComparison.OrdinalIgnoreCase)) continue;
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            var target = Path.Combine(destination, sub.Name);
            Directory.CreateDirectory(target);
            CopySetContents(sub.FullName, target);
        }
    }

    public IReadOnlyList<HistoryEntry> ListHistory(string setName)
    {
        var root = HistoryRoot(setName);
        if (!Directory.Exists(root)) return Array.Empty<HistoryEntry>();

        var entries = new List<HistoryEntry>();
        foreach (var dir in new DirectoryInfo(root).GetDirectories())
        {
            SaveSet? filed = null;
            try { filed = LoadSetFrom(Path.Combine(dir.FullName, MetadataFileName)); }
            catch (ConfigSchemaMismatchException) { }

            long bytes = 0;
            var count = 0;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                count++;
            }

            var when = ParseStamp(dir.Name) ?? dir.CreationTimeUtc;
            entries.Add(new HistoryEntry(dir.Name, dir.FullName, when, ShortDevice(filed?.Device),
                                         filed?.GameVersion, VectorClock.Revision(filed?.Clock), count, bytes));
        }

        return entries.OrderByDescending(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static DateTime? ParseStamp(string folderName)
    {
        if (folderName.Length < 15) return null;
        return DateTime.TryParseExact(folderName[..15], "yyyyMMdd-HHmmss", null,
                                      System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                      out var when)
            ? when
            : null;
    }

    /// <summary>
    /// Put an earlier revision back as the set's current contents. What is
    /// current is filed first, so this is never destructive. The result is a new
    /// revision on this device, with the restored entry as its parent.
    /// </summary>
    public SaveSet RestoreHistory(string setName, string entryName)
    {
        var current = LoadSet(setName) ?? throw new UnsafePathException($"No save set called '{setName}'.");

        var root = Path.GetFullPath(HistoryRoot(setName)).TrimEnd(Path.DirectorySeparatorChar);
        var entry = Path.GetFullPath(Path.Combine(root, entryName));
        if (!entry.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException($"'{entryName}' is not inside the set's history.");
        if (!Directory.Exists(entry))
            throw new UnsafePathException($"No such history entry: {entryName}");

        var filed = LoadSetFrom(Path.Combine(entry, MetadataFileName))
                    ?? throw new UnsafePathException($"History entry '{entryName}' has no readable set.json.");

        var folder = SetFolder(setName);
        WriteHistory(current);
        ClearSetContents(folder);
        CopySetContents(entry, folder);

        // The filed metadata describes the restored bytes, which is what we
        // want — except the name, which may have changed since, and the clock,
        // which must advance because this is a new revision on this device.
        filed.Name = setName;
        filed.Clock = current.Clock.Count > 0 ? new Dictionary<string, int>(current.Clock) : filed.Clock;
        filed.ParentRevision = entryName;
        if (_options.DeviceId is { Length: > 0 } device)
        {
            filed.Device = device;
            filed.Clock = VectorClock.Bump(filed.Clock, device);
        }

        SaveSetMetadata(filed);
        return filed;
    }

    /// <summary>History entries are true copies, so retention may prune them: keep the newest <paramref name="keep"/>.</summary>
    public IReadOnlyList<HistoryEntry> PruneHistory(string setName, int keep = 20)
    {
        var doomed = ListHistory(setName).Skip(Math.Max(keep, 0)).ToList();
        foreach (var entry in doomed)
        {
            try { Directory.Delete(entry.Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return doomed;
    }

    // --- Backups ------------------------------------------------------------

    /// <summary>
    /// Copy the live saves aside, timestamped to the second, along with the
    /// per-slot state the game keeps elsewhere. Returns the folder used.
    /// </summary>
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

        var live = ReadLive();
        foreach (var file in live)
            File.Copy(Path.Combine(folder, file.FileName), Path.Combine(destination, file.FileName), overwrite: false);

        var slots = SlotsOf(live.Select(f => f.FileName));
        foreach (var carrier in Carriers)
            carrier.BackupLive(destination, slots);

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

    // --- What a set holds ---------------------------------------------------

    /// <summary>One slot's save file inside a set or the live folder, parsed.</summary>
    public sealed record SlotDescription(int Slot, GameBuild Build, string FileName, SaveFileSummary Summary)
    {
        public string BuildText => Build == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
        public string Label => $"Slot {Slot} · {BuildText}";
    }

    /// <summary>Parse every unlock file a set holds, one entry per slot per build.</summary>
    public IReadOnlyList<SlotDescription> DescribeSet(SaveSet set) =>
        DescribeFolder(SetFolder(set.Name));

    /// <summary>Parse every unlock file live right now.</summary>
    public IReadOnlyList<SlotDescription> DescribeLive()
    {
        var folder = LiveFolder;
        return folder is null ? Array.Empty<SlotDescription>() : DescribeFolder(folder);
    }

    private static IReadOnlyList<SlotDescription> DescribeFolder(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<SlotDescription>();

        var result = new List<SlotDescription>();
        foreach (var file in new DirectoryInfo(folder).GetFiles("*persistentgamedata?.dat"))
        {
            if (!IsSaveFile(file.Name)) continue;
            var slot = SlotStateCarrier.SlotOf(file.Name);
            if (slot == 0) continue;
            var build = file.Name.StartsWith(RepentogonPrefix, StringComparison.OrdinalIgnoreCase) ? GameBuild.Repentogon : GameBuild.Vanilla;
            result.Add(new SlotDescription(slot, build, file.Name, SaveFileParser.ParseFile(file.FullName)));
        }

        return result.OrderBy(d => d.Slot).ThenBy(d => d.Build).ToList();
    }

    // --- Bringing a save file in --------------------------------------------

    /// <summary>The filename the game uses for a slot on a build.</summary>
    public static string SaveFileNameFor(GameBuild build, int slot) =>
        (build == GameBuild.Repentogon ? RepentogonPrefix : VanillaPrefix) + $"persistentgamedata{slot}.dat";

    /// <summary>
    /// Put a save file from elsewhere — a fully unlocked save from speedrun.com,
    /// a friend's export, a file dug out of REPENTOGON's save_backups — into a
    /// slot of a set. Validated as a save first, and the set's previous contents
    /// are filed into history. The live folder is not touched; load the set to
    /// play it.
    /// </summary>
    public SaveSet ImportSaveFile(string setName, int slot, string sourcePath, GameBuild build)
    {
        if (slot is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(slot), "Isaac has slots 1 to 3.");
        if (build is not (GameBuild.Vanilla or GameBuild.Repentogon))
            throw new ArgumentException("Say which build the file is for: vanilla or REPENTOGON.", nameof(build));
        if (!File.Exists(sourcePath)) throw new UnsafePathException($"No such file: {sourcePath}");

        var summary = SaveFileParser.ParseFile(sourcePath);
        if (!summary.Parsed)
            throw new UnsafePathException($"{Path.GetFileName(sourcePath)} is not an Isaac save file: {summary.Problem}.");

        var set = LoadSet(setName) ?? throw new UnsafePathException($"No save set called '{setName}'.");
        if (set.Build != GameBuild.Unknown && set.Build != GameBuild.Both && set.Build != build)
            throw new UnsafePathException(
                $"'{set.Name}' is a {set.BuildText} set. A {(build == GameBuild.Repentogon ? "REPENTOGON" : "vanilla")} " +
                "file in it would be loaded on the wrong build. Make a separate set for it.");

        var folder = SetFolder(setName);
        if (_options.KeepHistory)
        {
            var filed = WriteHistory(set);
            if (filed is not null) set.ParentRevision = filed;
        }

        var fileName = SaveFileNameFor(build, slot);
        var destination = Path.Combine(folder, fileName);
        File.Copy(sourcePath, destination, overwrite: true);

        // A run file from before does not belong to this unlock file; the game
        // would reject its checksum and discard it anyway.
        var runFile = Path.Combine(folder, (build == GameBuild.Repentogon ? RepentogonPrefix : VanillaPrefix) + $"gamestate{slot}.dat");
        if (File.Exists(runFile)) File.Delete(runFile);

        if (set.Build == GameBuild.Unknown) set.Build = build;

        var files = new DirectoryInfo(folder).GetFiles().Where(f => IsSaveFile(f.Name)).ToList();
        set.Files = files.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        set.Slots = SlotsOf(set.Files).ToList();
        set.Sha1 = files.ToDictionary(f => f.Name, f => Sha1Of(f.FullName), StringComparer.OrdinalIgnoreCase);
        set.CapturedUtc = DateTime.UtcNow.ToString("o");
        Stamp(set);
        SaveSetMetadata(set);
        return set;
    }

    // --- Packs: a set as one file ---------------------------------------------

    public const string PackExtension = ".ipmsave";

    /// <summary>Write the set — files, carried state, set.json, not history — as one zip.</summary>
    public string ExportPack(string setName, string destinationPath)
    {
        var set = LoadSet(setName) ?? throw new UnsafePathException($"No save set called '{setName}'.");
        var folder = SetFolder(setName);

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        using var zip = System.IO.Compression.ZipFile.Open(destinationPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative.StartsWith(HistoryFolderName + "/", StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(file, relative);
        }

        return set.Name;
    }

    /// <summary>
    /// Read a pack into a new set. Refuses to overwrite an existing set, and
    /// only writes paths that stay inside the new folder. The set is not
    /// activated — every gate still runs when it is loaded.
    /// </summary>
    public SaveSet ImportPack(string packPath, string? newName = null)
    {
        if (!File.Exists(packPath)) throw new UnsafePathException($"No such file: {packPath}");

        using var zip = System.IO.Compression.ZipFile.OpenRead(packPath);
        var metadata = zip.GetEntry(MetadataFileName)
                       ?? throw new UnsafePathException($"{Path.GetFileName(packPath)} has no set.json, so it is not a save set pack.");

        SaveSet packed;
        using (var stream = metadata.Open())
        using (var reader = new StreamReader(stream))
        {
            packed = JsonSerializer.Deserialize<SaveSet>(reader.ReadToEnd(), SerializerOptions)
                     ?? throw new UnsafePathException("set.json in the pack is empty.");
        }
        if (packed.SchemaVersion != SaveSet.CurrentSchemaVersion)
            throw new ConfigSchemaMismatchException($"The pack's set.json has SchemaVersion {packed.SchemaVersion}; this build understands {SaveSet.CurrentSchemaVersion}.");

        var name = string.IsNullOrWhiteSpace(newName) ? packed.Name : newName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(newName));

        var folder = SetFolder(name);
        if (Directory.Exists(folder))
            throw new UnsafePathException($"A save set called '{name}' already exists. Rename it first, or import under another name.");

        Directory.CreateDirectory(folder);
        ExtractPack(zip, folder);

        packed.Name = name;
        SaveSetMetadata(packed);
        return packed;
    }

    /// <summary>
    /// Make a pack the current contents of a set that already exists — what a
    /// sync pull does. What is there now is filed into history first, so a
    /// pull is never destructive; the local history stays, the pack has none.
    /// </summary>
    public SaveSet ReplaceFromPack(string setName, string packPath)
    {
        var current = LoadSet(setName) ?? throw new UnsafePathException($"No save set called '{setName}'.");
        if (!File.Exists(packPath)) throw new UnsafePathException($"No such file: {packPath}");

        using var zip = ZipFile.OpenRead(packPath);
        var metadata = zip.GetEntry(MetadataFileName)
                       ?? throw new UnsafePathException($"{Path.GetFileName(packPath)} has no set.json, so it is not a save set pack.");

        SaveSet packed;
        using (var stream = metadata.Open())
        using (var reader = new StreamReader(stream))
        {
            packed = JsonSerializer.Deserialize<SaveSet>(reader.ReadToEnd(), SerializerOptions)
                     ?? throw new UnsafePathException("set.json in the pack is empty.");
        }
        if (packed.SchemaVersion != SaveSet.CurrentSchemaVersion)
            throw new ConfigSchemaMismatchException($"The pack's set.json has SchemaVersion {packed.SchemaVersion}; this build understands {SaveSet.CurrentSchemaVersion}.");

        var folder = SetFolder(setName);
        if (_options.KeepHistory) WriteHistory(current);
        ClearSetContents(folder);
        ExtractPack(zip, folder);

        packed.Name = setName;
        SaveSetMetadata(packed);
        return packed;
    }

    /// <summary>Write a pack's entries under a folder, refusing any that would land outside it.</summary>
    private static void ExtractPack(ZipArchive zip, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            var target = Path.GetFullPath(Path.Combine(folder, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnsafePathException($"The pack tried to write outside the set folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
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
        var resolved = ResolveLiveFolder();

        // Steam Cloud can only take a file back if the file is Steam's. When the
        // live saves are somewhere Steam does not know about — anything running
        // an emulated steam_api — the whole question is beside the point, and
        // gating on it would block a swap for a reason that cannot apply.
        var cloudApplies = resolved.Source == SaveFolderSource.SteamUserdata;

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
            CloudDisabled: !cloudApplies || cloud.SafeToSwapSaves || cloudAcknowledged,
            CloudState: cloud.State,
            RemoteDirFound: resolved.Found && Directory.Exists(resolved.Path!),
            BuildMatches: buildMatches,
            BuildProblem: buildProblem,
            CloudApplies: cloudApplies);
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

    /// <summary>Carried files (mod data, REPENTOGON state) that have changed since capture.</summary>
    public IReadOnlyList<string> DetectCarriedDrift(SaveSet set)
    {
        var drift = new List<string>();
        if (set.ModDataCaptured) drift.AddRange(ModData.DetectDrift(set.ModData, set.Slots));
        if (set.RepentogonStateCaptured) drift.AddRange(RepentogonState.DetectDrift(set.RepentogonState, set.Slots));
        return drift;
    }

    /// <summary>
    /// Back up the live saves, then copy the set over them. Refuses unless every
    /// precondition passes — the caller must not be able to force it.
    /// </summary>
    public string Activate(SaveSet set, GameBuild selectedBuild, bool cloudAcknowledged = false) =>
        ActivateSet(set, selectedBuild, cloudAcknowledged).Backup;

    /// <summary>
    /// <see cref="Activate"/>, reporting what happened to the carried state.
    ///
    /// Carried state is only touched for a set that captured it. A 1.x set
    /// never looked, and clearing live mod data on its behalf would reset every
    /// mod's settings for no reason the user could see.
    /// </summary>
    public SaveActivation ActivateSet(SaveSet set, GameBuild selectedBuild, bool cloudAcknowledged = false)
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

        var modData = set.ModDataCaptured ? ModData.Restore(source, set.Slots) : null;
        var repentogon = set.RepentogonStateCaptured ? RepentogonState.Restore(source, set.Slots) : null;

        set.LastUsedUtc = DateTime.UtcNow.ToString("o");
        SaveSetMetadata(set);
        return new SaveActivation(backup, modData, repentogon);
    }

    /// <summary>
    /// Delete one backup folder for good.
    ///
    /// The only genuinely destructive call in this service: everywhere else a
    /// "delete" is a move into here. Backups accumulate one per swap and are
    /// the last copy of whatever they hold, so this is offered rather than done
    /// automatically, one at a time, and never while Isaac is running — a
    /// backup taken seconds ago may be the only copy of the run in progress.
    /// </summary>
    public void DeleteBackup(string backupName)
    {
        if (string.IsNullOrWhiteSpace(backupName) || backupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{backupName}' is not a backup name.", nameof(backupName));
        if (_process.IsIsaacRunning())
            throw new UnsafePathException("Isaac is running. Close it before deleting a save backup.");

        var path = Path.Combine(BackupRoot, backupName);

        // Confine it to the backup folder: the name comes from a list we
        // produced, but a delete is not the place to assume that holds.
        var root = Path.GetFullPath(BackupRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException($"'{backupName}' is not inside the backup folder.");

        if (!Directory.Exists(path))
            throw new UnsafePathException($"No such backup: {path}");

        Directory.Delete(path, recursive: true);
    }

    /// <summary>How much one backup holds, for deciding whether to keep it.</summary>
    public (int Files, long Bytes) MeasureBackup(string backupName)
    {
        var path = Path.Combine(BackupRoot, backupName);
        if (!Directory.Exists(path)) return (0, 0);

        var files = new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories);
        return (files.Length, files.Sum(f => f.Length));
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

        // Only save files. A backup can be a deleted set's folder, which carries
        // set.json, and copying that in left our own bookkeeping sitting in the
        // game's save directory — observed on the reference install.
        var restored = new List<string>();
        foreach (var file in new DirectoryInfo(source).GetFiles())
        {
            if (!IsSaveFile(file.Name)) continue;
            File.Copy(file.FullName, Path.Combine(folder, file.Name), overwrite: true);
            restored.Add(file.Name);
        }

        // A 2.0 backup carries mod data and REPENTOGON state beside the saves;
        // a 1.x one does not, and its absence must leave the live copies alone.
        var slots = SlotsOf(restored);
        foreach (var carrier in Carriers)
        {
            if (carrier.ListInFolder(source).Count > 0) carrier.Restore(source, slots);
        }

        return safety;
    }
}
