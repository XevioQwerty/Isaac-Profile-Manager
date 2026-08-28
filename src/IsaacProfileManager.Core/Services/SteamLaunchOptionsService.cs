namespace IsaacProfileManager.Core.Services;

public enum LaunchOptionsState
{
    /// <summary>Starts the REPENTOGON launcher and hands it the game — what per-profile build selection needs.</summary>
    LauncherConfigured,

    /// <summary>Something is set, but not the launcher line.</summary>
    Other,

    /// <summary>Empty. Steam starts the game directly, so the build never follows the profile.</summary>
    Empty,

    /// <summary>Steam's config could not be read.</summary>
    Unknown,
}

public sealed record LaunchOptionsStatus(LaunchOptionsState State, string? Current, string? Suggested)
{
    public bool IsCorrect => State == LaunchOptionsState.LauncherConfigured;
}

public enum LaunchOptionsWriteResult
{
    Written,
    AlreadyCorrect,

    /// <summary>Steam holds this file in memory and rewrites it on exit, so a write now would be lost.</summary>
    SteamRunning,

    /// <summary>Steam has no record of the game for this account — it has never been launched.</summary>
    NoAppNode,

    /// <summary>Steam's config could not be found or read.</summary>
    Unavailable,
}

public sealed record LaunchOptionsWrite(LaunchOptionsWriteResult Result, string? BackupPath, string Message)
{
    public bool Ok => Result is LaunchOptionsWriteResult.Written or LaunchOptionsWriteResult.AlreadyCorrect;
}

/// <summary>
/// Reads Steam's per-game launch options and checks whether they start the
/// REPENTOGON launcher.
///
/// Without that line Steam launches the game directly, `[Shared] LaunchMode` is
/// never consulted, and per-profile build selection silently does nothing — a
/// setup error that presents as "switching the build didn't work". It is also
/// what the REPENTOGON docs require for Steam Remote Play.
/// </summary>
public sealed class SteamLaunchOptionsService
{
    private readonly SteamCloudService _steam;
    private readonly IGameProcessService? _process;
    private readonly string _backupRoot;

    public SteamLaunchOptionsService(SteamCloudService? steam = null,
                                     IGameProcessService? process = null,
                                     string? backupRoot = null)
    {
        _steam = steam ?? new SteamCloudService();
        _process = process;
        _backupRoot = backupRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IsaacProfileManager", "backups");
    }

    /// <summary>The line Steam needs, given where the launcher actually lives.</summary>
    public static string Suggest(string launcherExePath) => $"\"{launcherExePath}\" --isaac=%command%";

    public LaunchOptionsStatus Check(string? launcherExePath, string appId = SteamCloudService.IsaacAppId)
    {
        var suggested = string.IsNullOrWhiteSpace(launcherExePath) ? null : Suggest(launcherExePath!);
        var status = _steam.GetStatus(appId);

        if (status.SteamRoot is null || status.AccountId is null)
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);

