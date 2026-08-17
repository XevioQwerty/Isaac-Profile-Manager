using System.Security.Cryptography;

namespace IsaacProfileManager.Core.Services;

public sealed record ArchivedLog(string Path, string Name, DateTime When, long SizeBytes, string? GameVersion)
{
    public double SizeKb => Math.Round(SizeBytes / 1024d, 1);
    public string Label => GameVersion is null ? Name : $"{Name}  ({GameVersion})";
}

/// <summary>
/// Keeps copies of previous runs' logs.
///
/// The game truncates <c>log.txt</c> on every launch, so without this you can
/// never compare a run that worked against the one that broke — which is the
/// first thing you want when a modpack starts crashing.
///
/// Archives are machine-local and go under LocalAppData rather than the synced
/// profiles folder; nobody else wants your logs.
/// </summary>
public sealed class LogArchiveService
{
    private readonly LogReaderService _reader;
    private readonly string _archiveRoot;

    /// <param name="archiveRoot">
    /// Overridable so tests never write to — or delete — the real archive folder.
    /// </param>
    public LogArchiveService(LogReaderService? reader = null, string? archiveRoot = null)
    {
        _reader = reader ?? new LogReaderService();
        _archiveRoot = archiveRoot ?? DefaultArchiveRoot;
    }

    public string ArchiveRoot => _archiveRoot;

    public static string DefaultArchiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsaacProfileManager", "logs");

    public IReadOnlyList<ArchivedLog> List()
    {
        if (!Directory.Exists(ArchiveRoot)) return Array.Empty<ArchivedLog>();

        return new DirectoryInfo(ArchiveRoot).GetFiles("*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ArchivedLog(f.FullName, Path.GetFileNameWithoutExtension(f.Name),
                                         f.LastWriteTime, f.Length, VersionFromName(f.Name)))
            .ToList();
    }

    /// <summary>
    /// Copy the current log aside. Returns null when there is nothing to archive,
    /// or when the newest archive already has identical contents — launching the
    /// app repeatedly must not fill the folder with duplicates.
    /// </summary>
    public ArchivedLog? ArchiveCurrent()
    {
        if (!_reader.Exists) return null;

        var current = HashOf(_reader.LogPath);
        if (current is null) return null;

        var newest = List().FirstOrDefault();
        if (newest is not null && HashOf(newest.Path) == current) return null;

        var version = _reader.Summarise(_reader.Read()).GameVersion;
        var stamp = (_reader.LastWritten ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        var name = version is null ? stamp : $"{stamp}_{Sanitise(version)}";

        Directory.CreateDirectory(ArchiveRoot);
        var destination = Path.Combine(ArchiveRoot, name + ".log");
        for (var n = 2; File.Exists(destination); n++)
            destination = Path.Combine(ArchiveRoot, $"{name}-{n}.log");

        // Read with sharing flags: the game may still hold the log open.
        using (var source = new FileStream(_reader.LogPath, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
        using (var target = File.Create(destination))
        {
            source.CopyTo(target);
        }

        var info = new FileInfo(destination);
        return new ArchivedLog(destination, Path.GetFileNameWithoutExtension(info.Name),
                               info.LastWriteTime, info.Length, version);
    }

    public void Delete(ArchivedLog log)
    {
        if (File.Exists(log.Path)) File.Delete(log.Path);
    }

    /// <summary>Drop the oldest archives beyond <paramref name="keep"/>.</summary>
    public IReadOnlyList<ArchivedLog> Prune(int keep = 20)
    {
        var doomed = List().Skip(Math.Max(keep, 0)).ToList();
        foreach (var log in doomed)
        {
            try { Delete(log); }
            catch (IOException) { }
        }
        return doomed;
    }

    private static string? VersionFromName(string fileName)
    {
        var underscore = fileName.IndexOf('_');
        if (underscore < 0) return null;
        var rest = Path.GetFileNameWithoutExtension(fileName)[(underscore + 1)..];
        return rest.Length == 0 ? null : rest;
    }

    private static string Sanitise(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(c => !invalid.Contains(c)).ToArray());
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
