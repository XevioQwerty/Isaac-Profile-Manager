using System.Text.RegularExpressions;

namespace IsaacProfileManager.Core.Services;

public enum LogSeverity
{
    Info,
    Assert,
    Error,
}

public sealed record LogLine(int Number, LogSeverity Severity, string Text, LogCategory Category);

/// <summary>Structured lines worth jumping straight to when something is wrong.</summary>
[Flags]
public enum LogCategory
{
    None = 0,
    ModLoaded = 1 << 0,
    LuaScript = 1 << 1,
    LuaDebug = 1 << 2,
    Checksum = 1 << 3,
    Version = 1 << 4,
    CommandLine = 1 << 5,
}

/// <summary>What a run's log says about itself, without reading 4,000 lines.</summary>
public sealed record LogSummary(
    string? GameVersion,
    string? CommandLine,
    int ModsLoaded,
    int Errors,
    int Asserts,
    int TotalLines,
    bool HasChecksums,
    DateTime? Written)
{
    /// <summary>Vanilla runs get --repentogonoff; its absence with a version of J273 means REPENTOGON.</summary>
    public bool LooksLikeVanilla => CommandLine?.Contains("--repentogonoff", StringComparison.OrdinalIgnoreCase) ?? false;
}

public interface ILogReaderService
{
    string LogPath { get; }
    bool Exists { get; }
    IReadOnlyList<LogLine> Read(int maxLines = 200_000);
    LogSummary Summarise(IReadOnlyList<LogLine> lines);
    IReadOnlyList<string> LoadedMods(IReadOnlyList<LogLine> lines);
}

