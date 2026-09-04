using System.Text.RegularExpressions;

namespace IsaacProfileManager.Core.Services;

/// <summary>A per-slot file that lives outside the save folder and travels with a save set.</summary>
public sealed record CarriedFile(string RelativePath, string LivePath, int Slot, long Length);

/// <summary>What a restore did, so the caller can say so.</summary>
public sealed record CarryReport(int Removed, int Restored, bool Skipped, string? Reason = null);

/// <summary>
/// State the game keeps per save slot somewhere other than the save folder.
///
/// There are two such places on a real install (verified 2026-09-03 on the
/// reference machine): each mod's own save data in
/// <c>&lt;GameDir&gt;\data\&lt;mod&gt;\save&lt;N&gt;.dat</c>, and REPENTOGON's
/// modded achievement and completion-mark JSON under
/// <c>Documents\My Games\Binding of Isaac Repentance+\Repentogon\</c>. Both are
/// keyed by slot number, both are produced alongside the unlock state in
/// <c>persistentgamedata&lt;N&gt;.dat</c>, and neither used to travel with a set —
/// so a restored set had the right unlocks and the wrong mod state.
///
/// A carrier captures those files into a subfolder of the set, backs the live
/// ones up beside the save backup, and puts the set's copies back on
/// activation. Each is a plain file copy; nothing here is junctioned.
/// </summary>
public abstract class SlotStateCarrier
{
    private static readonly Regex SlotSuffix = new(@"(\d)\.(dat|json)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The subfolder of a save set (and of a backup) this carrier owns.</summary>
    public abstract string SetSubfolder { get; }

    /// <summary>What these files are, for messages.</summary>
    public abstract string Description { get; }

    /// <summary>Whether the live location exists on this machine at all.</summary>
    public abstract bool Available { get; }

    /// <summary>Where the live copy of a carried file belongs, or null when the path is not one this carrier produces.</summary>
    protected abstract string? LivePathFor(string relativePath);

    /// <summary>Every live file for these slots.</summary>
    protected abstract IEnumerable<CarriedFile> EnumerateLive(IReadOnlySet<int> slots);

    /// <summary>The slot a file name ends in (<c>save2.dat</c>, <c>achievements1.json</c>), or 0.</summary>
    public static int SlotOf(string fileName)
    {
        var match = SlotSuffix.Match(fileName);
        return match.Success ? match.Groups[1].Value[0] - '0' : 0;
    }

    public IReadOnlyList<CarriedFile> ReadLive(IEnumerable<int> slots)
    {
        if (!Available) return Array.Empty<CarriedFile>();
        var wanted = slots.ToHashSet();
        if (wanted.Count == 0) return Array.Empty<CarriedFile>();

        return EnumerateLive(wanted)
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Relative paths of the files a set (or backup) folder holds for this carrier.</summary>
    public IReadOnlyList<string> ListInFolder(string setFolder)
    {
        var root = Path.Combine(setFolder, SetSubfolder);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => ToRelative(setFolder, f))
            .Where(rel => LivePathFor(rel) is not null)
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Copy the live files for these slots into the set folder, replacing
    /// whatever the subfolder held. Returns relative path to SHA-1.
    /// </summary>
    public Dictionary<string, string> CaptureInto(string setFolder, IEnumerable<int> slots)
    {
        var target = Path.Combine(setFolder, SetSubfolder);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ReadLive(slots))
        {
            var destination = Path.Combine(setFolder, FromRelative(file.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.LivePath, destination, overwrite: true);
            hashes[file.RelativePath] = SaveSetService.Sha1Of(destination);
        }

        return hashes;
    }

    /// <summary>Copy the live files for these slots under a backup folder. Returns how many.</summary>
    public int BackupLive(string backupFolder, IEnumerable<int> slots)
    {
        var count = 0;
        foreach (var file in ReadLive(slots))
        {
            var destination = Path.Combine(backupFolder, FromRelative(file.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.LivePath, destination, overwrite: false);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Replace the live files for these slots with the folder's copies. The
    /// caller has already backed the live ones up. Live files for a slot the
    /// folder does not cover are left alone; live files for a covered slot that
    /// the folder lacks are removed, because that slot's unlock state is being
    /// replaced wholesale and state written after the capture would otherwise
    /// outlive it.
    /// </summary>
    public CarryReport Restore(string setFolder, IEnumerable<int> slots)
    {
        if (!Available)
            return new CarryReport(0, 0, Skipped: true, $"{Description} folder not found on this machine");

        var wanted = slots.ToHashSet();
        var removed = 0;
        foreach (var live in ReadLive(wanted))
        {
            File.Delete(live.LivePath);
            removed++;
        }

        var restored = 0;
        foreach (var relative in ListInFolder(setFolder))
        {
            var slot = SlotOf(Path.GetFileName(relative));
            if (!wanted.Contains(slot)) continue;

            var livePath = LivePathFor(relative)!;
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            File.Copy(Path.Combine(setFolder, FromRelative(relative)), livePath, overwrite: true);
            restored++;
        }

        return new CarryReport(removed, restored, Skipped: false);
    }

    /// <summary>Live files whose hash differs from what the set recorded, plus any the set has that are gone.</summary>
    public IReadOnlyList<string> DetectDrift(IReadOnlyDictionary<string, string> recorded, IEnumerable<int> slots)
    {
        var live = ReadLive(slots).ToDictionary(f => f.RelativePath, f => f.LivePath, StringComparer.OrdinalIgnoreCase);
        var drift = new List<string>();

        foreach (var (relative, sha) in recorded)
        {
            if (!live.TryGetValue(relative, out var path)) { drift.Add(relative); continue; }
            if (!string.Equals(SaveSetService.Sha1Of(path), sha, StringComparison.OrdinalIgnoreCase)) drift.Add(relative);
        }

        return drift.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    protected string ToRelative(string setFolder, string fullPath) =>
        Path.GetRelativePath(setFolder, fullPath).Replace('\\', '/');

    private static string FromRelative(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>A relative path is only ever used to build a path under a folder we own.</summary>
    protected bool IsUnderSubfolder(string relativePath, out string[] segments)
    {
        segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
               segments[0].Equals(SetSubfolder, StringComparison.OrdinalIgnoreCase) &&
               segments.All(s => s != "." && s != "..");
    }
}

/// <summary>
/// Each mod's per-slot save data: <c>&lt;GameDir&gt;\data\&lt;mod folder name&gt;\save&lt;N&gt;.dat</c>.
///
/// The folder is named after the mod's folder under <c>mods\</c>, which for a
/// library-built profile is the suffix-free library name — the reference
/// install's <c>data\</c> matched its library names exactly. Renaming a mod
/// therefore strands its data, which is one more reason import keeps the
/// sender's entry names.
/// </summary>
public sealed class ModDataCarrier : SlotStateCarrier
{
    public const string GameSubfolder = "data";
    public const string Subfolder = "moddata";

    private readonly string? _root;

    /// <param name="dataRoot">The game's <c>data\</c> folder. Null disables the carrier.</param>
    public ModDataCarrier(string? dataRoot) => _root = dataRoot;

    public static ModDataCarrier ForGameDir(string? gameDir) =>
        new(string.IsNullOrWhiteSpace(gameDir) ? null : Path.Combine(gameDir, GameSubfolder));

    public string? Root => _root;

    public override string SetSubfolder => Subfolder;
    public override string Description => "mod save data";
    public override bool Available => _root is not null && Directory.Exists(_root);

    protected override string? LivePathFor(string relativePath)
    {
        if (_root is null || !IsUnderSubfolder(relativePath, out var segments) || segments.Length != 3) return null;
        if (SlotOf(segments[2]) == 0 || !segments[2].StartsWith("save", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(_root, segments[1], segments[2]);
    }

    protected override IEnumerable<CarriedFile> EnumerateLive(IReadOnlySet<int> slots)
    {
        foreach (var modDir in new DirectoryInfo(_root!).EnumerateDirectories())
        {
            // A mod's data folder is a real folder the game made. Never follow a link here.
            if ((modDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            foreach (var slot in slots)
            {
                var file = new FileInfo(Path.Combine(modDir.FullName, $"save{slot}.dat"));
                if (!file.Exists) continue;
                yield return new CarriedFile($"{Subfolder}/{modDir.Name}/{file.Name}", file.FullName, slot, file.Length);
            }
        }
    }
}

/// <summary>
/// REPENTOGON's per-slot modded achievements and completion marks:
/// <c>achievements&lt;N&gt;.json</c> and <c>completionmarks&lt;N&gt;.json</c> in its
/// settings folder under Documents. Keyed by Workshop id inside, so they are
/// only meaningful beside the mods that defined them — which is what pairing a
/// save set with a mod profile is for.
/// </summary>
public sealed class RepentogonStateCarrier : SlotStateCarrier
{
    public const string Subfolder = "repentogon";
    private static readonly string[] Kinds = { "achievements", "completionmarks" };

    private readonly string? _folder;

    /// <param name="stateFolder">REPENTOGON's settings folder. Null disables the carrier.</param>
    public RepentogonStateCarrier(string? stateFolder) => _folder = stateFolder;

    public static string DefaultStateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Binding of Isaac Repentance+", "Repentogon");

    public string? Folder => _folder;

    public override string SetSubfolder => Subfolder;
    public override string Description => "REPENTOGON achievement state";
    public override bool Available => _folder is not null && Directory.Exists(_folder);

    protected override string? LivePathFor(string relativePath)
    {
        if (_folder is null || !IsUnderSubfolder(relativePath, out var segments) || segments.Length != 2) return null;
        var name = segments[1];
        var slot = SlotOf(name);
        if (slot == 0) return null;
        var stem = name[..^"N.json".Length];
        return Kinds.Contains(stem, StringComparer.OrdinalIgnoreCase) ? Path.Combine(_folder, name) : null;
    }

    protected override IEnumerable<CarriedFile> EnumerateLive(IReadOnlySet<int> slots)
    {
        foreach (var kind in Kinds)
        {
            foreach (var slot in slots)
            {
                var file = new FileInfo(Path.Combine(_folder!, $"{kind}{slot}.json"));
                if (!file.Exists) continue;
                yield return new CarriedFile($"{Subfolder}/{file.Name}", file.FullName, slot, file.Length);
            }
        }
    }
}