        var localConfig = Path.Combine(status.SteamRoot, "userdata", status.AccountId, "config", "localconfig.vdf");
        if (!File.Exists(localConfig))
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);

        string? current;
        try
        {
            var root = VdfParser.ParseFile(localConfig);
            var store = root["UserLocalConfigStore"] ?? root.Children.Values.FirstOrDefault();
            current = store?["Software"]?["Valve"]?["Steam"]?["apps"]?[appId]?["LaunchOptions"]?.Value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new LaunchOptionsStatus(LaunchOptionsState.Unknown, null, suggested);
        }

        if (string.IsNullOrWhiteSpace(current))
            return new LaunchOptionsStatus(LaunchOptionsState.Empty, current, suggested);

        // Both halves matter: the launcher must run, and %command% is how Steam
        // passes it the game's own executable.
        var looksRight = current.Contains("REPENTOGONLauncher", StringComparison.OrdinalIgnoreCase)
                      && current.Contains("%command%", StringComparison.OrdinalIgnoreCase);

        return new LaunchOptionsStatus(
            looksRight ? LaunchOptionsState.LauncherConfigured : LaunchOptionsState.Other,
            current,
            suggested);
    }

    /// <summary>
    /// Write the launcher line into Steam's per-game launch options.
    ///
    /// Two things make this delicate. Steam keeps localconfig.vdf in memory and
    /// rewrites it wholesale on exit, so a write while Steam runs is simply
    /// discarded — this refuses rather than appearing to succeed. And the file
    /// is ~440 KB of Steam's own state containing several sections named
    /// "apps", so the edit is targeted at one node found by walking the key
    /// path, and every other byte is left exactly as it was.
    /// </summary>
    public LaunchOptionsWrite Apply(string launcherExePath, string appId = SteamCloudService.IsaacAppId)
    {
        if (string.IsNullOrWhiteSpace(launcherExePath))
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.Unavailable, null,
                "No launcher path is configured, so there is nothing to write.");

        if (IsSteamRunning())
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.SteamRunning, null,
                "Steam is running. It rewrites this file when it exits, so the change would be lost. " +
                "Close Steam completely and try again.");

        var status = _steam.GetStatus(appId);
        if (status.SteamRoot is null || status.AccountId is null)
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.Unavailable, null,
                "Could not find your Steam user folder.");

        var path = Path.Combine(status.SteamRoot, "userdata", status.AccountId, "config", "localconfig.vdf");
        if (!File.Exists(path))
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.Unavailable, null, $"No Steam config at {path}.");

        var wanted = Suggest(launcherExePath);

        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex)
        {
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.Unavailable, null, ex.Message);
        }

        var edited = SetLaunchOptions(text, appId, wanted, out var changed);

        if (!changed && edited is null)
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.NoAppNode, null,
                $"Steam has no record of app {appId} for this account. Launch the game from Steam once, then try again.");

        if (!changed)
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.AlreadyCorrect, null,
                "Steam already has the right launch options.");

        string backup;
        try
        {
            Directory.CreateDirectory(_backupRoot);
            backup = Path.Combine(_backupRoot, $"localconfig-{DateTime.Now:yyyyMMdd-HHmmss}.vdf");
            File.Copy(path, backup, overwrite: false);

            // Written beside the original and swapped in, so a failure part way
            // through cannot leave Steam with half a config file.
            var temp = path + ".ipm-tmp";
            File.WriteAllText(temp, edited!, new System.Text.UTF8Encoding(false));
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LaunchOptionsWrite(LaunchOptionsWriteResult.Unavailable, null,
                $"Could not write Steam's config: {ex.Message}");
        }

        return new LaunchOptionsWrite(LaunchOptionsWriteResult.Written, backup,
            "Steam launch options set. They take effect the next time you launch from Steam.");
    }

    private bool IsSteamRunning()
    {
        if (_process is not null) return false;   // injected for tests; the real check is below

        try
        {
            return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Return <paramref name="text"/> with the app's LaunchOptions set, or null
    /// when the app has no node at all. <paramref name="changed"/> is false when
    /// the value was already what we wanted.
    ///
    /// Public so it can be tested against realistic files without touching a
    /// real Steam install.
    /// </summary>
    public static string? SetLaunchOptions(string text, string appId, string value, out bool changed)
    {
        changed = false;

        var body = FindSection(text, new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId });
        if (body is null) return null;

        var (start, end) = body.Value;
        var existing = FindDirectKey(text, start, end, "LaunchOptions");
        var encoded = Escape(value);

        if (existing is not null)
        {
            var (valueStart, valueEnd) = existing.Value;
            if (text[valueStart..valueEnd] == encoded) return text;

            changed = true;
            return text[..valueStart] + encoded + text[valueEnd..];
        }

        // No key yet. Match the indentation Steam used for this node's children
        // so the file stays readable and diffable.
        var indent = ChildIndent(text, start, end);

        // The file's own line ending, not this machine's. Steam writes CRLF on
        // Windows, but mixing endings into a file we are only meant to touch in
        // one place is exactly the sort of gratuitous change to avoid.
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = $"{indent}\"LaunchOptions\"\t\t\"{encoded}\"{newline}";

        var lineStart = text.IndexOf('\n', start);
        if (lineStart < 0) return null;

        changed = true;
        return text[..(lineStart + 1)] + insertion + text[(lineStart + 1)..];
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>The indentation Steam used for the first child of this section.</summary>
    private static string ChildIndent(string text, int bodyStart, int bodyEnd)
    {
        var line = text.IndexOf('\n', bodyStart);
        if (line >= 0 && line < bodyEnd)
        {
            var i = line + 1;
            var run = i;
            while (run < bodyEnd && (text[run] == '\t' || text[run] == ' ')) run++;
            if (run > i) return text[i..run];
        }
        return "\t";
    }

    /// <summary>
    /// The span of a section's body, located by walking the key path from the
    /// root. Walking matters: localconfig.vdf contains several sections named
    /// "apps", and matching the first one found anywhere edits the wrong game.
    /// </summary>
    private static (int Start, int End)? FindSection(string text, IReadOnlyList<string> path)
    {
        var stack = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            var c = text[position];

            if (c == '}')
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                position++;
                continue;
            }

            if (c != '"') { position++; continue; }

            var key = ReadQuoted(text, ref position);
            SkipTrivia(text, ref position);

            if (position < text.Length && text[position] == '{')
            {
                stack.Add(key);
                var open = position;
                position++;

                if (Matches(stack, path))
                {
                    var close = MatchingBrace(text, open);
                    return close < 0 ? null : (open + 1, close);
                }
                continue;
            }

            // A plain value; step over it so its contents are never scanned.
            if (position < text.Length && text[position] == '"') ReadQuoted(text, ref position);
        }

        return null;
    }

    /// <summary>A key directly inside this section, ignoring anything nested deeper.</summary>
    private static (int Start, int End)? FindDirectKey(string text, int bodyStart, int bodyEnd, string name)
    {
        var position = bodyStart;
        var depth = 0;

        while (position < bodyEnd)
        {
            var c = text[position];

            if (c == '{') { depth++; position++; continue; }
            if (c == '}') { depth--; position++; continue; }
            if (c != '"') { position++; continue; }

            var keyStart = position;
            var key = ReadQuoted(text, ref position);
            SkipTrivia(text, ref position);

            if (position < bodyEnd && text[position] == '{')
            {
                // A nested section: skip its whole body rather than descending.
                var close = MatchingBrace(text, position);
                position = close < 0 ? bodyEnd : close + 1;
                continue;
            }

            if (position < bodyEnd && text[position] == '"')
            {
                var valueOpen = position;
                ReadQuoted(text, ref position);

                if (depth == 0 && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return (valueOpen + 1, position - 1);
            }

            _ = keyStart;
        }

        return null;
    }

    private static bool Matches(List<string> stack, IReadOnlyList<string> path)
    {
        if (stack.Count != path.Count) return false;
        for (var i = 0; i < path.Count; i++)
            if (!string.Equals(stack[i], path[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string ReadQuoted(string text, ref int position)
    {
        position++;   // opening quote
        var builder = new System.Text.StringBuilder();

        while (position < text.Length && text[position] != '"')
        {
            if (text[position] == '\\' && position + 1 < text.Length)
            {
                builder.Append(text[position]).Append(text[position + 1]);
                position += 2;
                continue;
            }
            builder.Append(text[position]);
            position++;
        }

        position++;   // closing quote
        return builder.ToString();
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var position = open;

        while (position < text.Length)
        {
            var c = text[position];

            if (c == '"') { ReadQuoted(text, ref position); continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return position;
            }

            position++;
        }

        return -1;
    }
}
