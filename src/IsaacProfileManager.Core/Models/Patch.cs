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

    /// <summary>
    /// Which folder this is normally laid over. A suggestion, not a
    /// restriction: the same fix often has to go over the retail install and
    /// the REPENTOGON build both, and they are applied and reverted separately.
    /// </summary>
    [JsonPropertyName("Target")]
    public PatchTarget Target { get; set; } = PatchTarget.GameRoot;

    /// <summary>
    /// Paths relative to the target that must not exist while this is applied.
    /// Each one is backed up before removal and restored on revert.
    /// </summary>
    [JsonPropertyName("Delete")]
    public List<string> Delete { get; set; } = new();

    /// <summary>
    /// Files this patch installs that carry state once the game has run, and so
    /// must be left exactly as they are found.
    ///
    /// Two things follow, and both matter. A change to one is never reported as
    /// drift — otherwise a config rewritten on every launch looks identical to a
    /// game update landing on the patch, the revert leaves it behind, and a file
    /// left behind means the patch is still partly applied, so it can never be
    /// fully taken off. And the file is neither restored nor removed by a
    /// revert, nor overwritten by a later apply.
    ///
    /// That second part is the whole point for <c>OnlineFix.ini</c>: the dll
    /// writes a hash into it on first launch, and putting the pristine copy back
    /// makes it first launch again, which sends you to a web page.
    ///
    /// Entries are relative paths, or <c>*.ext</c> to match by extension.
    /// </summary>
    [JsonPropertyName("Volatile")]
    public List<string> Volatile { get; set; } = new();

    /// <summary>
    /// Always left alone, without a patch having to say so.
    ///
    /// Logs are written by whatever produced them. Settings files are the same
    /// case one step further on: an ini a patch installs is a default, and once
    /// the game has run it holds that install's state, so putting the shipped
    /// copy back over it destroys something. OnlineFix.ini keeps a hash there
    /// and reverting to the pristine copy makes the dll treat the next start as
    /// a first launch, which opens a web page.
    ///
    /// On a first apply the file is absent and the default is laid down as
    /// normal; only an existing one is left as it is.
    /// </summary>
    public static readonly string[] AlwaysVolatile = { "*.log", "*.ini" };

    /// <summary>
    /// Extensions that may be <em>learned</em> as volatile from a drift prompt.
    ///
    /// Deliberately narrow. "Remember these as expected to change" is offered
    /// after a revert finds drift, and the drifted set can easily include
    /// binaries — another tool removing this patch's dlls looks identical to a
    /// config being rewritten. Recording a dll as expected to change would
    /// quietly disable the protection that stops a revert clobbering a newer
    /// file, so only things that are plainly settings can be learned.
    /// </summary>
    public static readonly string[] LearnableVolatileExtensions =
        { ".ini", ".cfg", ".conf", ".json", ".txt", ".log", ".xml", ".yml" };

    public static bool CanLearnAsVolatile(string relativePath) =>
        LearnableVolatileExtensions.Contains(System.IO.Path.GetExtension(relativePath),
                                             StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a change to this path should be ignored rather than reported.</summary>
    public bool IsVolatile(string relativePath)
    {
        foreach (var pattern in Volatile.Concat(AlwaysVolatile))
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (relativePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(relativePath, pattern, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(System.IO.Path.GetFileName(relativePath), pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Where one patch stands against one of the two folders it can go over.</summary>
public sealed record PatchTargetState(PatchTarget Target, bool IsApplied, string? AppliedUtc, int DriftCount)
{
    public string TargetText => Target == PatchTarget.GameRoot ? "retail install" : "REPENTOGON build";

    public string ShortText => Target == PatchTarget.GameRoot ? "Retail" : "REPENTOGON";
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
    IReadOnlyList<PatchTargetState> States)
{
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);

    /// <summary>
    /// What to call this on screen.
    ///
    /// A patch folder is named by whoever packaged the release, so it arrives as
    /// something like <c>TBoIR_Fix_Repair_Steam_V2_Generic</c>. Shown verbatim
    /// that is unreadable and dominates any row it appears in, so separators
    /// become spaces and a name set by hand wins outright.
    /// </summary>
    public string DisplayName => Prettify(Name);

    /// <summary>Trimmed for places with no room, like the launch bar.</summary>
    public string ShortName => Shorten(DisplayName, 22);

    internal static string Prettify(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var spaced = raw.Replace('_', ' ').Replace('-', ' ');
        while (spaced.Contains("  ")) spaced = spaced.Replace("  ", " ");
        return spaced.Trim();
    }

    internal static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "\u2026";

    public string TargetText => Target == PatchTarget.GameRoot ? "retail install" : "REPENTOGON build";

    /// <summary>True when it is laid over at least one of the two folders.</summary>
    public bool IsAppliedAnywhere => States.Any(t => t.IsApplied);

    public string SummaryText =>
        $"{FileCount} file(s), {SizeMb:N1} MB" + (Deletes.Count > 0 ? $", removes {Deletes.Count}" : "");
}
