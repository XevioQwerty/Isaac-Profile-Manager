using System.Text.Json.Serialization;

namespace IsaacProfileManager.Core.Models;

public enum GameBuild
{
    Unknown,
    Vanilla,
    Repentogon,
    Both,
}

/// <summary>
/// A captured set of save files.
///
/// The build is required and is enforced on activation, not warned about: save
/// structures differ between REPENTOGON's J273 and current retail, and loading
/// one on the other can destroy every achievement.
///
/// Everything added in 2.0 is additive and the schema stays at 1: a 1.x build
/// reads a 2.0 set and simply ignores the extra keys, and a 2.0 build reads a
/// 1.x set and treats the missing keys as "never captured". The set folder
/// gained two subfolders (<c>moddata\</c> and <c>repentogon\</c>) and a
/// <c>.history\</c>; a 1.x activation copies only the files in the folder root,
/// so it never sees them.
/// </summary>
public sealed class SaveSet
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Which build produced these files. Never guess this.</summary>
    [JsonPropertyName("Build")]
    public GameBuild Build { get; set; } = GameBuild.Unknown;

    /// <summary>The mod profile this save was played with.</summary>
    [JsonPropertyName("ModProfile")]
    public string ModProfile { get; set; } = string.Empty;

    /// <summary>Who you play this with — the actual reason three slots are not enough.</summary>
    [JsonPropertyName("Players")]
    public List<string> Players { get; set; } = new();

    [JsonPropertyName("Notes")]
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Free text per slot, keyed by slot number. Three slots and several groups
    /// is the whole problem — "slot 2 is the no-mods run" is the note that stops
    /// someone overwriting it.
    /// </summary>
    [JsonPropertyName("SlotNotes")]
    public Dictionary<string, string> SlotNotes { get; set; } = new();

    /// <summary>The save filenames captured, so a partial set is visible rather than silent.</summary>
    [JsonPropertyName("Files")]
    public List<string> Files { get; set; } = new();

    /// <summary>Which of slots 1-3 have data.</summary>
    [JsonPropertyName("Slots")]
    public List<int> Slots { get; set; } = new();

    /// <summary>Per-file SHA-1 at capture, so drift since then can be reported before overwriting.</summary>
    [JsonPropertyName("Sha1")]
    public Dictionary<string, string> Sha1 { get; set; } = new();

    [JsonPropertyName("CapturedUtc")]
    public string CapturedUtc { get; set; } = string.Empty;

    [JsonPropertyName("LastUsedUtc")]
    public string? LastUsedUtc { get; set; }

    // --- Added in 2.0 -------------------------------------------------------

    /// <summary>Id of the device that last captured into this set. See <c>DeviceService</c>.</summary>
    [JsonPropertyName("Device")]
    public string? Device { get; set; }

    /// <summary>
    /// Vector clock: one counter per device id, bumped by that device on every
    /// capture. Comparing two copies of a set says whether one is strictly
    /// newer or whether both machines played from the same point — a fork,
    /// which is never resolved automatically.
    /// </summary>
    [JsonPropertyName("Clock")]
    public Dictionary<string, int> Clock { get; set; } = new();

    /// <summary>
    /// The game's J-number from <c>log.txt</c> at capture, or null when the log
    /// had none. <see cref="Build"/> separates vanilla from REPENTOGON; this
    /// separates one retail version from another, which matters the moment a
    /// save crosses a machine boundary.
    /// </summary>
    [JsonPropertyName("GameVersion")]
    public string? GameVersion { get; set; }

    /// <summary>The history entry this revision was captured over, for lineage display.</summary>
    [JsonPropertyName("ParentRevision")]
    public string? ParentRevision { get; set; }

    /// <summary>
    /// Whether mod save data was looked for at capture. "Captured, and there
    /// was none" and "a 1.x set that never looked" must not be confused: only
    /// the former is allowed to clear live mod data on activation.
    /// </summary>
    [JsonPropertyName("ModDataCaptured")]
    public bool ModDataCaptured { get; set; }

    /// <summary>
    /// Mod save data carried with the set, relative path to SHA-1. The game
    /// keeps each mod's per-slot state in <c>&lt;GameDir&gt;\data\&lt;mod&gt;\save&lt;N&gt;.dat</c>,
    /// outside the save folder; a set that leaves it behind restores unlocks
    /// without the mod state that was produced alongside them.
    /// </summary>
    [JsonPropertyName("ModData")]
    public Dictionary<string, string> ModData { get; set; } = new();

    [JsonPropertyName("RepentogonStateCaptured")]
    public bool RepentogonStateCaptured { get; set; }

    /// <summary>
    /// REPENTOGON's per-slot modded achievement and completion-mark JSON,
    /// relative path to SHA-1. Lives in the Documents folder, not the save
    /// folder, so a set without it restores vanilla unlocks and blank modded ones.
    /// </summary>
    [JsonPropertyName("RepentogonState")]
    public Dictionary<string, string> RepentogonState { get; set; } = new();

    /// <summary>Anything written by another version. Preserved verbatim on write.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = new();

    public string BuildText => Build switch
    {
        GameBuild.Repentogon => "REPENTOGON",
        GameBuild.Vanilla => "vanilla",
        GameBuild.Both => "both builds",
        _ => "unknown build",
    };

    /// <summary>Everything the set carries beyond the game's own save files.</summary>
    [JsonIgnore]
    public int CarriedFileCount => ModData.Count + RepentogonState.Count;
}
