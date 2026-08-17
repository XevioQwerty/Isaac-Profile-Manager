using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class LibraryHashServiceTests
{
    private static (ModLibraryService Library, LibraryHashService Hashes) Build(TempDir temp, string syncFolder = "sync")
    {
        var library = new ModLibraryService(new JunctionService(), temp.Dir(syncFolder));
        return (library, new LibraryHashService(library));
    }

    private static void GiveMod(TempDir temp, string sync, string entry, string content, string file = "main.lua")
    {
        temp.File($@"{sync}\.library\{entry}\{file}", content);
    }

    [Fact]
    public void ComputeHash_IsStableForTheSameContent()
    {
        using var temp = new TempDir();
        var (_, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- alpha");

        Assert.Equal(hashes.ComputeHash("alpha"), hashes.ComputeHash("alpha"));
    }

    [Fact]
    public void ComputeHash_ChangesWhenAnyFileContentChanges()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- alpha");
        var before = hashes.ComputeHash("alpha");

        File.WriteAllText(Path.Combine(library.LibraryRoot, "alpha", "main.lua"), "-- alpha, edited");

        Assert.NotEqual(before, hashes.ComputeHash("alpha"));
    }

    [Fact]
    public void ComputeHash_ChangesWhenAFileIsAddedOrRenamed()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- alpha");
        var before = hashes.ComputeHash("alpha");

        File.WriteAllText(Path.Combine(library.LibraryRoot, "alpha", "extra.lua"), "");
        var withExtra = hashes.ComputeHash("alpha");
        Assert.NotEqual(before, withExtra);

        File.Move(Path.Combine(library.LibraryRoot, "alpha", "extra.lua"),
                  Path.Combine(library.LibraryRoot, "alpha", "renamed.lua"));
        Assert.NotEqual(withExtra, hashes.ComputeHash("alpha"));
    }

    [Fact]
    public void ComputeHash_IgnoresTimestampsAndWhereTheLibraryLives()
    {
        using var temp = new TempDir();
        var (libraryA, hashesA) = Build(temp, "machine-a");
        var (libraryB, hashesB) = Build(temp, "machine-b");

        GiveMod(temp, "machine-a", "alpha", "-- identical");
        GiveMod(temp, "machine-b", "alpha", "-- identical");
        File.SetLastWriteTimeUtc(Path.Combine(libraryB.LibraryRoot, "alpha", "main.lua"), new DateTime(2001, 1, 1));

        // Two people who synced the same mod must agree, whatever their paths.
        Assert.Equal(hashesA.ComputeHash("alpha"), hashesB.ComputeHash("alpha"));
        Assert.NotEqual(libraryA.LibraryRoot, libraryB.LibraryRoot);
    }

    [Fact]
    public void ComputeHash_DistinguishesSameNameDifferentContent()
    {
        using var temp = new TempDir();
        var (_, hashesA) = Build(temp, "a");
        var (_, hashesB) = Build(temp, "b");
        GiveMod(temp, "a", "eid", "-- version 1");
        GiveMod(temp, "b", "eid", "-- version 2");

        // The exact case a folder listing cannot see.
        Assert.NotEqual(hashesA.ComputeHash("eid"), hashesB.ComputeHash("eid"));
    }

    [Fact]
    public void RecordAll_ThenVerifyAll_ReportsUnchanged()
    {
        using var temp = new TempDir();
        var (_, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        GiveMod(temp, "sync", "beta", "-- b");

        hashes.RecordAll();
        var verified = hashes.VerifyAll();

        Assert.Equal(2, verified.Count);
        Assert.All(verified, v => Assert.True(v.Matches));
    }

    [Fact]
    public void VerifyAll_FlagsAModThatChangedSinceItWasRecorded()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        GiveMod(temp, "sync", "beta", "-- b");
        hashes.RecordAll();

        File.WriteAllText(Path.Combine(library.LibraryRoot, "beta", "main.lua"), "-- tampered");
        var verified = hashes.VerifyAll();

        Assert.True(verified.Single(v => v.Entry == "alpha").Matches);
        var beta = verified.Single(v => v.Entry == "beta");
        Assert.False(beta.Matches);
        Assert.Equal("CHANGED", beta.StatusText);
    }

    [Fact]
    public void RecordAll_SkipsModsWhoseStampIsUnchanged()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        hashes.RecordAll();

        // Reading every byte of a real library takes minutes, so a second pass
        // must not re-read anything that has not changed.
        var touched = new List<string>();
        var results = hashes.RecordAll(new Progress<string>(e => { lock (touched) touched.Add(e); }));

        Assert.Single(results);
        Assert.True(results[0].Matches);
        Assert.Empty(touched);
    }

    [Fact]
    public void RecordAll_RehashesWhenTheStampChanges()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        hashes.RecordAll();

        var file = Path.Combine(library.LibraryRoot, "alpha", "main.lua");
        File.WriteAllText(file, "-- a much longer body, different size");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        var result = hashes.RecordAll().Single();

        Assert.False(result.Matches);
        Assert.Equal(hashes.ComputeHash("alpha"), result.Actual);
    }

    [Fact]
    public void VerifyAll_IgnoresTheStampAndRereadsEverything()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- aaaa");
        hashes.RecordAll();

        // Same length and same timestamp: the stamp cannot see this, but the
        // "prove it" pass must.
        var file = Path.Combine(library.LibraryRoot, "alpha", "main.lua");
        var when = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "-- bbbb");
        File.SetLastWriteTimeUtc(file, when);

        Assert.False(hashes.VerifyAll().Single().Matches);
    }

    [Fact]
    public void VerifyAll_MarksAModThatWasNeverRecorded()
    {
        using var temp = new TempDir();
        var (_, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        hashes.RecordAll();
        GiveMod(temp, "sync", "newcomer", "-- new");

        var newcomer = hashes.VerifyAll().Single(v => v.Entry == "newcomer");

        Assert.False(newcomer.IsRecorded);
        Assert.Equal("not recorded", newcomer.StatusText);
    }

    // --- Sharing ------------------------------------------------------------

    [Fact]
    public void Export_CarriesOnlyNamesAndHashes_NoLocalPaths()
    {
        using var temp = new TempDir();
        var (library, hashes) = Build(temp);
        GiveMod(temp, "sync", "alpha", "-- a");
        GiveMod(temp, "sync", "beta", "-- b");
        hashes.RecordAll();

        var manifest = new ProfileManifest { Mods = { "alpha", "beta" }, Notes = "friday group" };
        var export = hashes.Export("coop", manifest);
        var path = temp.Combine("coop.ipmprofile.json");
        hashes.WriteExport(export, path);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(":\\", text);
        Assert.Contains("alpha", text);

        var read = LibraryHashService.ReadExport(path);
        Assert.Equal("coop", read.Name);
        Assert.Equal(new[] { "alpha", "beta" }, read.Mods);
        Assert.Equal(2, read.Hashes.Count);
    }

    [Fact]
    public void Compare_IdenticalOnBothSides()
    {
        using var temp = new TempDir();
        var (_, mine) = Build(temp, "mine");
        var (_, theirs) = Build(temp, "theirs");
        GiveMod(temp, "mine", "alpha", "-- same");
        GiveMod(temp, "theirs", "alpha", "-- same");
        mine.RecordAll();
        theirs.RecordAll();

        var manifest = new ProfileManifest { Mods = { "alpha" } };
        var diff = mine.Compare(manifest, theirs.Export("coop", manifest));

        Assert.True(diff.IsIdentical);
        Assert.Contains("Identical", diff.Summary);
    }

    [Fact]
    public void Compare_CatchesSameNameDifferentContents()
    {
        using var temp = new TempDir();
        var (_, mine) = Build(temp, "mine");
        var (_, theirs) = Build(temp, "theirs");
        GiveMod(temp, "mine", "eid", "-- version 1");
        GiveMod(temp, "theirs", "eid", "-- version 2");
        mine.RecordAll();
        theirs.RecordAll();

        var manifest = new ProfileManifest { Mods = { "eid" } };
        var diff = mine.Compare(manifest, theirs.Export("coop", manifest));

        Assert.False(diff.IsIdentical);
        Assert.Equal(ProfileDiffKind.ContentDiffers, diff.Entries.Single().Kind);
    }

    [Fact]
    public void Compare_ReportsWhoIsMissingWhat()
    {
        using var temp = new TempDir();
        var (_, mine) = Build(temp, "mine");
        var (_, theirs) = Build(temp, "theirs");
        GiveMod(temp, "mine", "alpha", "-- a");
        GiveMod(temp, "mine", "mine-only", "-- m");
        GiveMod(temp, "theirs", "alpha", "-- a");
        GiveMod(temp, "theirs", "theirs-only", "-- t");
        mine.RecordAll();
        theirs.RecordAll();

        var diff = mine.Compare(
            new ProfileManifest { Mods = { "alpha", "mine-only" } },
            theirs.Export("coop", new ProfileManifest { Mods = { "alpha", "theirs-only" } }));

        Assert.Equal(ProfileDiffKind.Identical, diff.Entries.Single(e => e.Entry == "alpha").Kind);
        Assert.Equal(ProfileDiffKind.OnlyMine, diff.Entries.Single(e => e.Entry == "mine-only").Kind);
        Assert.Equal(ProfileDiffKind.OnlyTheirs, diff.Entries.Single(e => e.Entry == "theirs-only").Kind);
        Assert.Equal(3, diff.Entries.Count);
        // The shared mod is not a problem; the two one-sided ones are.
        Assert.Equal(2, diff.Problems.Count());
        Assert.False(diff.IsIdentical);
    }

    [Fact]
    public void Compare_MarksUnverifiedWhenEitherSideHasNoHash()
    {
        using var temp = new TempDir();
        var (_, mine) = Build(temp, "mine");
        var (_, theirs) = Build(temp, "theirs");
        GiveMod(temp, "mine", "alpha", "-- a");
        GiveMod(temp, "theirs", "alpha", "-- a");
        mine.RecordAll();
        // theirs never recorded hashes, so there is nothing to compare against.

        var manifest = new ProfileManifest { Mods = { "alpha" } };
        var diff = mine.Compare(manifest, theirs.Export("coop", manifest));

        Assert.Equal(ProfileDiffKind.Unverified, diff.Entries.Single().Kind);
        Assert.False(diff.IsIdentical);
    }

    [Fact]
    public void ReadExport_RefusesAnUnknownSchema()
    {
        using var temp = new TempDir();
        var path = temp.File("bad.json", """{"SchemaVersion": 99, "Name": "x"}""");

        Assert.Throws<ConfigSchemaMismatchException>(() => LibraryHashService.ReadExport(path));
    }
}
