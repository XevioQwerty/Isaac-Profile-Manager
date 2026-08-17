using System.Text;

namespace IsaacProfileManager.Core.Services;

public enum LaunchMode
{
    Vanilla = 0,
    Repentogon = 1,
}

public interface ILauncherIniService
{
    string IniPath { get; }
    bool Exists { get; }
    string? Get(string section, string key);
    bool TrySet(string section, string key, string value);
    LaunchMode? GetLaunchMode();
    bool TrySetLaunchMode(LaunchMode mode);
}

/// <summary>
/// Reads and writes <c>Documents\My Games\repentogon_launcher.ini</c>.
///
/// The launcher owns this file and rewrites it on exit, so anything we write is
/// a request, not a durable setting — always re-read before displaying state.
/// Writes preserve unknown keys, comments and section order for the same reason:
/// we are a guest in someone else's file.
/// </summary>
public sealed class LauncherIniService : ILauncherIniService
{
    public string IniPath { get; }

    public LauncherIniService(string? iniPath = null)
    {
        IniPath = iniPath ?? Path.Combine(
            // Not %USERPROFILE%\Documents — OneDrive redirection breaks that.
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "repentogon_launcher.ini");
    }

    public bool Exists => File.Exists(IniPath);

    public string? Get(string section, string key)
    {
        if (!Exists) return null;

        string? current = null;
        foreach (var line in File.ReadAllLines(IniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1].Trim();
                continue;
            }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;

            var separator = trimmed.IndexOf('=');
            if (separator < 0) continue;
            if (string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return trimmed[(separator + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Set one key, leaving every other byte of the file alone. Returns false if
    /// there is no ini to write to — the launcher is not installed, which is not
    /// an error, just nothing to do.
    /// </summary>
    public bool TrySet(string section, string key, string value)
    {
        if (!Exists) return false;

        var lines = File.ReadAllLines(IniPath).ToList();
        var output = new List<string>(lines.Count + 2);
        var inSection = false;
        var written = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var isHeader = trimmed.StartsWith('[') && trimmed.EndsWith(']');

            if (isHeader)
            {
                // Leaving the target section without having found the key: append
                // it here so it lands inside the right section rather than at EOF.
                if (inSection && !written)
                {
                    output.Add($"{key} = {value}");
                    written = true;
                }
                inSection = string.Equals(trimmed[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                output.Add(line);
                continue;
            }

            if (inSection && !written)
            {
                var separator = trimmed.IndexOf('=');
                if (separator > 0 && string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    output.Add($"{key} = {value}");
                    written = true;
                    continue;
                }
            }

            output.Add(line);
        }

        if (inSection && !written)
        {
            output.Add($"{key} = {value}");
            written = true;
        }

        if (!written)
        {
            output.Add($"[{section}]");
            output.Add($"{key} = {value}");
        }

        File.WriteAllLines(IniPath, output, new UTF8Encoding(false));
        return true;
    }

    /// <summary><c>[Shared] LaunchMode</c>: 1 = REPENTOGON, 0 = vanilla (which then gets --repentogonoff).</summary>
    public LaunchMode? GetLaunchMode()
    {
        var raw = Get("Shared", "LaunchMode");
        if (raw is null) return null;
        return int.TryParse(raw, out var value) && value == 1 ? LaunchMode.Repentogon : LaunchMode.Vanilla;
    }

    public bool TrySetLaunchMode(LaunchMode mode) => TrySet("Shared", "LaunchMode", ((int)mode).ToString());
}
