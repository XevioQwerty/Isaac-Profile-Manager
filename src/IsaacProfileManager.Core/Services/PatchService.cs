using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Lays unzipped releases over the game folders, and takes them off again.
///
/// This replaces swapping whole build folders by junction. A complete build is
/// 1.1 GB on the reference install and only ever covered the REPENTOGON
/// subfolder — the retail root keeps its OnlineFix files loose beside
/// <c>mods\</c>, where there is no folder to re-point. An overlay covers both
/// with one mechanism and holds no second copy of the game.
///
/// Everything here writes into the game directory, which nothing else in this
/// app does. Three things make that recoverable and none of them are optional:
///
/// <list type="bullet">
/// <item>every displaced file is copied to a backup <em>before</em> it is touched;</item>
/// <item>a journal records each file's operation and its hash before and after,
///       so a revert restores rather than guesses;</item>
/// <item>the journal is written as the apply proceeds, so a run interrupted
///       halfway still describes exactly what it managed to do.</item>
/// </list>
/// </summary>
public sealed class PatchService
{
    public const string PatchesFolderName = ".patches";
    public const string AppliedFolderName = ".applied";
    public const string BackupFolderName = ".backup";

    /// <summary>
    /// Folders inside the game directory that belong to other subsystems. A
    /// patch reaching into <c>mods\</c> would land in whichever profile is
    /// junctioned there and be invisible to the profile's manifest.
    /// </summary>
    private static readonly string[] ForbiddenRoots = { "mods", "remote", "userdata" };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGameProcessService _process;
    private readonly string _syncRoot;

    public PatchService(IGameProcessService process, string syncRoot)
    {
        _process = process;
        _syncRoot = syncRoot;
    }

    public string PatchesRoot => Path.Combine(_syncRoot, PatchesFolderName);
    public string AppliedRoot => Path.Combine(PatchesRoot, AppliedFolderName);
    public string BackupRoot => Path.Combine(PatchesRoot, BackupFolderName);

    public static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    // --- Reading what is there ---------------------------------------------

    /// <summary>Patch folders, ignoring the bookkeeping folders beside them.</summary>
    public IReadOnlyList<string> ListPatches()
    {
        if (!Directory.Exists(PatchesRoot)) return Array.Empty<string>();

        return Directory.GetDirectories(PatchesRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith('.'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public PatchManifest LoadManifest(string patch)
    {
        var path = Path.Combine(PatchesRoot, patch, PatchManifest.FileName);
        if (!File.Exists(path)) return new PatchManifest { Name = patch };

        try
        {
            var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(path), SerializerOptions)
                           ?? new PatchManifest { Name = patch };

            if (manifest.SchemaVersion != PatchManifest.CurrentSchemaVersion)
                throw new ConfigSchemaMismatchException(
                    $"{path} has SchemaVersion {manifest.SchemaVersion}; this build understands {PatchManifest.CurrentSchemaVersion}.");

            if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = patch;
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new ConfigSchemaMismatchException($"{path} is not readable as JSON: {ex.Message}");
        }
    }

    public void SaveManifest(string patch, PatchManifest manifest)
    {
        var dir = Path.Combine(PatchesRoot, patch);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PatchManifest.FileName),
                          JsonSerializer.Serialize(manifest, SerializerOptions), new UTF8Encoding(false));
    }

    public PatchInfo Describe(string patch)
    {
        var dir = Path.Combine(PatchesRoot, patch);
        var manifest = LoadManifest(patch);
        var journal = LoadJournal(patch);

        var files = PayloadFiles(dir).ToList();
        var size = files.Sum(f => new FileInfo(f).Length);

        return new PatchInfo(
            Name: manifest.Name,
            Path: dir,
            Target: manifest.Target,
            Description: manifest.Description,
            FileCount: files.Count,
            SizeBytes: size,
            Deletes: manifest.Delete,
            IsApplied: journal is not null,
            AppliedUtc: journal?.AppliedUtc);
    }

    public IReadOnlyList<PatchInfo> DescribeAll() => ListPatches().Select(Describe).ToList();

