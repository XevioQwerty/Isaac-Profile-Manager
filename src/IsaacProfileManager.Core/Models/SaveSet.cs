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

    public string BuildText => Build switch
    {
        GameBuild.Repentogon => "REPENTOGON",
        GameBuild.Vanilla => "vanilla",
        GameBuild.Both => "both builds",
        _ => "unknown build",
    };
}
