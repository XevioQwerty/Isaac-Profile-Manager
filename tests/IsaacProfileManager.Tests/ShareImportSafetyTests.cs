using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// What an import must never do to a profile that already exists.
///
/// Reported from a real machine: importing a share whose mods could not be
/// downloaded left an existing profile with no links in it at all. The manifest
/// was written from "what arrived", which was nothing, and materialising an
/// empty manifest removes every junction it finds.
/// </summary>
public class ShareImportSafetyTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {directory}");
        return new WorkshopItem { Id = id, Name = directory, Directory = directory, ContentPath = content };
    }

    private sealed class FakeProcess : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    /// <summary>A helper that downloads nothing, which is the reported case.</summary>
    private sealed class DeadPull : IWorkshopPullService
    {
        public bool IsAvailable => true;
        public string? HelperPath => "fake";

        public Task<PullResult> StatusAsync(CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> PullAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                          CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(false,
                ids.Select(id => new PulledItem(id, "not-subscribed", string.Empty, 0, 0)).ToList(),
                Array.Empty<string>(), 0));

        public Task<PullResult> UnsubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                                 CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> SubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                               CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> UnsubscribeAllAsync(IProgress<string>? progress,
                                                    CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));
    }

    private static SharedProfile Share(params string[] entries) => new()
    {
        Name = "shared",
        Mods = entries.ToList(),
        WorkshopIds = entries.Select((e, i) => (e, id: $"10000000{i}"))
                             .ToDictionary(x => x.e, x => x.id, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task Run_DoesNotStripAnExistingProfileWhenNothingDownloads()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        // An existing, working profile of the same name as the incoming share.
        library.Import(Item(temp, "111", "alpha"));
        library.Import(Item(temp, "222", "beta"));
        library.SaveManifest("shared", new ProfileManifest { Mods = new List<string> { "alpha", "beta" } });
        library.Materialise("shared", library.LoadManifest("shared"));

        var profileDir = temp.Combine("sync", "shared");
        Assert.Equal(2, Directory.GetDirectories(profileDir).Length);

        // A share naming mods this machine does not have, none of which download.
        var runner = new ShareImportRunner(library, new DeadPull(), new FakeProcess());
        var share = Share("gamma", "delta");
        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "shared");

        // The existing profile must survive untouched.
        Assert.Equal(2, Directory.GetDirectories(profileDir).Length);
        Assert.Equal(new[] { "alpha", "beta" }, library.LoadManifest("shared").Mods);
        Assert.Null(report.ProfileWritten);
        Assert.Contains(report.Warnings, w => w.Contains("left alone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_DoesNotWriteAnEmptyProfileWhenNothingArrives()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var runner = new ShareImportRunner(library, new DeadPull(), new FakeProcess());
        var share = Share("gamma", "delta");
        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "brand new");

        // Writing a profile with nothing in it is not a useful outcome; it just
        // looks like the import succeeded and produced an empty modpack.
        Assert.Null(report.ProfileWritten);
        Assert.Empty(library.ListManifests());
        Assert.False(Directory.Exists(temp.Combine("sync", "brand new")));
    }

    [Fact]
    public async Task Run_StillBuildsTheProfileWhenSomeModsArrive()
    {
        // The guard must not block the normal partial case: some arrived, so the
        // profile is worth having even if it is short a mod.
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "111", "alpha"));

        var runner = new ShareImportRunner(library, new DeadPull(), new FakeProcess());
        var share = new SharedProfile
        {
            Name = "shared",
            Mods = new List<string> { "alpha", "gamma" },
            WorkshopIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gamma"] = "999" },
        };

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "shared");

        Assert.Equal("shared", report.ProfileWritten);
        Assert.Equal(new[] { "alpha" }, library.LoadManifest("shared").Mods);
    }
}
