using System.Diagnostics;
using System.Text.Json;

namespace IsaacProfileManager.Core.Services;

/// <summary>What a write through Steam did, file by file.</summary>
public sealed record SteamSaveWriteResult(bool Ok, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted, IReadOnlyList<string> Problems)
{
    public static SteamSaveWriteResult Unavailable(string why) =>
        new(false, Array.Empty<string>(), Array.Empty<string>(), new[] { why });
}

/// <summary>
/// Puts save files into the live folder the way the game does: through
/// Steam's Remote Storage API.
///
/// The game reads its saves through that API, and Steam answers from its own
/// manifest (<c>remotecache.vdf</c>). A file copied into the folder behind
/// Steam's back can be invisible to the game — found 2026-09-04, a run file
/// with the right bytes on disk that the game logged as "could not find",
/// because Steam still held the "deleted" mark from an earlier run that had
/// ended. Writing through the API makes Steam index the file exactly as it
/// indexes the game's own writes. A plain file copy stays as the fallback for
/// when Steam is not running.
/// </summary>
public interface ISteamSaveWriter
{
    bool IsAvailable { get; }

    /// <summary>Delete the named files, then write the named files from a folder, all through Steam.</summary>
    SteamSaveWriteResult Replace(IReadOnlyList<string> deleteNames, IReadOnlyList<string> writeNames, string fromFolder);
}

/// <summary>Drives <c>ipm-steam-helper.exe cloud-replace</c>, the 32-bit process that can load the game's steam_api.dll.</summary>
public sealed class SteamHelperSaveWriter : ISteamSaveWriter
{
    private readonly string _gameDir;
    private readonly string? _helperPath;
    private readonly TimeSpan _timeout;

    public SteamHelperSaveWriter(string gameDir, string? helperPath = null, TimeSpan? timeout = null)
    {
        _gameDir = gameDir;
        _helperPath = helperPath ?? new WorkshopPullService(gameDir).HelperPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
    }

    public bool IsAvailable => _helperPath is not null && File.Exists(_helperPath) && File.Exists(Path.Combine(_gameDir, "steam_api.dll"));

    public SteamSaveWriteResult Replace(IReadOnlyList<string> deleteNames, IReadOnlyList<string> writeNames, string fromFolder)
    {
        if (!IsAvailable) return SteamSaveWriteResult.Unavailable(WorkshopPullService.NotFoundMessage());
        if (deleteNames.Count == 0 && writeNames.Count == 0) return new SteamSaveWriteResult(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var start = new ProcessStartInfo
        {
            FileName = _helperPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("cloud-replace");
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(_gameDir);
        if (writeNames.Count > 0)
        {
            start.ArgumentList.Add("--from");
            start.ArgumentList.Add(fromFolder);
        }
        foreach (var name in deleteNames) { start.ArgumentList.Add("--delete"); start.ArgumentList.Add(name); }
        foreach (var name in writeNames) { start.ArgumentList.Add("--write"); start.ArgumentList.Add(name); }

        var written = new List<string>();
        var deleted = new List<string>();
        var problems = new List<string>();
        var done = false;
        var ok = false;

        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var stderr = process.StandardError.ReadToEndAsync();

            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (!line.StartsWith('{')) continue;   // steam_api.dll's banner
                try
                {
                    var message = JsonDocument.Parse(line).RootElement;
                    var kind = message.TryGetProperty("event", out var e) ? e.GetString() : null;
                    switch (kind)
                    {
                        case "written":
                        case "deleted":
                            var name = message.GetProperty("name").GetString() ?? "?";
                            var fileOk = message.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                            if (fileOk) (kind == "written" ? written : deleted).Add(name);
                            else
                            {
                                var reason = message.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "refused";
                                problems.Add($"{name}: {reason}");
                            }
                            break;
                        case "error":
                            problems.Add(message.GetProperty("message").GetString() ?? "the helper reported an error");
                            break;
                        case "done":
                            done = true;
                            ok = message.TryGetProperty("ok", out var d) && d.ValueKind == JsonValueKind.True;
                            break;
                    }
                }
                catch (JsonException) { }
            }

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                problems.Add("the Steam helper did not finish in time");
            }

            var error = stderr.GetAwaiter().GetResult().Trim();
            if (!done && error.Length > 0 && process.ExitCode != 0) problems.Add(error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            problems.Add($"could not run the Steam helper: {ex.Message}");
        }

        if (!done) problems.Add("the Steam helper stopped before reporting");
        return new SteamSaveWriteResult(done && ok && problems.Count == 0, written, deleted, problems);
    }
}
