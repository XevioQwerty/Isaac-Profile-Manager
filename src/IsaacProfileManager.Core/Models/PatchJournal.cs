using System.Text.Json.Serialization;

namespace IsaacProfileManager.Core.Models;

/// <summary>What was done to one file when a patch was applied.</summary>
public enum PatchOp
{
    /// <summary>The file did not exist before. Reverting removes it.</summary>
    Added,

    /// <summary>A file was there and was overwritten. Reverting restores the backup.</summary>
    Replaced,

    /// <summary>The patch required the file to be absent. Reverting restores the backup.</summary>
    Deleted,
}

/// <summary>One file's worth of the record needed to undo an apply.</summary>
public sealed class PatchEntry
{
    [JsonPropertyName("Path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("Op")]
    public PatchOp Op { get; set; }

    /// <summary>
    /// What was at that path before, so a revert can tell "unchanged since I
    /// applied this" from "something else has written here since".
    /// </summary>
    [JsonPropertyName("Sha1Before")]
    public string? Sha1Before { get; set; }

    /// <summary>What this patch left there. Null for a delete.</summary>
    [JsonPropertyName("Sha1After")]
    public string? Sha1After { get; set; }

    /// <summary>Where the displaced original was kept. Null for an add.</summary>
    [JsonPropertyName("Backup")]
    public string? Backup { get; set; }
}

/// <summary>
/// The record of one applied patch, and the only thing that makes reverting
/// safe rather than a guess.
///
/// Written incrementally as the apply proceeds: a patch interrupted halfway
/// through leaves a journal describing exactly the files it had got to, so it
/// can still be reverted cleanly. A journal that exists at all means the patch
/// is applied, wholly or partly.
/// </summary>
public sealed class PatchJournal
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("Patch")]
    public string Patch { get; set; } = string.Empty;

    [JsonPropertyName("Target")]
    public PatchTarget Target { get; set; }

    /// <summary>The absolute folder it was laid over, so a moved game dir is visible.</summary>
    [JsonPropertyName("TargetPath")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("AppliedUtc")]
    public string AppliedUtc { get; set; } = string.Empty;

    /// <summary>False while an apply is in flight, true once every file is done.</summary>
    [JsonPropertyName("Complete")]
    public bool Complete { get; set; }

    [JsonPropertyName("Entries")]
    public List<PatchEntry> Entries { get; set; } = new();
}

/// <summary>A file the patch system declined to touch, and why.</summary>
public sealed record PatchSkip(string Path, string Reason);

/// <summary>Outcome of applying a patch.</summary>
public sealed record PatchApplyResult(
    string Patch,
    int Added,
    int Replaced,
    int Deleted,
    IReadOnlyList<PatchSkip> Skipped)
{
    public int Changed => Added + Replaced + Deleted;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added > 0) parts.Add($"{Added} added");
            if (Replaced > 0) parts.Add($"{Replaced} replaced");
            if (Deleted > 0) parts.Add($"{Deleted} removed");
            if (parts.Count == 0) parts.Add("nothing to do");
            var skipped = Skipped.Count > 0 ? $", {Skipped.Count} skipped" : "";
            return $"'{Patch}': {string.Join(", ", parts)}{skipped}.";
        }
    }
}

/// <summary>Outcome of reverting a patch.</summary>
public sealed record PatchRevertResult(
    string Patch,
    int Removed,
    int Restored,
    IReadOnlyList<PatchSkip> Skipped)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Removed > 0) parts.Add($"{Removed} removed");
            if (Restored > 0) parts.Add($"{Restored} restored");
            if (parts.Count == 0) parts.Add("nothing to undo");
            var skipped = Skipped.Count > 0 ? $", {Skipped.Count} left alone" : "";
            return $"'{Patch}': {string.Join(", ", parts)}{skipped}.";
        }
    }
}

/// <summary>
/// A file an applied patch no longer matches — something wrote over it after the
/// apply, most often a Steam update.
/// </summary>
public sealed record PatchDrift(string Path, string Expected, string Actual);
