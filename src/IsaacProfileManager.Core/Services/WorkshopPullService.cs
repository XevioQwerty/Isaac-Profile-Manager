using System.Diagnostics;
using System.Text.Json;
using IsaacProfileManager.Core;

namespace IsaacProfileManager.Core.Services;

/// <summary>One item's outcome from a pull.</summary>
public sealed record PulledItem(string Id, string State, string Path, long SizeOnDisk, long Timestamp)
{
    public bool Installed => State == "installed" && Path.Length > 0;
}

/// <summary>What a whole helper invocation reported.</summary>
public sealed record PullResult(
    bool Ok,
    IReadOnlyList<PulledItem> Items,
    IReadOnlyList<string> Errors,
    uint SubscribedAfter)
{
    public IEnumerable<PulledItem> Successful => Items.Where(i => i.Installed);

    /// <summary>Ids the helper reported dropping. Set by the unsubscribe verbs.</summary>
    public IReadOnlyList<string> Unsubscribed { get; init; } = Array.Empty<string>();

    /// <summary>How many were subscribed when the helper connected.</summary>
    public uint SubscribedBefore { get; init; }
}

public interface IWorkshopPullService
{
    bool IsAvailable { get; }
    string? HelperPath { get; }
    Task<PullResult> StatusAsync(CancellationToken cancellation = default);
    Task<PullResult> PullAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken cancellation = default);
    Task<PullResult> UnsubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken cancellation = default);
    Task<PullResult> UnsubscribeAllAsync(IProgress<string>? progress, CancellationToken cancellation = default);
}

/// <summary>
/// Drives <c>ipm-steam-helper.exe</c>, the 32-bit process that talks to Steam.
///
/// Out of process for a hard reason: Isaac ships only a 32-bit
/// <c>steam_api.dll</c>, so this 64-bit app cannot load it. Verified end to end
/// 2026-08-27 against the reference install — subscribe, download with progress,
/// install path, unsubscribe, content gone, <c>mods\</c> untouched throughout.
///
/// Two behaviours of Steam shape the contract:
/// <list type="bullet">
/// <item>Unsubscribing deletes the downloaded content within seconds, so the
/// copy into the library must happen between the pull and the unsubscribe.</item>
/// <item>Subscribing alone does <em>not</em> materialise anything into
/// <c>mods\</c>; that happens when the game launches. So an update run is safe
/// while Isaac is closed, and only then.</item>
/// </list>
/// </summary>
public sealed class WorkshopPullService : IWorkshopPullService
{
    public const string HelperFileName = "ipm-steam-helper.exe";

    private readonly string _gameDir;

    public WorkshopPullService(string gameDir, string? helperPath = null)
    {
        _gameDir = gameDir;
        HelperPath = helperPath ?? Locate();
    }

    public string? HelperPath { get; }

    public bool IsAvailable => HelperPath is not null && File.Exists(HelperPath);

    /// <summary>Every path checked, in order, so a failure can say where it looked.</summary>
    public static IReadOnlyList<string> ProbedPaths() => Probe().ToList();

