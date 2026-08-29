using System.Text.Json.Serialization;

namespace IsaacProfileManager.Core.Models;

/// <summary>Which folder a patch is laid over.</summary>
public enum PatchTarget
{
    /// <summary>The retail install: the folder holding isaac-ng.exe and mods\.</summary>
    GameRoot,

    /// <summary>The downgraded build the REPENTOGON launcher loads.</summary>
    Repentogon,
}

/// <summary>
/// An unzipped release to be laid over a target folder.
///
/// The folder mirrors the target's structure, so installing one is "unzip it
/// here" and nothing has to be described. <see cref="Delete"/> covers the case a
/// folder cannot express: a fix that requires a file to be absent.
/// </summary>
public sealed class PatchManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "patch.json";

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Shown in the UI. Defaults to the folder name when absent.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Which folder this is laid over.</summary>
    [JsonPropertyName("Target")]
    public PatchTarget Target { get; set; } = PatchTarget.GameRoot;

    /// <summary>
    /// Paths relative to the target that must not exist while this is applied.
    /// Each one is backed up before removal and restored on revert.
    /// </summary>
    [JsonPropertyName("Delete")]
    public List<string> Delete { get; set; } = new();
}

/// <summary>A patch on disk, with whatever the folder and manifest say about it.</summary>
public sealed record PatchInfo(
    string Name,
    string Path,
    PatchTarget Target,
    string Description,
    int FileCount,
    long SizeBytes,
    IReadOnlyList<string> Deletes,
    bool IsApplied,
    string? AppliedUtc)
{
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);

    public string TargetText => Target == PatchTarget.GameRoot ? "game root" : "REPENTOGON folder";

    public string SummaryText =>
        $"{FileCount} file(s), {SizeMb:N1} MB" + (Deletes.Count > 0 ? $", removes {Deletes.Count}" : "");
}
