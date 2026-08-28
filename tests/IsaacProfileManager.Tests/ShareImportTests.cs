using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Rebuilding someone else's set from a code: what gets skipped, what gets
/// fetched, and what the account looks like afterwards.
/// </summary>
public class ShareImportTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string name, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {name}");
        return new WorkshopItem { Id = id, Name = name, Directory = directory, ContentPath = content };
    }

    private sealed class FakeProcess : IGameProcessService
    {
        public bool Running { get; init; }
        public bool IsIsaacRunning() => Running;
    }

    private sealed class FakePull : IWorkshopPullService
    {
        private readonly Func<IReadOnlyList<string>, PullResult> _pull;
        public FakePull(Func<IReadOnlyList<string>, PullResult> pull) => _pull = pull;

        public bool IsAvailable => true;
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

    private static SharedProfile Share(params (string Entry, string? Id, string? Hash)[] mods) => new()
    {
        Name = "theirs",
        Mods = mods.Select(m => m.Entry).ToList(),
        WorkshopIds = mods.Where(m => m.Id is not null).ToDictionary(m => m.Entry, m => m.Id!, StringComparer.OrdinalIgnoreCase),
        Hashes = mods.Where(m => m.Hash is not null).ToDictionary(m => m.Entry, m => m.Hash!, StringComparer.OrdinalIgnoreCase),
    };

    // --- Planning -----------------------------------------------------------

    [Fact]
    public void Plan_SkipsAModWeAlreadyHaveWithAMatchingHash()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "836319872", "EID", "eid"));

        var hashes = new LibraryHashService(library);
        hashes.RecordAll();
        var mine = hashes.LoadHashes()["eid"];

        var runner = new ShareImportRunner(library, new FakePull(_ => throw new InvalidOperationException()), new FakeProcess());
        var plan = runner.Plan(Share(("eid", "836319872", mine)));

        Assert.Equal(ShareItemAction.AlreadyMatches, Assert.Single(plan.Items).Action);
        Assert.Empty(plan.ToFetch);
    }

    [Fact]
    public void Plan_RefetchesAModWhoseBytesDiffer()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "836319872", "EID", "eid"));
        new LibraryHashService(library).RecordAll();

        var runner = new ShareImportRunner(library, new FakePull(_ => throw new InvalidOperationException()), new FakeProcess());
        var plan = runner.Plan(Share(("eid", "836319872", new string('f', 64))));

        Assert.Equal(ShareItemAction.Differs, Assert.Single(plan.Items).Action);
        Assert.Single(plan.ToFetch);
    }

    [Fact]
    public void Plan_FlagsAModWithNoWorkshopIdAsSomethingWeCannotFetch()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var runner = new ShareImportRunner(library, new FakePull(_ => throw new InvalidOperationException()), new FakeProcess());
        var plan = runner.Plan(Share(("their hand written mod", null, null)));

        Assert.Equal(ShareItemAction.Unfetchable, Assert.Single(plan.Items).Action);
        Assert.Empty(plan.ToFetch);
    }

    // --- Running ------------------------------------------------------------

    [Fact]
    public async Task Run_DownloadsWhatIsMissingAndUnsubscribesAfterwards()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var downloaded = temp.Dir("downloaded");
        File.WriteAllText(Path.Combine(downloaded, "main.lua"), "-- theirs");
        File.WriteAllText(Path.Combine(downloaded, "metadata.xml"),
            "<metadata><name>External Item Descriptions</name><directory>eid</directory></metadata>");

        var pull = new FakePull(_ => new PullResult(
            true, new[] { new PulledItem("836319872", "installed", downloaded, 99, 1787287627) },
            Array.Empty<string>(), 0));

        var runner = new ShareImportRunner(library, pull, new FakeProcess());
        var share = Share(("eid", "836319872", null));

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: null);

        Assert.Equal(new[] { "eid" }, report.Installed);
        Assert.Equal(new[] { "836319872" }, pull.Unsubscribed);
        Assert.Equal("-- theirs", File.ReadAllText(Path.Combine(library.LibraryRoot, "eid", "main.lua")));

        // The id and the revision must be recorded, or this mod silently drops
        // out of every future update check.
        var info = library.Describe("eid", measure: false);
        Assert.Equal("836319872", info.WorkshopId);
        Assert.Equal(1787287627, info.UpstreamTimeUpdated);
        Assert.Equal("External Item Descriptions", info.Name);
    }

    [Fact]
    public async Task Run_UsesTheSendersEntryNameNotTheOneDerivedLocally()
    {
        // The manifest refers to the sender's names. Deriving "eid" from
        // metadata.xml when they called it "eid_836319872" would leave the
        // profile pointing at a folder that does not exist.
        using var temp = new TempDir();
        var library = Build(temp);

        var downloaded = temp.Dir("downloaded");
        File.WriteAllText(Path.Combine(downloaded, "main.lua"), "-- theirs");
        File.WriteAllText(Path.Combine(downloaded, "metadata.xml"),
            "<metadata><name>EID</name><directory>eid</directory></metadata>");

        var pull = new FakePull(_ => new PullResult(
            true, new[] { new PulledItem("836319872", "installed", downloaded, 99, 1) }, Array.Empty<string>(), 0));

        var runner = new ShareImportRunner(library, pull, new FakeProcess());
        var share = Share(("eid_836319872", "836319872", null));

        await runner.RunAsync(share, runner.Plan(share), profileName: null);

        Assert.Contains("eid_836319872", library.ListEntries());
        Assert.DoesNotContain("eid", library.ListEntries());
    }

    [Fact]
    public async Task Run_BuildsTheProfileFromWhatActuallyArrived()
    {
        using var temp = new TempDir();
        var library = Build(temp);

        var downloaded = temp.Dir("downloaded");
        File.WriteAllText(Path.Combine(downloaded, "main.lua"), "-- theirs");

        // One arrives, one is not a Workshop item and cannot.
        var pull = new FakePull(_ => new PullResult(
            true, new[] { new PulledItem("836319872", "installed", downloaded, 99, 1) }, Array.Empty<string>(), 0));

        var runner = new ShareImportRunner(library, pull, new FakeProcess());
        var share = Share(("eid", "836319872", null), ("their local mod", null, null));

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "theirs");

        Assert.Equal("theirs", report.ProfileWritten);
        Assert.Equal(new[] { "eid" }, library.LoadManifest("theirs").Mods);
        Assert.Contains(report.Warnings, w => w.Contains("not Workshop items"));
        Assert.Contains(report.Warnings, w => w.Contains("1 fewer mod"));
    }

    [Fact]
    public async Task Run_ReportsWhenDownloadedBytesDoNotMatchTheSenders()
    {
        // The Workshop moved on since they made the code. Silence here would
        // mean two people believing they match when they do not.
        using var temp = new TempDir();
        var library = Build(temp);

        var downloaded = temp.Dir("downloaded");
        File.WriteAllText(Path.Combine(downloaded, "main.lua"), "-- a newer version");

        var pull = new FakePull(_ => new PullResult(
            true, new[] { new PulledItem("836319872", "installed", downloaded, 99, 1) }, Array.Empty<string>(), 0));

        var runner = new ShareImportRunner(library, pull, new FakeProcess());
        var share = Share(("eid", "836319872", new string('c', 64)));

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: null);

        Assert.Equal(new[] { "eid" }, report.HashMismatches);
        Assert.Contains(report.Warnings, w => w.Contains("do not match the sender's hashes"));
    }

    [Fact]
    public async Task Run_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        var pull = new FakePull(_ => throw new InvalidOperationException("should never be reached"));

        var runner = new ShareImportRunner(library, pull, new FakeProcess { Running = true });
        var share = Share(("eid", "836319872", null));

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "theirs");

        Assert.Empty(pull.Pulled);
        Assert.Contains(report.Warnings, w => w.Contains("Isaac is running"));
    }

    [Fact]
    public async Task Run_DoesNotSubscribeAtAllWhenEverythingIsAlreadyHere()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "836319872", "EID", "eid"));

        var hashes = new LibraryHashService(library);
        hashes.RecordAll();

        var pull = new FakePull(_ => throw new InvalidOperationException("should never be reached"));
        var runner = new ShareImportRunner(library, pull, new FakeProcess());
        var share = Share(("eid", "836319872", hashes.LoadHashes()["eid"]));

        var report = await runner.RunAsync(share, runner.Plan(share), profileName: "theirs");

        Assert.Empty(pull.Pulled);
        Assert.Empty(pull.Unsubscribed);
        Assert.Equal(new[] { "eid" }, library.LoadManifest("theirs").Mods);
    }
}
