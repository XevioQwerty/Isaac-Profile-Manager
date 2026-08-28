using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The update path: asking the Workshop what changed, and replacing a library
/// entry with a newer revision without losing the old one.
/// </summary>
public class LibraryUpdateTests
{
    private static ModLibraryService Build(TempDir temp) => new(new JunctionService(), temp.Dir("sync"));

    private static WorkshopItem Item(TempDir temp, string id, string name, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {name} v1");
        return new WorkshopItem
        {
            Id = id, Name = name, Directory = directory,
            Description = $"about {name}", ContentPath = content,
        };
    }

    /// <summary>A stand-in for Steam's answer, so no test touches the network.</summary>
    private sealed class FakeChecker : IWorkshopUpdateChecker
    {
        private readonly Dictionary<string, WorkshopFileDetails> _details;
        public FakeChecker(params WorkshopFileDetails[] details) =>
            _details = details.ToDictionary(d => d.Id, StringComparer.Ordinal);

        public List<string> Asked { get; } = new();

        public Task<IReadOnlyDictionary<string, WorkshopFileDetails>> FetchAsync(
            IReadOnlyList<string> ids, CancellationToken cancellation = default)
        {
            Asked.AddRange(ids);
            IReadOnlyDictionary<string, WorkshopFileDetails> found =
                _details.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    // --- Parsing Steam's answer --------------------------------------------

    [Fact]
    public void Parse_ReadsTimeUpdatedAndTheStringEncodedFileSize()
    {
        // file_size comes back as a string and time_updated as a number. Both
        // shapes are real, from the live endpoint.
        const string json = """
        {"response":{"result":1,"resultcount":1,"publishedfiledetails":[
          {"publishedfileid":"836319872","result":1,"title":"External Item Descriptions",
           "file_size":"27355052","time_updated":1784752126}]}}
        """;

        var details = Assert.Single(WorkshopUpdateService.Parse(json));

        Assert.Equal("836319872", details.Id);
        Assert.True(details.Available);
        Assert.Equal(27355052, details.FileSize);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784752126), details.UpdatedUtc);
    }

    [Fact]
    public void Parse_TreatsANonSuccessResultAsUnavailableRatherThanUnchanged()
    {
        // A delisted mod must not read as "up to date" — that would quietly
        // pretend the library copy is current forever.
        const string json = """
        {"response":{"publishedfiledetails":[{"publishedfileid":"1","result":9}]}}
        """;

        Assert.False(Assert.Single(WorkshopUpdateService.Parse(json)).Available);
    }

    // --- Deciding what is stale --------------------------------------------

    [Fact]
    public async Task Check_FlagsAnEntryWhoseWorkshopCopyIsNewerThanOurs()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));
        library.RecordUpstreamStamp("resprited champions", 1_000_000, 500);

        var service = new LibraryUpdateService(library,
            new FakeChecker(new WorkshopFileDetails("3734781489", true, "Resprited Champions", 2_000_000, 600)));

        var status = Assert.Single(await service.CheckAsync());

        Assert.Equal(UpdateState.UpdateAvailable, status.State);
        Assert.False(status.BaselineIsImportDate);
        Assert.Equal("3734781489", status.WorkshopId);
    }

    [Fact]
    public async Task Check_CallsAnOlderWorkshopRevisionUpToDate()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));
        library.RecordUpstreamStamp("resprited champions", 2_000_000, 500);

        var service = new LibraryUpdateService(library,
            new FakeChecker(new WorkshopFileDetails("3734781489", true, "Resprited Champions", 1_000_000, 500)));

        Assert.Equal(UpdateState.UpToDate, Assert.Single(await service.CheckAsync()).State);
    }

    [Fact]
    public async Task Check_FallsBackToTheImportDateAndSaysSo()
    {
        // Entries imported before revisions were recorded still have to be
        // answerable, but the answer is weaker: Steam may have downloaded the
        // content well before the import, so the flag has to travel with it.
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));

        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var service = new LibraryUpdateService(library,
            new FakeChecker(new WorkshopFileDetails("3734781489", true, "Resprited Champions", tomorrow, 600)));

        var status = Assert.Single(await service.CheckAsync());

        Assert.Equal(UpdateState.UpdateAvailable, status.State);
        Assert.True(status.BaselineIsImportDate);
    }

    [Fact]
    public async Task Check_NeverAsksSteamAboutAHandInstalledMod()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        temp.Dir("sync", ModLibraryService.LibraryFolderName, "my hand written mod");

        var checker = new FakeChecker();
        var status = Assert.Single(await new LibraryUpdateService(library, checker).CheckAsync());

        Assert.Equal(UpdateState.NoWorkshopOrigin, status.State);
        Assert.Empty(checker.Asked);
    }

    // --- Replacing the content ---------------------------------------------

    [Fact]
    public void UpdateFromContent_DropsFilesTheAuthorDeletedUpstream()
    {
        // The reason this is not a copy-over-the-top: a merge leaves removed
        // files behind, and the resulting bytes match nobody else's install.
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));
        temp.File(@"sync\.library\resprited champions\gone-in-v2.lua", "old");

        var newer = temp.Dir("newer");
        File.WriteAllText(Path.Combine(newer, "main.lua"), "-- v2");

        library.UpdateFromContent("resprited champions", newer, 2_000_000, 1234);

        var entry = Path.Combine(library.LibraryRoot, "resprited champions");
        Assert.Equal("-- v2", File.ReadAllText(Path.Combine(entry, "main.lua")));
        Assert.False(File.Exists(Path.Combine(entry, "gone-in-v2.lua")));
    }

    [Fact]
    public void UpdateFromContent_KeepsTheOldRevisionInABackup()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));

        var newer = temp.Dir("newer");
        File.WriteAllText(Path.Combine(newer, "main.lua"), "-- v2");

        var backup = library.UpdateFromContent("resprited champions", newer, 2_000_000, 1234);

        Assert.NotNull(backup);
        Assert.Equal("-- Resprited Champions v1", File.ReadAllText(Path.Combine(backup!, "main.lua")));
    }

    [Fact]
    public void UpdateFromContent_RecordsTheRevisionSoTheNextCheckIsExact()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));

        var newer = temp.Dir("newer");
        File.WriteAllText(Path.Combine(newer, "main.lua"), "-- v2");
        library.UpdateFromContent("resprited champions", newer, 1787287627, 1916515);

        var info = library.Describe("resprited champions", measure: false);

        Assert.Equal(1787287627, info.UpstreamTimeUpdated);
        Assert.Equal("3734781489", info.WorkshopId);   // the import metadata survives
    }

    [Fact]
    public void Import_DoesNotWipeARecordedRevision()
    {
        using var temp = new TempDir();
        var library = Build(temp);
        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"));
        library.RecordUpstreamStamp("resprited champions", 1787287627, 1916515);

        library.Import(Item(temp, "3734781489", "Resprited Champions", "resprited champions"), overwrite: true);

        Assert.Equal(1787287627, library.Describe("resprited champions", measure: false).UpstreamTimeUpdated);
    }
}
