using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The real client against a real deployed Worker. Runs only when
/// <c>IPM_SYNC_ENDPOINT</c> is set (Test.ps1 -Live sets it from the config),
/// uses a throwaway key and a throwaway set name, and deletes what it wrote.
/// </summary>
public class SaveSyncLiveTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    [Fact]
    public async Task PushListPullDelete_AgainstTheDeployedWorker()
    {
        var endpoint = Environment.GetEnvironmentVariable("IPM_SYNC_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;   // not a live run

        using var temp = new TempDir();
        var steam = temp.Dir("Steam");
        var remote = temp.Dir("Steam", "userdata", "1", "250900", "remote");
        temp.File(@"Steam\userdata\1\7\remote\sharedconfig.vdf", """
            "UserRoamingConfigStore" { "Software" { "Valve" { "Steam" { "apps" { "250900" { "cloudenabled" "0" } } } } } }
            """);
        File.WriteAllText(Path.Combine(remote, "remotecache.vdf"), "\"250900\"\n{\n}\n");
        File.WriteAllText(Path.Combine(remote, "rep+persistentgamedata1.dat"), "live test " + Guid.NewGuid());

        var key = "ipm-live-test-" + Guid.NewGuid().ToString("N");
        var store = new HttpLaneStore(endpoint, key);
        var device = new DeviceIdentity("livetest00000000", "livetest");
        var sets = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync"), null, temp.Dir("Game"),
                                      new SaveSetOptions { DeviceId = device.Id, DeviceName = device.Name });
        var sync = new SaveSyncService(sets, store, device);
        var setName = "ipm live test";

        try
        {
            sets.Capture(setName, "Vanilla+");
            var manifest = await sync.PushAsync(setName);
            Assert.Equal(1, manifest.Revision);

            var lanes = await store.ListAsync();
            var lane = Assert.Single(lanes);
            Assert.Equal(setName, lane.SetName);
            Assert.Equal(manifest.PackSha1, lane.PackSha1);

            // A second, empty machine sees it and can pull it.
            var other = new SaveSetService(new FakeProcessService(), new SteamCloudService(steam), temp.Dir("sync2"), null, temp.Dir("Game2"),
                                           new SaveSetOptions { DeviceId = "other00000000000", DeviceName = "other" });
            var otherSync = new SaveSyncService(other, store, new DeviceIdentity("other00000000000", "other"));
            var status = (await otherSync.StatusOfAsync(setName))!;
            Assert.Equal(SyncRelation.RemoteOnly, status.Relation);
            var pulled = await otherSync.PullAsync(setName, status.Newest!);
            Assert.Equal(File.ReadAllText(Path.Combine(remote, "rep+persistentgamedata1.dat")),
                         File.ReadAllText(Path.Combine(other.SetFolder(pulled.Name), "rep+persistentgamedata1.dat")));
        }
        finally
        {
            await store.DeleteAsync(setName, device.Id);
        }

        Assert.Empty(await store.ListAsync());
    }
}
