namespace IsaacProfileManager.Core.Services;

public sealed record GameInstall(string IsaacExe, string GameDir, string ModsDir, string Source);

public interface IGameDetectionService
{
    GameInstall? FindInstall();
    string? FindRepentogonLauncher(string gameDir);
}

/// <summary>
/// Locates the game and the REPENTOGON launcher. Ported from the discovery
/// logic in IsaacProfiles.ps1 so both tools agree on what they find.
/// </summary>
public sealed class GameDetectionService : IGameDetectionService
{
    private const string IsaacExeName = "isaac-ng.exe";
    private const string LauncherExeName = "REPENTOGONLauncher.exe";

    private static readonly string[] SteamSubPaths =
    {
        @"Program Files (x86)\Steam",
        "Steam",
        "SteamLibrary",
    };

    private readonly ILauncherIniService _launcherIni;

    public GameDetectionService(ILauncherIniService launcherIni) => _launcherIni = launcherIni;

    public GameInstall? FindInstall()
    {
        // The launcher's own ini is the most reliable source: the user already
        // pointed it at the right exe.
        var fromIni = _launcherIni.Get("General", "IsaacExecutable");
        if (!string.IsNullOrWhiteSpace(fromIni) && File.Exists(fromIni))
            return Describe(fromIni, "repentogon_launcher.ini");

        foreach (var drive in ReadyDrives())
        {
            foreach (var sub in SteamSubPaths)
            {
                var candidate = Path.Combine(drive, sub, @"steamapps\common\The Binding of Isaac Rebirth", IsaacExeName);
                if (File.Exists(candidate)) return Describe(candidate, "standard Steam path");
            }
        }

        return null;
    }

    /// <summary>
    /// The launcher is expected to live outside the game install — the official
    /// docs warn against extracting it there, and specifically against a folder
    /// named <c>repentogon</c> in the game dir, which belongs to the downgraded
    /// build. Installs that ignore that advice are still found by the final sweep.
    /// </summary>
    public string? FindRepentogonLauncher(string gameDir)
    {
        var parent = Directory.GetParent(gameDir)?.FullName;

        var roots = new List<string>();
        if (parent is not null) roots.Add(parent);
        roots.AddRange(ReadyDrives());

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "REPENTOGONLauncher", "Repentogon", "RepentogonLauncher" })
            {
                var candidate = Path.Combine(root, name, LauncherExeName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var searchRoot in new[] { gameDir, parent })
        {
            if (searchRoot is null || !Directory.Exists(searchRoot)) continue;
            var hit = EnumerateShallow(searchRoot, LauncherExeName, maxDepth: 2).FirstOrDefault();
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>Accept either the exe itself or a folder containing it.</summary>
    public static string? ResolveLauncherPath(string? pathText)
    {
        if (string.IsNullOrWhiteSpace(pathText)) return null;
        var path = pathText.Trim().Trim('"');

        if (File.Exists(path))
            return string.Equals(Path.GetFileName(path), LauncherExeName, StringComparison.OrdinalIgnoreCase) ? path : null;

        if (Directory.Exists(path))
        {
            var candidate = Path.Combine(path, LauncherExeName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static GameInstall Describe(string exePath, string source)
    {
        var gameDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        return new GameInstall(exePath, gameDir, Path.Combine(gameDir, "mods"), source);
    }

    private static IEnumerable<string> ReadyDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch (IOException) { ready = false; }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }

    private static IEnumerable<string> EnumerateShallow(string root, string fileName, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(current, fileName); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files) yield return file;
            if (depth >= maxDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(current); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                // Never descend through a link: the mods junction alone would
                // drag the search into the entire profile sync root.
                try
                {
                    if ((new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (IOException) { continue; }

                queue.Enqueue((child, depth + 1));
            }
        }
    }
}
