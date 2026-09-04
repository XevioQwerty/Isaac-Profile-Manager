using System.Net;
using System.Text;
using System.Text.Json;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Two machines, two lanes, one folder between them. Each side has its own
/// SaveSetService over its own sync root and its own device id; the lane
/// store is the only thing they share.
/// </summary>
public class SaveSyncTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    private const string Account = "351019201";

    private sealed record Machine(SaveSetService Sets, SaveSyncService Sync, string Remote, DeviceIdentity Device);

    private static Machine Build(TempDir temp, string name, ISaveLaneStore store)
    {
        var steam = temp.Dir(name, "Steam");
        var remote = temp.Dir(name, "Steam", "userdata", Account, "250900", "remote");
        temp.File($@"{name}\Steam\userdata\{Account}\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } } }
            """);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");

        var device = new DeviceIdentity(name + "0000000000000000", name);
        var sets = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir(name, "sync"), null, temp.Dir(name, "Game"),
                                      new SaveSetOptions { DeviceId = device.Id, DeviceName = device.Name });
        return new Machine(sets, new SaveSyncService(sets, store, device), remote, device);
    }

    private static void Live(Machine m, string content)
    {
        File.WriteAllText(Path.Combine(m.Remote, "rep+persistentgamedata1.dat"), content);
    }

    // --- Folder lanes: the walk-through ---------------------------------------

    [Fact]
    public async Task DesktopPushes_LaptopSeesRemoteOnly_PullsAndIsInStep()
    {
        using var temp = new TempDir();
        var store = new FolderLaneStore(temp.Combine("shared", ".savesync"));
        var desktop = Build(temp, "desktop", store);
        var laptop = Build(temp, "laptop", store);

        Live(desktop, "evening one");
        desktop.Sets.Capture("solo", "Vanilla+");
        Assert.Equal(SyncRelation.LocalOnly, (await desktop.Sync.StatusOfAsync("solo"))!.Relation);

        var pushed = await desktop.Sync.PushAsync("solo");
        Assert.Equal("desktop", pushed.DeviceName);
        Assert.Equal(1, pushed.Revision);
        Assert.True(File.Exists(Path.Combine(store.LanePath("solo", desktop.Device.Id), FolderLaneStore.PackFileName)));
        Assert.Equal(SyncRelation.Equal, (await desktop.Sync.StatusOfAsync("solo"))!.Relation);

        var seen = (await laptop.Sync.StatusOfAsync("solo"))!;
        Assert.Equal(SyncRelation.RemoteOnly, seen.Relation);

        var pulled = await laptop.Sync.PullAsync("solo", seen.Newest!);
        Assert.Equal("solo", pulled.Name);
        Assert.Equal("evening one", File.ReadAllText(Path.Combine(laptop.Sets.SetFolder("solo"), "rep+persistentgamedata1.dat")));
        Assert.Equal(SyncRelation.Equal, (await laptop.Sync.StatusOfAsync("solo"))!.Relation);
    }

    [Fact]
    public async Task LaptopPlaysAndPushes_DesktopIsBehind_PullReplacesAndFilesHistory()
    {
        using var temp = new TempDir();
        var store = new FolderLaneStore(temp.Combine("shared", ".savesync"));
        var desktop = Build(temp, "desktop", store);
        var laptop = Build(temp, "laptop", store);

        Live(desktop, "one");
        desktop.Sets.Capture("solo", "Vanilla+");
        await desktop.Sync.PushAsync("solo");
        await laptop.Sync.PullAsync("solo", (await laptop.Sync.StatusOfAsync("solo"))!.Newest!);

        // The laptop plays and captures: its clock gains a laptop tick on top of desktop's.
        Live(laptop, "two, from the laptop");
        laptop.Sets.CaptureInto(laptop.Sets.LoadSet("solo")!);
        await laptop.Sync.PushAsync("solo");

        var status = (await desktop.Sync.StatusOfAsync("solo"))!;
        Assert.Equal(SyncRelation.RemoteAhead, status.Relation);
        Assert.Equal("laptop", status.Newest!.DeviceName);
        Assert.Equal(2, status.RemoteRevision);
        Assert.Equal(1, status.LocalRevision);

        await desktop.Sync.PullAsync("solo", status.Newest);

        Assert.Equal("two, from the laptop", File.ReadAllText(Path.Combine(desktop.Sets.SetFolder("solo"), "rep+persistentgamedata1.dat")));
        var history = Assert.Single(desktop.Sets.ListHistory("solo"));
        Assert.Equal("one", File.ReadAllText(Path.Combine(history.Path, "rep+persistentgamedata1.dat")));
        Assert.Equal(SyncRelation.Equal, (await desktop.Sync.StatusOfAsync("solo"))!.Relation);
    }

    [Fact]
    public async Task BothPlayedFromTheSamePoint_IsAFork_PullRefuses_PullAsCopyKeepsBoth()
    {
        using var temp = new TempDir();
        var store = new FolderLaneStore(temp.Combine("shared", ".savesync"));
        var desktop = Build(temp, "desktop", store);
        var laptop = Build(temp, "laptop", store);

        Live(desktop, "one");
        desktop.Sets.Capture("solo", "Vanilla+");
        await desktop.Sync.PushAsync("solo");
        await laptop.Sync.PullAsync("solo", (await laptop.Sync.StatusOfAsync("solo"))!.Newest!);

        Live(desktop, "desktop's evening");
        desktop.Sets.CaptureInto(desktop.Sets.LoadSet("solo")!);
        Live(laptop, "laptop's evening");
        laptop.Sets.CaptureInto(laptop.Sets.LoadSet("solo")!);
        await laptop.Sync.PushAsync("solo");

        var status = (await desktop.Sync.StatusOfAsync("solo"))!;
        Assert.Equal(SyncRelation.Fork, status.Relation);

        await Assert.ThrowsAsync<SaveSyncException>(() => desktop.Sync.PullAsync("solo", status.Newest!));
        Assert.Equal("desktop's evening", File.ReadAllText(Path.Combine(desktop.Sets.SetFolder("solo"), "rep+persistentgamedata1.dat")));

        var copy = await desktop.Sync.PullAsCopyAsync("solo", status.Newest!);
        Assert.Equal("solo (from laptop)", copy.Name);
        Assert.Equal("laptop's evening", File.ReadAllText(Path.Combine(desktop.Sets.SetFolder(copy.Name), "rep+persistentgamedata1.dat")));
        Assert.Equal("desktop's evening", File.ReadAllText(Path.Combine(desktop.Sets.SetFolder("solo"), "rep+persistentgamedata1.dat")));
    }

    [Fact]
    public async Task ACaptureAfterPushing_IsLocalAhead()
    {
        using var temp = new TempDir();
        var store = new FolderLaneStore(temp.Combine("shared", ".savesync"));
        var desktop = Build(temp, "desktop", store);

        Live(desktop, "one");
        desktop.Sets.Capture("solo", "Vanilla+");
        await desktop.Sync.PushAsync("solo");
        Live(desktop, "two");
        desktop.Sets.CaptureInto(desktop.Sets.LoadSet("solo")!);

        Assert.Equal(SyncRelation.LocalAhead, (await desktop.Sync.StatusOfAsync("solo"))!.Relation);
    }

    [Fact]
    public async Task FolderLanes_LeaveNoTornFiles_AndIgnoreAHalfWrittenManifest()
    {
        using var temp = new TempDir();
        var root = temp.Combine("shared", ".savesync");
        var store = new FolderLaneStore(root);
        var desktop = Build(temp, "desktop", store);
        Live(desktop, "one");
        desktop.Sets.Capture("solo", "Vanilla+");
        await desktop.Sync.PushAsync("solo");

        var lane = store.LanePath("solo", desktop.Device.Id);
        Assert.Empty(Directory.GetFiles(lane, "*.tmp"));

        // A sync client mid-write: manifest present but unparseable. Skipped, not fatal.
        var broken = Path.Combine(root, "otherdevice", "solo");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, FolderLaneStore.PackFileName), "PK");
        File.WriteAllText(Path.Combine(broken, FolderLaneStore.ManifestFileName), "{ \"SetName\": ");

        var lanes = await store.ListAsync();
        Assert.Single(lanes);
    }

    [Fact]
    public async Task ADamagedPack_IsRefused_BeforeTouchingTheSet()
    {
        using var temp = new TempDir();
        var store = new FolderLaneStore(temp.Combine("shared", ".savesync"));
        var desktop = Build(temp, "desktop", store);
        var laptop = Build(temp, "laptop", store);
        Live(desktop, "one");
        desktop.Sets.Capture("solo", "Vanilla+");
        var manifest = await desktop.Sync.PushAsync("solo");

        File.WriteAllText(Path.Combine(store.LanePath("solo", desktop.Device.Id), FolderLaneStore.PackFileName), "not the pack");

        await Assert.ThrowsAsync<SaveSyncException>(() => laptop.Sync.PullAsync("solo", manifest));
        Assert.DoesNotContain("solo", laptop.Sets.ListSets());
    }

    // --- The HTTP store, against a fake endpoint --------------------------------

    /// <summary>Just enough of the Worker to prove the client speaks its protocol.</summary>
    private sealed class FakeWorker : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = new();
        public string ExpectedKey { get; init; } = "secret";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Headers.Authorization?.Parameter != ExpectedKey)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/v1/lanes")
            {
                var manifests = Objects.Where(o => o.Key.EndsWith("/manifest")).Select(o => Encoding.UTF8.GetString(o.Value));
                return Json("{\"lanes\":[" + string.Join(",", manifests) + "]}");
            }
            if (request.Method == HttpMethod.Put)
            {
                Objects[path] = await request.Content!.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && Objects.TryGetValue(path, out var bytes))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (request.Method == HttpMethod.Delete)
            {
                foreach (var key in Objects.Keys.Where(k => k.StartsWith(path + "/")).ToList()) Objects.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task HttpStore_PushesPackThenManifest_AndPullsItBack()
    {
        using var temp = new TempDir();
        var worker = new FakeWorker();
        var store = new HttpLaneStore("https://ipm.example.workers.dev/", "secret", worker);
        var desktop = Build(temp, "desktop", store);
        var laptop = Build(temp, "laptop", store);

        Live(desktop, "cloud one");
        desktop.Sets.Capture("co op", "Online+");
        await desktop.Sync.PushAsync("co op");

        Assert.Equal(new[] { "PUT /v1/lanes/co%20op/desktop0000000000000000/pack", "PUT /v1/lanes/co%20op/desktop0000000000000000/manifest" },
                     worker.Requests.Where(r => r.StartsWith("PUT")));

        var status = (await laptop.Sync.StatusOfAsync("co op"))!;
        Assert.Equal(SyncRelation.RemoteOnly, status.Relation);
        var pulled = await laptop.Sync.PullAsync("co op", status.Newest!);
        Assert.Equal("cloud one", File.ReadAllText(Path.Combine(laptop.Sets.SetFolder(pulled.Name), "rep+persistentgamedata1.dat")));
    }

    [Fact]
    public async Task HttpStore_ReportsAWrongKeyPlainly()
    {
        var store = new HttpLaneStore("https://ipm.example.workers.dev", "wrong", new FakeWorker());
        var ex = await Assert.ThrowsAsync<SaveSyncException>(() => store.ListAsync());
        Assert.Contains("sync key", ex.Message);
    }

    [Fact]
    public void HttpStore_RefusesToStartWithoutEndpointOrKey()
    {
        Assert.Throws<SaveSyncException>(() => new HttpLaneStore("", "k"));
        Assert.Throws<SaveSyncException>(() => new HttpLaneStore("https://x", ""));
    }

    [Fact]
    public void LaneManifest_RoundTripsThroughJson()
    {
        var manifest = new LaneManifest { SetName = "s", DeviceId = "d", Clock = new() { ["d"] = 3 }, Build = GameBuild.Repentogon };
        var back = JsonSerializer.Deserialize<LaneManifest>(JsonSerializer.Serialize(manifest, LaneManifest.Json), LaneManifest.Json)!;
        Assert.Equal(3, back.Revision);
        Assert.Equal(GameBuild.Repentogon, back.Build);
    }
}
