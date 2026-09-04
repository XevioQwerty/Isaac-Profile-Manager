using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// What one device last pushed for one set: enough to compare clocks without
/// downloading the pack.
/// </summary>
public sealed class LaneManifest
{
    [JsonPropertyName("SchemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("SetName")] public string SetName { get; set; } = string.Empty;
    [JsonPropertyName("DeviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("DeviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("Clock")] public Dictionary<string, int> Clock { get; set; } = new();
    [JsonPropertyName("CapturedUtc")] public string CapturedUtc { get; set; } = string.Empty;
    [JsonPropertyName("PushedUtc")] public string PushedUtc { get; set; } = string.Empty;
    [JsonPropertyName("GameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("Build")] public GameBuild Build { get; set; }
    [JsonPropertyName("PackBytes")] public long PackBytes { get; set; }
    [JsonPropertyName("PackSha1")] public string PackSha1 { get; set; } = string.Empty;

    [JsonIgnore] public int Revision => VectorClock.Revision(Clock);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class SaveSyncException : Exception
{
    public SaveSyncException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Somewhere lanes live. Each device writes only its own lane
/// (<c>&lt;set&gt;/&lt;device&gt;</c>), so no two writers ever touch the same
/// object and the transport can never produce a conflict; reconciliation is
/// the app's job. The same interface sits over a synced folder and over a
/// Worker in front of an object store, and the app does not care which.
/// </summary>
public interface ISaveLaneStore
{
    string Description { get; }
    Task<IReadOnlyList<LaneManifest>> ListAsync(CancellationToken ct = default);
    Task PushAsync(LaneManifest manifest, string packPath, CancellationToken ct = default);
    Task PullAsync(string setName, string deviceId, string destinationPackPath, CancellationToken ct = default);
    Task DeleteAsync(string setName, string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Lanes as files under a folder a sync client carries between machines —
/// Syncthing, OneDrive, a network share. <c>&lt;root&gt;\&lt;device&gt;\&lt;set&gt;\</c>
/// holds <c>pack.ipmsave</c> and <c>manifest.json</c>. Every write goes to a
/// temp file and is moved into place, so a reader on the other machine sees
/// the old file or the new one, never a torn one; the manifest is written last
/// so it never describes a pack that has not arrived.
/// </summary>
public sealed class FolderLaneStore : ISaveLaneStore
{
    public const string ManifestFileName = "manifest.json";
    public const string PackFileName = "pack.ipmsave";

    private readonly string _root;

    public FolderLaneStore(string root) => _root = root;

    public string Root => _root;
    public string Description => $"folder {_root}";

    public string LanePath(string setName, string deviceId) =>
        Path.Combine(_root, DeviceService.SafeName(deviceId), DeviceService.SafeName(setName));

    public Task<IReadOnlyList<LaneManifest>> ListAsync(CancellationToken ct = default)
    {
        var lanes = new List<LaneManifest>();
        if (!Directory.Exists(_root)) return Task.FromResult<IReadOnlyList<LaneManifest>>(lanes);

        foreach (var device in new DirectoryInfo(_root).EnumerateDirectories())
        {
            if ((device.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var set in device.EnumerateDirectories())
            {
                var manifestPath = Path.Combine(set.FullName, ManifestFileName);
                if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(set.FullName, PackFileName))) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<LaneManifest>(File.ReadAllText(manifestPath), LaneManifest.Json);
                    if (manifest is not null && manifest.SchemaVersion == 1) lanes.Add(manifest);
                }
                catch (JsonException) { /* a manifest mid-sync; it will be whole next time */ }
                catch (IOException) { }
            }
        }

        return Task.FromResult<IReadOnlyList<LaneManifest>>(lanes);
    }

    public Task PushAsync(LaneManifest manifest, string packPath, CancellationToken ct = default)
    {
        var lane = LanePath(manifest.SetName, manifest.DeviceId);
        Directory.CreateDirectory(lane);

        var pack = Path.Combine(lane, PackFileName);
        var packTemp = pack + ".tmp";
        File.Copy(packPath, packTemp, overwrite: true);
        File.Move(packTemp, pack, overwrite: true);

        var manifestPath = Path.Combine(lane, ManifestFileName);
        var manifestTemp = manifestPath + ".tmp";
        File.WriteAllText(manifestTemp, JsonSerializer.Serialize(manifest, LaneManifest.Json), new UTF8Encoding(false));
        File.Move(manifestTemp, manifestPath, overwrite: true);

        return Task.CompletedTask;
    }

    public Task PullAsync(string setName, string deviceId, string destinationPackPath, CancellationToken ct = default)
    {
        var pack = Path.Combine(LanePath(setName, deviceId), PackFileName);
        if (!File.Exists(pack)) throw new SaveSyncException($"No lane for '{setName}' from device {deviceId} in {_root}.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPackPath))!);
        File.Copy(pack, destinationPackPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string setName, string deviceId, CancellationToken ct = default)
    {
        var lane = LanePath(setName, deviceId);
        if (Directory.Exists(lane)) Directory.Delete(lane, recursive: true);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Lanes behind the Worker in <c>cloud/save-sync-worker</c>. The sync key is
/// the bearer token and, hashed, the namespace, so one Worker can serve
/// several people who never see each other's saves. The endpoint is a setting:
/// nothing here is tied to a domain, and the folder store above is what keeps
/// working if the endpoint ever goes away.
/// </summary>
public sealed class HttpLaneStore : ISaveLaneStore
{
    public const int MaxPackBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public HttpLaneStore(string endpoint, string key, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new SaveSyncException("No sync endpoint is set.");
        if (string.IsNullOrWhiteSpace(key)) throw new SaveSyncException("No sync key is set.");

        _endpoint = endpoint.Trim().TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("IsaacProfileManager/" + AppPaths.Version);
    }

    public string Description => _endpoint;

    private string LaneUrl(string setName, string deviceId) =>
        $"{_endpoint}/v1/lanes/{Uri.EscapeDataString(setName)}/{Uri.EscapeDataString(deviceId)}";

    public async Task<IReadOnlyList<LaneManifest>> ListAsync(CancellationToken ct = default)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/v1/lanes"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var listing = JsonSerializer.Deserialize<LaneListing>(body, LaneManifest.Json);
            return listing?.Lanes ?? new List<LaneManifest>();
        }
        catch (JsonException ex)
        {
            throw new SaveSyncException($"The sync endpoint answered with something that is not a lane list: {ex.Message}");
        }
    }

    public async Task PushAsync(LaneManifest manifest, string packPath, CancellationToken ct = default)
    {
        var length = new FileInfo(packPath).Length;
        if (length > MaxPackBytes)
            throw new SaveSyncException($"The pack is {length / 1024 / 1024} MB; the sync endpoint accepts up to {MaxPackBytes / 1024 / 1024} MB.");

        // Pack first, manifest last: a listing never shows a manifest whose pack is missing.
        using (var stream = File.OpenRead(packPath))
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await Send(() => new HttpRequestMessage(HttpMethod.Put, LaneUrl(manifest.SetName, manifest.DeviceId) + "/pack") { Content = content }, ct);
        }

        var json = new StringContent(JsonSerializer.Serialize(manifest, LaneManifest.Json), Encoding.UTF8, "application/json");
        using var manifestResponse = await Send(() => new HttpRequestMessage(HttpMethod.Put, LaneUrl(manifest.SetName, manifest.DeviceId) + "/manifest") { Content = json }, ct);
    }

    public async Task PullAsync(string setName, string deviceId, string destinationPackPath, CancellationToken ct = default)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get, LaneUrl(setName, deviceId) + "/pack"), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPackPath))!);
        var temp = destinationPackPath + ".part";
        await using (var file = File.Create(temp))
            await response.Content.CopyToAsync(file, ct);
        File.Move(temp, destinationPackPath, overwrite: true);
    }

    public async Task DeleteAsync(string setName, string deviceId, CancellationToken ct = default)
    {
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Delete, LaneUrl(setName, deviceId)), ct);
    }

    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(make(), HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SaveSyncException($"Could not reach the sync endpoint {_endpoint}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SaveSyncException($"The sync endpoint {_endpoint} did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        var detail = string.Empty;
        try { detail = (await response.Content.ReadAsStringAsync(ct)).Trim(); } catch (Exception) { }
        response.Dispose();

        throw new SaveSyncException(response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "The sync endpoint rejected the sync key.",
            System.Net.HttpStatusCode.NotFound => "The sync endpoint has no such lane.",
            System.Net.HttpStatusCode.RequestEntityTooLarge => "The sync endpoint says the pack is too large.",
            _ => $"The sync endpoint answered {(int)response.StatusCode}{(detail.Length > 0 ? ": " + detail : "")}.",
        });
    }

    private sealed class LaneListing
    {
        [JsonPropertyName("lanes")] public List<LaneManifest> Lanes { get; set; } = new();
    }
}