    /// <summary>Files that make up the payload — everything but the manifest itself.</summary>
    private static IEnumerable<string> PayloadFiles(string patchDir)
    {
        if (!Directory.Exists(patchDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(patchDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals(PatchManifest.FileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(file), patchDir, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return file;
        }
    }

    // --- Journals ----------------------------------------------------------

    private string JournalPath(string patch) => Path.Combine(AppliedRoot, patch + ".json");

    public PatchJournal? LoadJournal(string patch)
    {
        var path = JournalPath(patch);
        if (!File.Exists(path)) return null;

        try
        {
            var journal = JsonSerializer.Deserialize<PatchJournal>(File.ReadAllText(path), SerializerOptions);
            if (journal is null) return null;

            if (journal.SchemaVersion != PatchJournal.CurrentSchemaVersion)
                throw new ConfigSchemaMismatchException(
                    $"{path} has SchemaVersion {journal.SchemaVersion}; this build understands {PatchJournal.CurrentSchemaVersion}.");

            return journal;
        }
        catch (JsonException ex)
        {
            // Refusing beats reverting from a journal we cannot read.
            throw new ConfigSchemaMismatchException($"{path} is not readable as JSON: {ex.Message}");
        }
    }

    private void SaveJournal(PatchJournal journal)
    {
        Directory.CreateDirectory(AppliedRoot);
        File.WriteAllText(JournalPath(journal.Patch),
                          JsonSerializer.Serialize(journal, SerializerOptions), new UTF8Encoding(false));
    }

    public bool IsApplied(string patch) => File.Exists(JournalPath(patch));

    // --- Safety ------------------------------------------------------------

    /// <summary>
    /// Resolve a payload path against the target, refusing anything that escapes
    /// it or reaches into a folder another subsystem owns.
    /// </summary>
    private static string? SafeDestination(string targetDir, string relative, out string? refusal)
    {
        refusal = null;

        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
                                 .TrimStart(Path.DirectorySeparatorChar);

        if (normalized.Length == 0)
        {
            refusal = "empty path";
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(targetDir, normalized));
        var root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "would write outside the target folder";
            return null;
        }

        var firstSegment = normalized.Split(Path.DirectorySeparatorChar)[0];
        if (ForbiddenRoots.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
        {
            refusal = $"'{firstSegment}\\' belongs to another part of the app and is never patched";
            return null;
        }

        return full;
    }

    private void RequireIsaacClosed()
    {
        if (_process.IsIsaacRunning())
            throw new UnsafePathException("Isaac is running. Close it before changing files in the game folder.");
    }

    /// <summary>
    /// Files another applied patch already claims. Two patches over the same
    /// file would make the second one's backup a copy of the first one's work,
    /// so reverting in the wrong order would restore the wrong bytes.
    /// </summary>
    public IReadOnlyList<string> FindConflicts(string patch, string targetDir)
    {
        var wanted = PlannedPaths(patch, targetDir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return Array.Empty<string>();

        var conflicts = new List<string>();
        foreach (var other in ListPatches())
        {
            if (string.Equals(other, patch, StringComparison.OrdinalIgnoreCase)) continue;

            var journal = LoadJournal(other);
            if (journal is null) continue;

            foreach (var entry in journal.Entries)
            {
                var full = Path.GetFullPath(Path.Combine(journal.TargetPath, entry.Path));
                if (wanted.Contains(full)) conflicts.Add($"{entry.Path} (held by '{other}')");
            }
        }
        return conflicts;
    }

    private IEnumerable<string> PlannedPaths(string patch, string targetDir)
    {
        var dir = Path.Combine(PatchesRoot, patch);
        var manifest = LoadManifest(patch);

        foreach (var file in PayloadFiles(dir))
        {
            var relative = Path.GetRelativePath(dir, file);
            var destination = SafeDestination(targetDir, relative, out _);
            if (destination is not null) yield return destination;
        }

        foreach (var delete in manifest.Delete)
        {
            var destination = SafeDestination(targetDir, delete, out _);
            if (destination is not null) yield return destination;
        }
    }

    // --- Applying ----------------------------------------------------------

    /// <summary>
    /// Lay a patch over <paramref name="targetDir"/>.
    ///
    /// Order matters: for each file the original is hashed and copied aside
    /// before anything is written, and the journal is saved after every file. A
    /// crash between two files leaves a journal describing the first and a
    /// revert that undoes exactly it.
    /// </summary>
    public PatchApplyResult Apply(string patch, string targetDir, IProgress<string>? progress = null)
    {
        RequireIsaacClosed();

        var dir = Path.Combine(PatchesRoot, patch);
        if (!Directory.Exists(dir))
            throw new UnsafePathException($"No such patch: {dir}");
        if (!Directory.Exists(targetDir))
            throw new UnsafePathException($"Target folder does not exist: {targetDir}");
        if (IsApplied(patch))
            throw new UnsafePathException($"'{patch}' is already applied. Revert it first.");

        var conflicts = FindConflicts(patch, targetDir);
        if (conflicts.Count > 0)
            throw new UnsafePathException(
                $"'{patch}' touches files another applied patch owns:\n\n" +
                string.Join("\n", conflicts.Select(c => "  " + c)) +
                "\n\nRevert that one first.");

        var manifest = LoadManifest(patch);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(BackupRoot, $"{patch}-{stamp}");

        var journal = new PatchJournal
        {
            Patch = patch,
            Target = manifest.Target,
            TargetPath = Path.GetFullPath(targetDir),
            AppliedUtc = DateTime.UtcNow.ToString("o"),
            Complete = false,
        };

        var skipped = new List<PatchSkip>();
        int added = 0, replaced = 0, deleted = 0;

        // Written before the first file, so an interrupted run is still known
        // to have started and can be reverted.
        SaveJournal(journal);

        foreach (var source in PayloadFiles(dir))
        {
            var relative = Path.GetRelativePath(dir, source);
            var destination = SafeDestination(targetDir, relative, out var refusal);
            if (destination is null)
            {
                skipped.Add(new PatchSkip(relative, refusal ?? "refused"));
                continue;
            }

            progress?.Report(relative);

            var entry = new PatchEntry { Path = relative };

            if (File.Exists(destination))
            {
                entry.Op = PatchOp.Replaced;
                entry.Sha1Before = Sha1Of(destination);
                entry.Backup = BackUp(destination, backupDir, relative);
                replaced++;
            }
            else
            {
                entry.Op = PatchOp.Added;
                added++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            ClearReadOnly(destination);
            entry.Sha1After = Sha1Of(destination);

            journal.Entries.Add(entry);
            SaveJournal(journal);
        }

        foreach (var wanted in manifest.Delete)
        {
            var destination = SafeDestination(targetDir, wanted, out var refusal);
            if (destination is null)
            {
                skipped.Add(new PatchSkip(wanted, refusal ?? "refused"));
                continue;
            }
            if (!File.Exists(destination)) continue;

            progress?.Report($"removing {wanted}");

            var entry = new PatchEntry
            {
                Path = Path.GetRelativePath(Path.GetFullPath(targetDir), destination),
                Op = PatchOp.Deleted,
                Sha1Before = Sha1Of(destination),
            };
            entry.Backup = BackUp(destination, backupDir, entry.Path);

            ClearReadOnly(destination);
            File.Delete(destination);
            deleted++;

            journal.Entries.Add(entry);
            SaveJournal(journal);
        }

        journal.Complete = true;
        SaveJournal(journal);

        return new PatchApplyResult(patch, added, replaced, deleted, skipped);
    }

    private static string BackUp(string file, string backupDir, string relative)
    {
        var destination = Path.Combine(backupDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
        return destination;
    }

    private static void ClearReadOnly(string file)
    {
        var attributes = File.GetAttributes(file);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
    }

    // --- Drift -------------------------------------------------------------

    /// <summary>
    /// Files that no longer hold what the patch left there. A Steam update over
    /// an applied patch is the ordinary cause, and it means a plain revert would
    /// put an old file over a newer one.
    /// </summary>
    public IReadOnlyList<PatchDrift> DetectDrift(string patch)
    {
        var journal = LoadJournal(patch);
        if (journal is null) return Array.Empty<PatchDrift>();

        var drift = new List<PatchDrift>();
        foreach (var entry in journal.Entries)
        {
            if (entry.Op == PatchOp.Deleted) continue;

            var path = Path.Combine(journal.TargetPath, entry.Path);
            if (!File.Exists(path))
            {
                drift.Add(new PatchDrift(entry.Path, entry.Sha1After ?? "", "missing"));
                continue;
            }

            var actual = Sha1Of(path);
            if (entry.Sha1After is not null && actual != entry.Sha1After)
                drift.Add(new PatchDrift(entry.Path, entry.Sha1After, actual));
        }
        return drift;
    }

    // --- Reverting ---------------------------------------------------------

    /// <summary>
    /// Undo an applied patch: added files removed, replaced and deleted files
    /// restored from their backups.
    ///
    /// A file that has changed since the apply is left alone unless
    /// <paramref name="force"/>, because overwriting it would discard whatever
    /// wrote it — usually a Steam update, i.e. the newer copy. Backups are never
    /// deleted here; a revert that turns out to be wrong is still recoverable.
    /// </summary>
    public PatchRevertResult Revert(string patch, bool force = false, IProgress<string>? progress = null)
    {
        RequireIsaacClosed();

        var journal = LoadJournal(patch)
                      ?? throw new UnsafePathException($"'{patch}' is not applied — there is nothing to undo.");

        var skipped = new List<PatchSkip>();
        int removed = 0, restored = 0;

        // Reverse order, so a patch that both replaced a file and deleted
        // another undoes in the opposite order to the way it was done.
        foreach (var entry in Enumerable.Reverse(journal.Entries))
        {
            var path = Path.Combine(journal.TargetPath, entry.Path);
            progress?.Report(entry.Path);

            if (entry.Op == PatchOp.Added)
            {
                if (!File.Exists(path)) continue;

                if (!force && entry.Sha1After is not null && Sha1Of(path) != entry.Sha1After)
                {
                    skipped.Add(new PatchSkip(entry.Path, "changed since the patch was applied"));
                    continue;
                }

                ClearReadOnly(path);
                File.Delete(path);
                removed++;
                continue;
            }

            // Replaced or Deleted: both restore the backup taken at apply time.
            if (entry.Backup is null || !File.Exists(entry.Backup))
            {
                skipped.Add(new PatchSkip(entry.Path, "its backup is missing"));
                continue;
            }

            if (!force && entry.Op == PatchOp.Replaced && File.Exists(path) &&
                entry.Sha1After is not null && Sha1Of(path) != entry.Sha1After)
            {
                skipped.Add(new PatchSkip(entry.Path, "changed since the patch was applied"));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) ClearReadOnly(path);
            File.Copy(entry.Backup, path, overwrite: true);
            restored++;
        }

        // Anything skipped is still patched, so the journal has to stay or the
        // record of those files would be lost.
        if (skipped.Count == 0) File.Delete(JournalPath(patch));

        return new PatchRevertResult(patch, removed, restored, skipped);
    }

    // --- Installing a new patch --------------------------------------------

    /// <summary>
    /// Take an unzipped folder and register it as a patch. A copy, so the source
    /// the user unzipped stays where it is and can be thrown away.
    /// </summary>
    public PatchInfo Install(string sourceDir, string name, PatchTarget target,
                             string description = "", IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not usable as a folder name.", nameof(name));
        if (!Directory.Exists(sourceDir))
            throw new UnsafePathException($"No such folder: {sourceDir}");

        var destination = Path.Combine(PatchesRoot, name);
        if (Directory.Exists(destination))
            throw new UnsafePathException($"A patch called '{name}' already exists.");

        Directory.CreateDirectory(PatchesRoot);
        DirectoryCopier.Copy(sourceDir, destination, overwrite: false, progress);

        // A manifest that came with the folder wins on everything but the
        // target, which is a local decision about where it is being laid.
        var manifest = LoadManifest(name);
        manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name == name ? name : manifest.Name;
        manifest.Target = target;
        if (description.Length > 0) manifest.Description = description;
        SaveManifest(name, manifest);

        return Describe(name);
    }

    /// <summary>Forget a patch. Refuses while it is applied, so its journal cannot be orphaned.</summary>
    public void Remove(string patch)
    {
        if (IsApplied(patch))
            throw new UnsafePathException($"'{patch}' is still applied. Revert it before removing it.");

        var dir = Path.Combine(PatchesRoot, patch);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
