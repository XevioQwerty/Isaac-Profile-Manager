using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The resubscribe cycle. Every test here is about the account and the profile
/// ending up back where they started, because a subscription left behind is
/// exactly what re-lays a mod into whichever profile is junctioned.
/// </summary>
public class LibraryUpdateRunnerTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string name, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {name} v1");
        return new WorkshopItem { Id = id, Name = name, Directory = directory, ContentPath = content };
    }

    private sealed class FakeProcess : IGameProcessService
    {
        public bool Running { get; init; }
        public bool IsIsaacRunning() => Running;
    }

    /// <summary>Stands in for the 32-bit helper, and records the calls made to it.</summary>
    private sealed class FakePull : IWorkshopPullService
    {
        private readonly Func<IReadOnlyList<string>, PullResult> _pull;

        public FakePull(Func<IReadOnlyList<string>, PullResult> pull) => _pull = pull;

        public bool IsAvailable { get; init; } = true;
        public string? HelperPath => "fake";
        public List<string> Pulled { get; } = new();
        public List<string> Unsubscribed { get; } = new();

        public Task<PullResult> StatusAsync(CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> PullAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                          CancellationToken cancellation = default)
        {
            Pulled.AddRange(ids);
            return Task.FromResult(_pull(ids));
        }

        public Task<PullResult> UnsubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                                 CancellationToken cancellation = default)
        {
            Unsubscribed.AddRange(ids);
            return Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));
        }

        public Task<PullResult> SubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                               CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> UnsubscribeAllAsync(IProgress<string>? progress,
                                                    CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));
    }

    private static PulledItem Installed(string id, string path, long timestamp = 2_000_000) =>
        new(id, "installed", path, 1234, timestamp);

    [Fact]
    public async Task Run_ReplacesTheEntryAndUnsubscribesAgain()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "champions"));

        var fresh = temp.Dir("fresh");
        File.WriteAllText(Path.Combine(fresh, "main.lua"), "-- v2");

        var pull = new FakePull(_ => new PullResult(
            true, new[] { Installed("3734781489", fresh) }, Array.Empty<string>(), 0));

        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess())
            .RunAsync(new[] { "champions" });

        Assert.Equal(new[] { "champions" }, report.Updated);
        Assert.Equal(new[] { "3734781489" }, pull.Unsubscribed);
        Assert.Equal("-- v2", File.ReadAllText(Path.Combine(library.LibraryRoot, "champions", "main.lua")));
        Assert.Equal(2_000_000, library.Describe("champions", measure: false).UpstreamTimeUpdated);
    }

    [Fact]
    public async Task Run_UnsubscribesEvenWhenTheImportThrows()
    {
        // The account must not be left subscribed because a copy failed — that
        // is the state where Steam starts re-laying mods into the active profile.
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "champions"));

        var pull = new FakePull(_ => new PullResult(
            true, new[] { Installed("3734781489", temp.Combine("does-not-exist")) }, Array.Empty<string>(), 0));

        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess())
            .RunAsync(new[] { "champions" });

        Assert.Equal(new[] { "3734781489" }, pull.Unsubscribed);
        Assert.Empty(report.Updated);
        Assert.Single(report.Failed);
    }

    [Fact]
    public async Task Run_WarnsWhenSteamStillReportsASubscription()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "champions"));

        var fresh = temp.Dir("fresh");
        File.WriteAllText(Path.Combine(fresh, "main.lua"), "-- v2");

        var pull = new StubbornPull(fresh);
        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess()).RunAsync(new[] { "champions" });

        Assert.Contains(report.Warnings, w => w.Contains("still reports 1 subscription"));
    }

    private sealed class StubbornPull : IWorkshopPullService
    {
        private readonly string _path;
        public StubbornPull(string path) => _path = path;

        public bool IsAvailable => true;
        public string? HelperPath => "fake";

        public Task<PullResult> StatusAsync(CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 1));

        public Task<PullResult> PullAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                          CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, new[] { Installed("3734781489", _path) }, Array.Empty<string>(), 1));

        public Task<PullResult> UnsubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                                 CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 1));

        public Task<PullResult> SubscribeAsync(IReadOnlyList<string> ids, IProgress<string>? progress,
                                               CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 0));

        public Task<PullResult> UnsubscribeAllAsync(IProgress<string>? progress,
                                                    CancellationToken cancellation = default) =>
            Task.FromResult(new PullResult(true, Array.Empty<PulledItem>(), Array.Empty<string>(), 1));
    }

    [Fact]
    public async Task Run_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "champions"));

        var pull = new FakePull(_ => throw new InvalidOperationException("should never be reached"));

        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess { Running = true })
            .RunAsync(new[] { "champions" });

        Assert.Empty(pull.Pulled);
        Assert.Empty(pull.Unsubscribed);
        Assert.Contains(report.Warnings, w => w.Contains("Isaac is running"));
    }

    [Fact]
    public async Task Run_SkipsAHandInstalledModRatherThanSubscribingToNothing()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        temp.Dir("sync", ModLibraryService.LibraryFolderName, "hand written");

        var pull = new FakePull(_ => throw new InvalidOperationException("should never be reached"));

        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess())
            .RunAsync(new[] { "hand written" });

        Assert.Empty(pull.Pulled);
        Assert.Contains(report.Failed, f => f.Contains("no Workshop id"));
    }

    [Fact]
    public async Task Run_SaysTheHashesMovedSoCoOpPartnersUpdateToo()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "champions"));

        var fresh = temp.Dir("fresh");
        File.WriteAllText(Path.Combine(fresh, "main.lua"), "-- v2");

        var pull = new FakePull(_ => new PullResult(
            true, new[] { Installed("3734781489", fresh) }, Array.Empty<string>(), 0));

        var report = await new LibraryUpdateRunner(library, pull, new FakeProcess()).RunAsync(new[] { "champions" });

        Assert.Contains(report.Warnings, w => w.Contains("desync"));
    }
}
