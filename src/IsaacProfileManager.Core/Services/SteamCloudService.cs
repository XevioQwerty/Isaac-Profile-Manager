using System.Diagnostics;
using Microsoft.Win32;

namespace IsaacProfileManager.Core.Services;

public enum SteamCloudState
{
    /// <summary>Explicitly turned off for this game. Save swapping is safe.</summary>
    Disabled,

    /// <summary>On, or defaulting to on. Steam may restore save files we replace.</summary>
    Enabled,

    /// <summary>Steam, the account, or its config could not be found.</summary>
    Unknown,
}

public sealed record SteamCloudStatus(
    SteamCloudState State,
    string? SteamRoot,
    string? AccountId,
    string? RemoteDir,
    string? SharedConfigPath,
    string? LastSyncState,
    bool ExplicitSetting,
    DateTime? SettingWritten = null,
    bool SteamRunning = false)
{
    /// <summary>
    /// Steam holds this setting in memory and only writes it out when it exits,
    /// so a value read while Steam is running can be a toggle or two behind what
    /// the properties dialog shows.
    /// </summary>
    public bool SettingMayBeStale => SteamRunning;

    /// <summary>
    /// Swapping save files is only supported with Cloud off for the game. With
    /// it on, Steam owns those files and may restore the set we just replaced.
    /// </summary>
    public bool SafeToSwapSaves => State == SteamCloudState.Disabled;
}

/// <summary>
/// Reads Steam's per-game Cloud setting, which is the precondition for save
/// switching.
///
/// Steam records it in the roaming <c>sharedconfig.vdf</c> at
/// <c>UserRoamingConfigStore\Software\Valve\Steam\apps\&lt;appid&gt;\cloudenabled</c>.
/// The key is **absent unless the user has touched the toggle**, and the default
/// is on — so anything other than an explicit "0" is treated as enabled. Being
/// wrong in that direction only costs a warning; being wrong the other way costs
/// someone's achievements.
///
/// Steam rewrites these files, so never cache the result — re-read every refresh.
/// </summary>
public sealed class SteamCloudService
{
    public const string IsaacAppId = "250900";

    private readonly string? _steamRootOverride;
    private readonly Func<bool> _isSteamRunning;
    private readonly string? _backupRootOverride;

    /// <param name="isSteamRunning">
    /// Injectable so the config-writing path can be tested on a machine where
    /// Steam happens to be running — otherwise those tests silently skip, which
    /// is worse than not having them.
    /// </param>
    public SteamCloudService(string? steamRoot = null, Func<bool>? isSteamRunning = null, string? backupRoot = null)
    {
        _steamRootOverride = steamRoot;
        _isSteamRunning = isSteamRunning ?? IsSteamRunning;
        _backupRootOverride = backupRoot;
    }

    /// <summary>Opens the game's properties dialog, where the Cloud toggle lives.</summary>
    public static string PropertiesUrl(string appId = IsaacAppId) => $"steam://gameproperties/{appId}";

