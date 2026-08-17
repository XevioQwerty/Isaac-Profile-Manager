namespace IsaacProfileManager.Core.Services;

public enum BackupKind
{
    /// <summary>A copy. The original still exists elsewhere, so deleting this loses nothing.</summary>
    Copy,

    /// <summary>
    /// Something that was moved here instead of being deleted. This may be the
    /// only remaining instance, so it must never be pruned automatically.
    /// </summary>
    MovedOriginal,
}

public sealed record BackupEntry(string Path, string Name, BackupKind Kind, DateTime When, long SizeBytes, int FileCount)
{
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);
    public bool IsSafeToPrune => Kind == BackupKind.Copy;

    public string KindText => Kind == BackupKind.Copy
        ? "copy"
        : "moved here — may be the only copy";
}

/// <summary>
/// Lists and prunes the backups this tool takes.
///
/// The important distinction is that not every backup is a copy. Removing a mod
/// from the library, replacing a profile folder with links, and deleting a save
/// set all **move** the original here rather than deleting it — so those folders
/// can be the only remaining instance and are never pruned automatically.
/// Save backups are true copies and are the only thing retention touches.
/// </summary>
public sealed class BackupService
{
    private readonly string _syncRoot;
    private readonly string _configBackupRoot;

    /// <param name="configBackupRoot">
    /// Overridable so tests never scan — or delete from — the real machine folder.
    /// </param>
    public BackupService(string syncRoot, string? configBackupRoot = null)
    {
        _syncRoot = syncRoot;
        _configBackupRoot = configBackupRoot ?? DefaultConfigBackupRoot;
    }

    public string ProfileBackupRoot => Path.Combine(_syncRoot, ".backup");
    public string SaveBackupRoot => Path.Combine(_syncRoot, ".backup", "saves");
    public string ConfigBackupRoot => _configBackupRoot;

    public static string DefaultConfigBackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsaacProfileManager", "backups");

    /// <summary>
    /// Save backups are named <c>&lt;stamp&gt;-manual</c> or
    /// <c>&lt;stamp&gt;-before-...</c> and written with a file copy. Anything
    /// else under .backup got there by a move.
    /// </summary>
    public static BackupKind ClassifyName(string folderName)
    {
        if (folderName.Contains("removed", StringComparison.OrdinalIgnoreCase)) return BackupKind.MovedOriginal;

        var dash = folderName.IndexOf('-', 9);   // past the yyyyMMdd-HHmmss stamp
        if (dash < 0) return BackupKind.MovedOriginal;

        var reason = folderName[(dash + 1)..];
        return reason.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
               reason.StartsWith("before-", StringComparison.OrdinalIgnoreCase)
            ? BackupKind.Copy
            : BackupKind.MovedOriginal;
    }

    public IReadOnlyList<BackupEntry> Scan()
    {
        var entries = new List<BackupEntry>();

        if (Directory.Exists(SaveBackupRoot))
        {
            foreach (var dir in new DirectoryInfo(SaveBackupRoot).GetDirectories())
                entries.Add(Describe(dir, ClassifyName(dir.Name)));
        }

        if (Directory.Exists(ProfileBackupRoot))
        {
            foreach (var dir in new DirectoryInfo(ProfileBackupRoot).GetDirectories())
            {
                // The saves folder is enumerated above, not as one lump.
                if (string.Equals(dir.Name, "saves", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(Describe(dir, BackupKind.MovedOriginal));
            }
        }

        if (Directory.Exists(ConfigBackupRoot))
        {
            foreach (var file in new DirectoryInfo(ConfigBackupRoot).GetFiles("*.vdf"))
                entries.Add(new BackupEntry(file.FullName, file.Name, BackupKind.Copy, file.LastWriteTime, file.Length, 1));
        }

        return entries.OrderByDescending(e => e.When).ToList();
    }

    public long TotalBytes() => Scan().Sum(e => e.SizeBytes);

    /// <summary>
    /// What <see cref="Prune"/> would remove: pure copies beyond the newest
    /// <paramref name="keep"/>, and older than <paramref name="minimumAgeDays"/>.
    /// Both conditions must hold, so a burst of backups today is never pruned.
    /// </summary>
    public IReadOnlyList<BackupEntry> PlanPrune(int keep = 10, int minimumAgeDays = 1)
    {
        var cutoff = DateTime.Now.AddDays(-minimumAgeDays);

        return Scan()
            .Where(e => e.IsSafeToPrune)
            .OrderByDescending(e => e.When)
            .Skip(Math.Max(keep, 0))
            .Where(e => e.When < cutoff)
            .ToList();
    }

    public IReadOnlyList<BackupEntry> Prune(int keep = 10, int minimumAgeDays = 1)
    {
        var doomed = PlanPrune(keep, minimumAgeDays);

        foreach (var entry in doomed)
        {
            try
            {
                if (File.Exists(entry.Path)) File.Delete(entry.Path);
                else if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return doomed;
    }

    /// <summary>
    /// Delete one backup outright, whatever its kind. Only ever called after the
    /// caller has shown the user what it is — a moved original has no other copy.
    /// </summary>
    public void Delete(BackupEntry entry)
    {
        if (File.Exists(entry.Path)) { File.Delete(entry.Path); return; }
        if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, recursive: true);
    }

    private static BackupEntry Describe(DirectoryInfo dir, BackupKind kind)
    {
        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                count++;
            }
        }
        catch (IOException) { }

        return new BackupEntry(dir.FullName, dir.Name, kind, dir.LastWriteTime, bytes, count);
    }
}