    /// <summary>
    /// Beside the executable first, then the sibling build output a developer
    /// run produces.
    ///
    /// It must be <see cref="AppPaths"/> and not
    /// <see cref="AppContext.BaseDirectory"/>: this app self-extracts, so
    /// BaseDirectory is a temp folder, and looking there found nothing while the
    /// helper sat next to the exe the whole time.
    /// </summary>
    private static IEnumerable<string> Probe()
    {
        foreach (var root in AppPaths.ProbeRoots())
            yield return Path.Combine(root, HelperFileName);

        foreach (var root in AppPaths.ProbeRoots())
        {
            var directory = new DirectoryInfo(root);
            for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                foreach (var leaf in new[] { "publish", "" })
                    yield return Path.Combine(directory.FullName, "src", "IsaacProfileManager.SteamHelper",
                                              "bin", configuration, "net8.0", "win-x86", leaf, HelperFileName);
            }
        }
    }

    private static string? Locate() => Probe().FirstOrDefault(File.Exists);

    /// <summary>
    /// Naming every path checked, because "it is missing" sent the user looking
    /// at a folder where the file was in fact sitting. A diagnostic that cannot
    /// be argued with beats a guess about which folder was meant.
    /// </summary>
    public static string NotFoundMessage()
    {
        var probed = ProbedPaths().Take(4).Select(path => $"  {path}");

        return $"{HelperFileName} was not found. Looked in:" + Environment.NewLine +
               string.Join(Environment.NewLine, probed) + Environment.NewLine + Environment.NewLine +
               "It ships beside the app. Reinstall, or rebuild the SteamHelper project.";
    }

    public Task<PullResult> StatusAsync(CancellationToken cancellation = default) =>
        RunAsync(new[] { "status" }, null, cancellation);

    public Task<PullResult> PullAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                      CancellationToken cancellation = default) =>
        RunAsync(new[] { "pull", "--timeout", "1800" }.Concat(ids).ToArray(), progress, cancellation);

    public Task<PullResult> UnsubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                             CancellationToken cancellation = default) =>
        RunAsync(new[] { "unsubscribe", "--settle", "10" }.Concat(ids).ToArray(), progress, cancellation);

    /// <summary>
    /// Drop every subscription for the app, with the list taken from Steam
    /// rather than from the acf — the acf is a cache and can lag the client.
    /// </summary>
    public Task<PullResult> UnsubscribeAllAsync(IProgress<string>? progress,
                                                CancellationToken cancellation = default) =>
        RunAsync(new[] { "unsubscribe-all", "--settle", "10" }, progress, cancellation);

    private async Task<PullResult> RunAsync(IReadOnlyList<string> arguments, IProgress<string>? progress,
                                            CancellationToken cancellation)
    {
        if (!IsAvailable)
            return new PullResult(false, Array.Empty<PulledItem>(), new[] { NotFoundMessage() }, 0);

        var start = new ProcessStartInfo
        {
            FileName = HelperPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(_gameDir);

        var items = new List<PulledItem>();
        var errors = new List<string>();
        var dropped = new List<string>();
        uint subscribed = 0;
        uint? before = null;

        using var process = new Process { StartInfo = start };
        process.Start();

        var stderr = process.StandardError.ReadToEndAsync(cancellation);

        while (await process.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            // steam_api.dll writes its own banner to stdout ("Setting breakpad
            // minidump AppID"), so anything that is not an object is noise.
            if (!line.StartsWith('{')) continue;

            try
            {
                var message = JsonDocument.Parse(line).RootElement;
                Handle(message, items, errors, dropped, ref subscribed, progress);
                before ??= subscribed;
            }
            catch (JsonException)
            {
                // A truncated line is not worth failing the whole run over.
            }
        }

        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);

        // steam_api.dll chats on stderr on every run ("Setting breakpad minidump
        // AppID"), so stderr alone is not a failure signal. Real failures arrive
        // as an "error" event on stdout; stderr is only worth surfacing when the
        // process actually died.
        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (error.Length > 0 && process.ExitCode != 0) errors.Add(error);

        return new PullResult(process.ExitCode == 0 && errors.Count == 0, items, errors, subscribed)
        {
            Unsubscribed = dropped,
            SubscribedBefore = before ?? 0,
        };
    }

    private static void Handle(JsonElement message, List<PulledItem> items, List<string> errors,
                              List<string> dropped, ref uint subscribed, IProgress<string>? progress)
    {
        switch (Text(message, "event"))
        {
            case "ready":
                subscribed = (uint)(Number(message, "subscribed") ?? 0);
                break;

            case "subscribed":
                progress?.Report($"Subscribed to {Text(message, "id")}");
                break;

            case "progress":
                var total = Number(message, "total") ?? 0;
                var done = Number(message, "downloaded") ?? 0;
                if (total > 0)
                    progress?.Report($"Downloading {Text(message, "id")} — {done * 100 / total}%");
                break;

            case "item":
                var id = Text(message, "id") ?? string.Empty;
                var state = Text(message, "state") ?? "unknown";
                items.Add(new PulledItem(id, state, Text(message, "path") ?? string.Empty,
                                         Number(message, "size") ?? 0, Number(message, "timestamp") ?? 0));
                progress?.Report(state == "installed" ? $"Downloaded {id}" : $"{id}: {state}");
                break;

            case "unsubscribed":
                var dropped_id = Text(message, "id");
                if (dropped_id is not null) dropped.Add(dropped_id);
                progress?.Report($"Unsubscribed from {dropped_id}");
                break;

            case "error":
                errors.Add(Text(message, "message") ?? "The Steam helper failed without saying why.");
                break;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}