    public static string? FindSteamRoot()
    {
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                using var subKey = hive.OpenSubKey(key);
                // Steam writes this lowercased with forward slashes.
                var path = subKey?.GetValue(value) as string;
                if (string.IsNullOrWhiteSpace(path)) continue;

                var normalised = Path.GetFullPath(path.Replace('/', '\\'));
                if (Directory.Exists(normalised)) return normalised;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // No registry access; fall through to the next candidate.
            }
        }
        return null;
    }

    public SteamCloudStatus GetStatus(string appId = IsaacAppId)
    {
        var running = _isSteamRunning();
        var root = _steamRootOverride ?? FindSteamRoot();
        if (root is null || !Directory.Exists(root))
            return new SteamCloudStatus(SteamCloudState.Unknown, root, null, null, null, null, false, null, running);

        var account = FindAccountFor(root, appId);
        if (account is null)
            return new SteamCloudStatus(SteamCloudState.Unknown, root, null, null, null, null, false, null, running);

        var remote = Path.Combine(root, "userdata", account, appId, "remote");
        var sharedConfig = Path.Combine(root, "userdata", account, "7", "remote", "sharedconfig.vdf");
        var lastSync = ReadLastSyncState(root, account, appId);

        if (!File.Exists(sharedConfig))
            return new SteamCloudStatus(SteamCloudState.Unknown, root, account, remote, null, lastSync, false, null, running);

        var setting = ReadCloudEnabled(sharedConfig, appId);

        // Absent means "never toggled", and the default is on.
        var state = setting == "0" ? SteamCloudState.Disabled : SteamCloudState.Enabled;

        return new SteamCloudStatus(
            state, root, account, remote, sharedConfig, lastSync,
            ExplicitSetting: setting is not null,
            SettingWritten: File.GetLastWriteTime(sharedConfig),
            SteamRunning: running);
    }

    public static bool IsSteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Write <c>cloudenabled</c> for an app directly into sharedconfig.vdf.
    ///
    /// Only possible with Steam closed: Steam keeps this file's contents in
    /// memory and rewrites the whole thing on exit, so anything written while it
    /// runs is discarded. The original is copied to
    /// <c>%LOCALAPPDATA%\IsaacProfileManager\backups\</c> first.
    ///
    /// Edits the single line rather than re-serialising the document, so every
    /// other byte Steam wrote is preserved exactly.
    /// </summary>
    public string SetCloudEnabled(bool enabled, string appId = IsaacAppId)
    {
        if (_isSteamRunning())
            throw new UnsafePathException(
                "Steam is running. It rewrites this file when it exits, so the change would be discarded.\n\n" +
                "Exit Steam completely — right-click the tray icon and choose Exit — then try again.");

        var status = GetStatus(appId);
        if (status.SharedConfigPath is null || !File.Exists(status.SharedConfigPath))
            throw new UnsafePathException("Steam's sharedconfig.vdf could not be found.");

        var path = status.SharedConfigPath;
        var backup = BackUp(path);

        var lines = File.ReadAllLines(path).ToList();
        var value = enabled ? "1" : "0";

        var appsIndex = FindBlock(lines, "apps", from: 0);
        if (appsIndex < 0)
            throw new UnsafePathException(
                $"No \"apps\" section in {path}. Open Steam, change any game's Cloud setting once so it creates one, exit Steam, then try again.");

        var appIndex = FindBlock(lines, appId, from: appsIndex);
        if (appIndex < 0)
        {
            // No entry for this app yet: add one inside the apps block.
            var indent = Indent(lines[appsIndex]) + "\t";
            lines.InsertRange(appsIndex + 2, new[]
            {
                $"{indent}\"{appId}\"",
                $"{indent}{{",
                $"{indent}\t\"cloudenabled\"\t\t\"{value}\"",
                $"{indent}}}",
            });
        }
        else
        {
            var keyIndex = lines.FindIndex(appIndex, i => i.Contains("\"cloudenabled\"", StringComparison.OrdinalIgnoreCase));
            var closeIndex = lines.FindIndex(appIndex + 2, l => l.Trim() == "}");

            if (keyIndex > 0 && (closeIndex < 0 || keyIndex < closeIndex))
                lines[keyIndex] = $"{Indent(lines[keyIndex])}\"cloudenabled\"\t\t\"{value}\"";
            else
                lines.Insert(appIndex + 2, $"{Indent(lines[appIndex])}\t\"cloudenabled\"\t\t\"{value}\"");
        }

        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));

        // Trust the file, not the write: re-read to confirm it took.
        if (ReadCloudEnabled(path, appId) != value)
            throw new UnsafePathException($"The setting did not take. The original was preserved at:\n{backup}");

        return backup;
    }

    /// <summary>Index of the line naming a block whose next line is its opening brace.</summary>
    private static int FindBlock(IReadOnlyList<string> lines, string name, int from)
    {
        var quoted = $"\"{name}\"";
        for (var i = Math.Max(from, 0); i < lines.Count - 1; i++)
        {
            if (lines[i].Trim() == quoted && lines[i + 1].Trim() == "{") return i;
        }
        return -1;
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private string BackUp(string path)
    {
        var folder = _backupRootOverride ?? BackupService.DefaultConfigBackupRoot;
        Directory.CreateDirectory(folder);

        // Second-resolution names collide when called twice in a second, and a
        // backup that throws instead of being written is worse than none.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(folder, $"sharedconfig-{stamp}.vdf");
        for (var n = 2; File.Exists(backup); n++)
            backup = Path.Combine(folder, $"sharedconfig-{stamp}-{n}.vdf");

        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    /// <summary>The account whose userdata holds this app, newest first when several do.</summary>
    public static string? FindAccountFor(string steamRoot, string appId)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return null;

        try
        {
            return new DirectoryInfo(userdata).GetDirectories()
                .Where(d => Directory.Exists(Path.Combine(d.FullName, appId)))
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(Path.Combine(d.FullName, appId)))
                .FirstOrDefault()?.Name;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walk the documented path rather than searching for "apps" anywhere in the
    /// tree: localconfig.vdf is 400 KB with more than one node by that name, and
    /// matching the wrong one silently reports the wrong answer.
    /// </summary>
    private static VdfNode? SteamApps(VdfNode root, string storeName)
    {
        var store = root[storeName] ?? root.Children.Values.FirstOrDefault();
        return store?["Software"]?["Valve"]?["Steam"]?["apps"];
    }

    private static string? ReadCloudEnabled(string sharedConfigPath, string appId)
    {
        try
        {
            var root = VdfParser.ParseFile(sharedConfigPath);
            return SteamApps(root, "UserRoamingConfigStore")?[appId]?["cloudenabled"]?.Value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Steam's own view of the folder — e.g. <c>changeslocally</c> after files are
    /// replaced. Advisory, but it is the signal that Steam noticed.
    /// </summary>
    private static string? ReadLastSyncState(string steamRoot, string account, string appId)
    {
        var localConfig = Path.Combine(steamRoot, "userdata", account, "config", "localconfig.vdf");
        if (!File.Exists(localConfig)) return null;

        try
        {
            var root = VdfParser.ParseFile(localConfig);
            return SteamApps(root, "UserLocalConfigStore")?[appId]?["cloud"]?["last_sync_state"]?.Value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