/// <summary>
/// Reads the game's <c>log.txt</c>.
///
/// Strictly read-only, and opened with the sharing flags the game's own handle
/// requires — Isaac keeps the file open while it runs, so anything stricter
/// throws instead of tailing. The game truncates the log on every launch, so
/// this is always "the latest run".
/// </summary>
public sealed class LogReaderService : ILogReaderService
{
    private static readonly Regex TagPattern = new(@"^\[(\w+)\]\s*-\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"Game Version:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex ModPattern = new(@"LOADED MOD\s+(.+?)/?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The game says where it saves, every launch:
    /// <c>Loading PersistentGameData from Steam Cloud: rep+persistentgamedata1.dat.</c>
    /// Verified 2026-09-03 across 40 archived logs; every one said Steam Cloud.
    /// </summary>
    private static readonly Regex SaveTransportPattern = new(@"PersistentGameData (?:from|to) (.+?):\s", RegexOptions.Compiled);

    /// <summary>What the log says the saves are read from and written to, or null when it never said.</summary>
    public static string? SaveTransport(IReadOnlyList<LogLine> lines)
    {
        foreach (var line in lines)
        {
            var match = SaveTransportPattern.Match(line.Text);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    public string LogPath { get; }

    public LogReaderService(string? logPath = null)
    {
        LogPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "Binding of Isaac Repentance+",
            "log.txt");
    }

    public bool Exists => File.Exists(LogPath);

    public DateTime? LastWritten => Exists ? File.GetLastWriteTime(LogPath) : null;

    /// <summary>
    /// Just the <c>Game Version:</c> line, without reading the whole log. It is
    /// within the first hundred lines on every log seen; the cap is a guard
    /// against a log with no such line, not a tuning knob.
    /// </summary>
    public string? ReadGameVersion(int maxLines = 400) => ReadGameVersion(LogPath, maxLines);

    public static string? ReadGameVersion(string logPath, int maxLines = 400) => ReadRun(logPath, maxLines).GameVersion;

    /// <summary>
    /// Which build wrote the log, from its command line: REPENTOGON's launcher
    /// runs <c>Repentogon\isaac-ng.exe</c>, and a vanilla launch through it
    /// carries <c>--repentogonoff</c>. A plain launch has neither and is
    /// vanilla. Verified 2026-09-04 against both kinds of log.
    /// </summary>
    public sealed record LogRun(string? GameVersion, Models.GameBuild Build)
    {
        public static readonly LogRun None = new(null, Models.GameBuild.Unknown);
    }

    /// <summary>
    /// The version and the build of the run that wrote the log. The version
    /// belongs to that build, not to the machine: after a REPENTOGON session
    /// the log says J273, and that says nothing about what a vanilla launch
    /// will run.
    /// </summary>
    public LogRun ReadRun(int maxLines = 400) => ReadRun(LogPath, maxLines);

    public static LogRun ReadRun(string logPath, int maxLines = 400)
    {
        if (!File.Exists(logPath)) return LogRun.None;

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? version = null;
        var build = Models.GameBuild.Unknown;
        string? raw;
        var number = 0;
        while ((raw = reader.ReadLine()) is not null && number++ < maxLines)
        {
            if (version is null)
            {
                var match = VersionPattern.Match(raw);
                if (match.Success) version = match.Groups[1].Value;
            }

            if (raw.Contains("--repentogonoff", StringComparison.OrdinalIgnoreCase))
                build = Models.GameBuild.Vanilla;
            else if (build == Models.GameBuild.Unknown &&
                     raw.Contains("isaac-ng.exe", StringComparison.OrdinalIgnoreCase))
                build = raw.Replace('/', '\\').Contains("\\Repentogon\\isaac-ng.exe", StringComparison.OrdinalIgnoreCase)
                    ? Models.GameBuild.Repentogon
                    : Models.GameBuild.Vanilla;

            if (version is not null && build != Models.GameBuild.Unknown) break;
        }

        if (version is not null && build == Models.GameBuild.Unknown) build = Models.GameBuild.Vanilla;
        return new LogRun(version, build);
    }

    public IReadOnlyList<LogLine> Read(int maxLines = 200_000)
    {
        if (!Exists) return Array.Empty<LogLine>();

        var result = new List<LogLine>();

        // The game holds log.txt open for writing; opening it any other way
        // throws IOException rather than tailing.
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? raw;
        var number = 0;
        while ((raw = reader.ReadLine()) is not null && result.Count < maxLines)
        {
            number++;
            var match = TagPattern.Match(raw);
            var severity = LogSeverity.Info;
            var text = raw;

            if (match.Success)
            {
                severity = match.Groups[1].Value.ToUpperInvariant() switch
                {
                    "ERROR" => LogSeverity.Error,
                    "ASSERT" => LogSeverity.Assert,
                    _ => LogSeverity.Info,
                };
                text = match.Groups[2].Value;
            }

            result.Add(new LogLine(number, severity, text, Categorise(text)));
        }

        return result;
    }

    private static LogCategory Categorise(string text)
    {
        var category = LogCategory.None;

        if (text.StartsWith("LOADED MOD", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.ModLoaded;
        if (text.StartsWith("Running Lua Script", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.LuaScript;
        if (text.StartsWith("Lua Debug", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.LuaDebug;
        if (text.Contains("Checksum", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.Checksum;
        if (text.StartsWith("Game Version", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.Version;
        if (text.StartsWith("Command Line", StringComparison.OrdinalIgnoreCase)) category |= LogCategory.CommandLine;

        return category;
    }

    public LogSummary Summarise(IReadOnlyList<LogLine> lines)
    {
        string? version = null;
        string? commandLine = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (version is null && line.Category.HasFlag(LogCategory.Version))
            {
                var match = VersionPattern.Match(line.Text);
                if (match.Success) version = match.Groups[1].Value;
            }

            if (commandLine is null && line.Category.HasFlag(LogCategory.CommandLine))
            {
                // "Command Line:" is a header; the arguments are on the lines
                // after it, until the next non-argument line.
                var parts = new List<string>();
                for (var j = i + 1; j < lines.Count && j <= i + 6; j++)
                {
                    var candidate = lines[j].Text.Trim();
                    if (candidate.Length == 0 || !candidate.StartsWith('-')) break;
                    parts.Add(candidate);
                }
                commandLine = parts.Count > 0 ? string.Join(' ', parts) : "(none)";
            }
        }

        return new LogSummary(
            GameVersion: version,
            CommandLine: commandLine,
            ModsLoaded: lines.Count(l => l.Category.HasFlag(LogCategory.ModLoaded)),
            Errors: lines.Count(l => l.Severity == LogSeverity.Error),
            Asserts: lines.Count(l => l.Severity == LogSeverity.Assert),
            TotalLines: lines.Count,
            HasChecksums: lines.Any(l => l.Category.HasFlag(LogCategory.Checksum)),
            Written: LastWritten);
    }

    /// <summary>
    /// One player's row from the log's checksum table. The row that disagrees
    /// with the others names the machine to investigate.
    /// </summary>
    public sealed record PlayerChecksum(string Player, string Checksum, string GlobalRng)
    {
        public bool IsOdd { get; set; }
    }

    private static readonly Regex ChecksumPattern = new(
        @"(Player\d+)\s*:\s*Checksum\s*\(([0-9a-fA-F]+)\).*?Global RNG checksum\s*\(([0-9a-fA-F]+)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse the per-player checksum table and mark the rows in the minority.
    /// The README's manual procedure — find whose row differs — done for you.
    /// </summary>
    public static IReadOnlyList<PlayerChecksum> Checksums(IReadOnlyList<LogLine> lines)
    {
        var rows = new List<PlayerChecksum>();

        foreach (var line in lines)
        {
            var match = ChecksumPattern.Match(line.Text);
            if (match.Success)
                rows.Add(new PlayerChecksum(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
        }

        if (rows.Count < 2) return rows;

        // Whatever most players agree on is the baseline; anything else is odd.
        var majorityChecksum = rows.GroupBy(r => r.Checksum).OrderByDescending(g => g.Count()).First().Key;
        var majorityRng = rows.GroupBy(r => r.GlobalRng).OrderByDescending(g => g.Count()).First().Key;

        foreach (var row in rows)
            row.IsOdd = row.Checksum != majorityChecksum || row.GlobalRng != majorityRng;

        return rows;
    }

    /// <summary>Mod folder names in load order, taken from the LOADED MOD lines.</summary>
    public IReadOnlyList<string> LoadedMods(IReadOnlyList<LogLine> lines)
    {
        var mods = new List<string>();

        foreach (var line in lines.Where(l => l.Category.HasFlag(LogCategory.ModLoaded)))
        {
            var match = ModPattern.Match(line.Text);
            if (!match.Success) continue;

            // ".../mods/<name>/content" — the folder under mods\ is what we want.
            var path = match.Groups[1].Value.Replace('\\', '/').TrimEnd('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var modsIndex = Array.FindLastIndex(segments, s => s.Equals("mods", StringComparison.OrdinalIgnoreCase));

            if (modsIndex >= 0 && modsIndex + 1 < segments.Length) mods.Add(segments[modsIndex + 1]);
            else if (segments.Length > 0) mods.Add(segments[^1]);
        }

        return mods;
    }
}
