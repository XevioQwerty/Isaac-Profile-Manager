using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The patch engine, which is the only thing in this app that writes into the
/// game directory. The round-trip test is the important one: apply then revert
/// has to leave the folder byte-identical, or the feature is a way to break an
/// install.
/// </summary>
public class PatchServiceTests
{
    private sealed class FakeProcessService : IGameProcessService
    {
        public bool Running { get; set; }
        public bool IsIsaacRunning() => Running;
    }

    private static (PatchService Service, FakeProcessService Process, string Game, string Sync) Build(TempDir temp)
    {
        var process = new FakeProcessService();
        var sync = temp.Dir("sync");
        var game = temp.Dir("game");
        return (new PatchService(process, sync), process, game, sync);
    }

    /// <summary>Lay out a patch folder, with an optional manifest.</summary>
    private static void GivePatch(PatchService service, string name,
                                  Dictionary<string, string> files,
                                  PatchTarget target = PatchTarget.GameRoot,
                                  IEnumerable<string>? deletes = null,
                                  IEnumerable<string>? volatiles = null)
    {
        var dir = Path.Combine(service.PatchesRoot, name);
        foreach (var (relative, contents) in files)
        {
            var path = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        service.SaveManifest(name, new PatchManifest
        {
            Name = name,
            Target = target,
            Delete = deletes?.ToList() ?? new List<string>(),
            Volatile = volatiles?.ToList() ?? new List<string>(),
        });
    }

    /// <summary>Every file under a folder with its hash — the shape of the tree.</summary>
    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), PatchService.Sha1Of, StringComparer.OrdinalIgnoreCase);

    // --- Applying -----------------------------------------------------------

    [Fact]
    public void Apply_AddsFilesThatWereNotThere()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "onlinefix", new() { ["OnlineFix.dll"] = "fix bytes" });

        var result = service.Apply("onlinefix", PatchTarget.GameRoot, game);

        Assert.Equal("fix bytes", File.ReadAllText(Path.Combine(game, "OnlineFix.dll")));
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Replaced);
        Assert.True(service.IsApplied("onlinefix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Apply_ReplacesAndKeepsTheOriginal()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        GivePatch(service, "onlinefix", new() { ["isaac-ng.exe"] = "patched exe" });

        var result = service.Apply("onlinefix", PatchTarget.GameRoot, game);

        Assert.Equal("patched exe", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
        Assert.Equal(1, result.Replaced);

        // The original has to survive somewhere or revert is impossible.
        var entry = service.LoadJournal("onlinefix", PatchTarget.GameRoot)!.Entries.Single();
        Assert.Equal(PatchOp.Replaced, entry.Op);
        Assert.Equal("stock exe", File.ReadAllText(entry.Backup!));
    }

    [Fact]
    public void Apply_HandlesNestedFolders()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { [@"resources\packed\a.a"] = "nested" });

        service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.Equal("nested", File.ReadAllText(Path.Combine(game, "resources", "packed", "a.a")));
    }

    [Fact]
    public void Apply_RemovesFilesTheManifestSaysMustBeAbsent()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "steam_api.dll"), "the real one");
        GivePatch(service, "fix", new() { ["OnlineFix.dll"] = "fix" }, deletes: new[] { "steam_api.dll" });

        var result = service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.False(File.Exists(Path.Combine(game, "steam_api.dll")));
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public void Apply_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, process, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["OnlineFix.dll"] = "fix" });
        process.Running = true;

        Assert.Throws<UnsafePathException>(() => service.Apply("fix", PatchTarget.GameRoot, game));
        Assert.False(File.Exists(Path.Combine(game, "OnlineFix.dll")));
    }

    [Fact]
    public void Apply_RefusesToApplyTwice()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "one" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // A second apply would back up its own output as though it were the
        // original, and the real original would be lost.
        Assert.Throws<UnsafePathException>(() => service.Apply("fix", PatchTarget.GameRoot, game));
    }

    [Fact]
    public void Apply_NeverDeletesOutsideTheTarget()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var outside = Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(outside, "untouched");

        // The Delete list is free text from a manifest, so it is the one place
        // a path can genuinely climb out of the target. A payload file cannot:
        // it is enumerated from inside the patch folder, so its relative path
        // is inside by construction.
        GivePatch(service, "evil", new() { ["a.dll"] = "a" }, deletes: new[] { @"..\outside.txt" });
        var result = service.Apply("evil", PatchTarget.GameRoot, game);

        Assert.True(File.Exists(outside));
        Assert.Equal("untouched", File.ReadAllText(outside));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("outside"));
    }

    [Fact]
    public void Apply_NeverDeletesOutOfAFolderAnotherSubsystemOwns()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var inMods = Path.Combine(game, "mods", "SomeMod");
        Directory.CreateDirectory(inMods);
        File.WriteAllText(Path.Combine(inMods, "main.lua"), "someone's mod");

        GivePatch(service, "fix", new() { ["a.dll"] = "a" }, deletes: new[] { @"mods\SomeMod\main.lua" });
        var result = service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.True(File.Exists(Path.Combine(inMods, "main.lua")));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("mods"));
    }

    [Fact]
    public void Apply_NeverWritesIntoModsWhichAnotherSubsystemOwns()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { [@"mods\SomeMod\main.lua"] = "would land in a profile" });

        var result = service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.False(Directory.Exists(Path.Combine(game, "mods")));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("mods"));
    }

    [Fact]
    public void Apply_RefusesWhenAnotherAppliedPatchOwnsTheSameFile()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock");

        GivePatch(service, "first", new() { ["isaac-ng.exe"] = "first patch" });
        GivePatch(service, "second", new() { ["isaac-ng.exe"] = "second patch" });
        service.Apply("first", PatchTarget.GameRoot, game);

        // The second one's backup would be the first one's output.
        var error = Assert.Throws<UnsafePathException>(() => service.Apply("second", PatchTarget.GameRoot, game));
        Assert.Contains("first", error.Message);
        Assert.Equal("first patch", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
    }

    // --- Reverting ----------------------------------------------------------

    [Fact]
    public void ApplyThenRevert_LeavesTheFolderExactlyAsItWas()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);

        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        File.WriteAllText(Path.Combine(game, "steam_api.dll"), "the real one");
        Directory.CreateDirectory(Path.Combine(game, "resources"));
        File.WriteAllText(Path.Combine(game, "resources", "packed.a"), "resources");

        var before = Snapshot(game);

        GivePatch(service, "onlinefix", new()
        {
            ["isaac-ng.exe"] = "patched exe",
            ["OnlineFix.dll"] = "fix bytes",
            [@"resources\extra.a"] = "added nested",
        }, deletes: new[] { "steam_api.dll" });

        service.Apply("onlinefix", PatchTarget.GameRoot, game);
        Assert.NotEqual(before, Snapshot(game));

        var result = service.Revert("onlinefix", PatchTarget.GameRoot);

        Assert.Equal(before, Snapshot(game));
        Assert.Empty(result.Skipped);
        Assert.False(service.IsApplied("onlinefix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Revert_LeavesAFileSomethingElseHasChangedSince()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        GivePatch(service, "fix", new() { ["isaac-ng.exe"] = "patched exe" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // Steam has updated the game over the top of the patch.
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "newer stock exe");

        var result = service.Revert("fix", PatchTarget.GameRoot);

        Assert.Equal("newer stock exe", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
        Assert.Single(result.Skipped);

        // Still applied as far as the record goes, because that file still is.
        Assert.True(service.IsApplied("fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Revert_ForcedOverwritesTheChangedFileAnyway()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        GivePatch(service, "fix", new() { ["isaac-ng.exe"] = "patched exe" });
        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "newer stock exe");

        var result = service.Revert("fix", PatchTarget.GameRoot, force: true);

        Assert.Equal("stock exe", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
        Assert.Empty(result.Skipped);
        Assert.False(service.IsApplied("fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Revert_RefusesWhileIsaacIsRunning()
    {
        using var temp = new TempDir();
        var (service, process, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "one" });
        service.Apply("fix", PatchTarget.GameRoot, game);
        process.Running = true;

        Assert.Throws<UnsafePathException>(() => service.Revert("fix", PatchTarget.GameRoot));
        Assert.True(File.Exists(Path.Combine(game, "a.dll")));
    }

    [Fact]
    public void Revert_RefusesWhenTheApplyNeverHappened()
    {
        using var temp = new TempDir();
        var (service, _, _, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "one" });

        Assert.Throws<UnsafePathException>(() => service.Revert("fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Revert_KeepsTheBackupsAfterwards()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        GivePatch(service, "fix", new() { ["isaac-ng.exe"] = "patched exe" });
        service.Apply("fix", PatchTarget.GameRoot, game);
        var backup = service.LoadJournal("fix", PatchTarget.GameRoot)!.Entries.Single().Backup!;

        service.Revert("fix", PatchTarget.GameRoot);

        // A revert that turns out to be wrong still has somewhere to go back to.
        Assert.True(File.Exists(backup));
    }

    // --- Interrupted applies ------------------------------------------------

    [Fact]
    public void AHalfWrittenJournalStillReverts()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "one.dll"), "original one");
        File.WriteAllText(Path.Combine(game, "two.dll"), "original two");
        var before = Snapshot(game);

        GivePatch(service, "fix", new() { ["one.dll"] = "patched one", ["two.dll"] = "patched two" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // Simulate a crash after the first file: drop the second entry, as the
        // incrementally-saved journal would have looked at that moment, and put
        // the second file back the way the apply had not yet changed it.
        var journal = service.LoadJournal("fix", PatchTarget.GameRoot)!;
        var dropped = journal.Entries[1];
        File.Copy(dropped.Backup!, Path.Combine(game, dropped.Path), overwrite: true);
        journal.Entries.RemoveAt(1);
        journal.Complete = false;
        Directory.CreateDirectory(service.AppliedRoot);
        File.WriteAllText(Path.Combine(service.AppliedRoot, "fix.GameRoot.json"),
                          System.Text.Json.JsonSerializer.Serialize(journal,
                              new System.Text.Json.JsonSerializerOptions
                              {
                                  WriteIndented = true,
                                  Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                              }));

        service.Revert("fix", PatchTarget.GameRoot);

        Assert.Equal(before, Snapshot(game));
    }

    // --- Drift --------------------------------------------------------------

    [Fact]
    public void DetectDrift_FindsWhatWasOverwrittenSinceTheApply()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock exe");
        GivePatch(service, "fix", new() { ["isaac-ng.exe"] = "patched exe", ["b.dll"] = "b" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.Empty(service.DetectDrift("fix", PatchTarget.GameRoot));

        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "steam updated me");
        var drift = service.DetectDrift("fix", PatchTarget.GameRoot);

        Assert.Single(drift);
        Assert.Equal("isaac-ng.exe", drift[0].Path);
    }

    [Fact]
    public void DetectDrift_ReportsAFileThatVanished()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });
        service.Apply("fix", PatchTarget.GameRoot, game);
        File.Delete(Path.Combine(game, "a.dll"));

        Assert.Equal("missing", service.DetectDrift("fix", PatchTarget.GameRoot).Single().Actual);
    }

    // --- Installing and describing -----------------------------------------

    [Fact]
    public void Install_CopiesAnUnzippedFolderIn()
    {
        using var temp = new TempDir();
        var (service, _, _, _) = Build(temp);
        var unzipped = temp.Dir("downloads", "OnlineFix v3");
        File.WriteAllText(Path.Combine(unzipped, "OnlineFix.dll"), "fix");

        var info = service.Install(unzipped, "onlinefix", PatchTarget.Repentogon, "the online fix");

        Assert.Equal(PatchTarget.Repentogon, info.Target);
        Assert.Equal(1, info.FileCount);
        Assert.Contains("onlinefix", service.ListPatches());

        // A copy: the folder they unzipped stays put and can be deleted.
        Assert.True(File.Exists(Path.Combine(unzipped, "OnlineFix.dll")));
    }

    [Fact]
    public void Install_RefusesToOverwriteAnExistingPatch()
    {
        using var temp = new TempDir();
        var (service, _, _, _) = Build(temp);
        var unzipped = temp.Dir("downloads", "fix");
        File.WriteAllText(Path.Combine(unzipped, "a.dll"), "a");
        service.Install(unzipped, "fix", PatchTarget.GameRoot);

        Assert.Throws<UnsafePathException>(() => service.Install(unzipped, "fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void Describe_DoesNotCountTheManifestAsPayload()
    {
        using var temp = new TempDir();
        var (service, _, _, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a", ["b.dll"] = "bb" });

        Assert.Equal(2, service.Describe("fix").FileCount);
    }

    [Fact]
    public void Describe_ReportsWhetherItIsApplied()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });

        Assert.False(service.Describe("fix").IsAppliedAnywhere);
        service.Apply("fix", PatchTarget.GameRoot, game);
        Assert.True(service.Describe("fix").IsAppliedAnywhere);
    }

    [Fact]
    public void APatchWithNoManifestIsStillUsable()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);

        // Just an unzipped folder dropped in by hand.
        var dir = Path.Combine(service.PatchesRoot, "handmade");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.dll"), "a");

        Assert.Equal("handmade", service.Describe("handmade").Name);
        service.Apply("handmade", PatchTarget.GameRoot, game);
        Assert.True(File.Exists(Path.Combine(game, "a.dll")));
    }

    [Fact]
    public void Remove_RefusesWhileThePatchIsStillApplied()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // Removing it would orphan the journal and strand the applied files.
        Assert.Throws<UnsafePathException>(() => service.Remove("fix"));

        service.Revert("fix", PatchTarget.GameRoot);
        service.Remove("fix");
        Assert.DoesNotContain("fix", service.ListPatches());
    }

    // --- One patch over both folders ---------------------------------------

    [Fact]
    public void TheSamePatchGoesOverBothFoldersIndependently()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var repentogon = Directory.CreateDirectory(Path.Combine(game, "Repentogon")).FullName;

        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "retail stock");
        File.WriteAllText(Path.Combine(repentogon, "isaac-ng.exe"), "rgon stock");
        GivePatch(service, "onlinefix", new() { ["isaac-ng.exe"] = "patched" });

        service.Apply("onlinefix", PatchTarget.GameRoot, game);
        service.Apply("onlinefix", PatchTarget.Repentogon, repentogon);

        Assert.Equal("patched", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
        Assert.Equal("patched", File.ReadAllText(Path.Combine(repentogon, "isaac-ng.exe")));
        Assert.True(service.IsApplied("onlinefix", PatchTarget.GameRoot));
        Assert.True(service.IsApplied("onlinefix", PatchTarget.Repentogon));
    }

    [Fact]
    public void RevertingOneFolderLeavesTheOtherPatched()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var repentogon = Directory.CreateDirectory(Path.Combine(game, "Repentogon")).FullName;

        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "retail stock");
        File.WriteAllText(Path.Combine(repentogon, "isaac-ng.exe"), "rgon stock");
        GivePatch(service, "onlinefix", new() { ["isaac-ng.exe"] = "patched" });

        service.Apply("onlinefix", PatchTarget.GameRoot, game);
        service.Apply("onlinefix", PatchTarget.Repentogon, repentogon);

        service.Revert("onlinefix", PatchTarget.GameRoot);

        // Each folder keeps its own original, so the two cannot cross over.
        Assert.Equal("retail stock", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
        Assert.Equal("patched", File.ReadAllText(Path.Combine(repentogon, "isaac-ng.exe")));
        Assert.False(service.IsApplied("onlinefix", PatchTarget.GameRoot));
        Assert.True(service.IsApplied("onlinefix", PatchTarget.Repentogon));
    }

    [Fact]
    public void ApplyingTwiceToTheSameFolderIsStillRefused()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "one" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.Throws<UnsafePathException>(() => service.Apply("fix", PatchTarget.GameRoot, game));
    }

    [Fact]
    public void ADifferentPatchOverTheOtherFolderIsNotAConflict()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var repentogon = Directory.CreateDirectory(Path.Combine(game, "Repentogon")).FullName;
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "retail stock");
        File.WriteAllText(Path.Combine(repentogon, "isaac-ng.exe"), "rgon stock");

        GivePatch(service, "first", new() { ["isaac-ng.exe"] = "first" });
        GivePatch(service, "second", new() { ["isaac-ng.exe"] = "second" });

        service.Apply("first", PatchTarget.GameRoot, game);

        // Same filename, different folder: nothing is being fought over.
        service.Apply("second", PatchTarget.Repentogon, repentogon);
        Assert.Equal("second", File.ReadAllText(Path.Combine(repentogon, "isaac-ng.exe")));
    }

    [Fact]
    public void RemoveRefusesWhileAppliedToEitherFolder()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        var repentogon = Directory.CreateDirectory(Path.Combine(game, "Repentogon")).FullName;
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });
        service.Apply("fix", PatchTarget.Repentogon, repentogon);

        Assert.Throws<UnsafePathException>(() => service.Remove("fix"));

        service.Revert("fix", PatchTarget.Repentogon);
        service.Remove("fix");
        Assert.DoesNotContain("fix", service.ListPatches());
    }

    [Fact]
    public void DescribeReportsEachFolderSeparately()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        var info = service.Describe("fix");

        Assert.True(info.States.Single(t => t.Target == PatchTarget.GameRoot).IsApplied);
        Assert.False(info.States.Single(t => t.Target == PatchTarget.Repentogon).IsApplied);
        Assert.True(info.IsAppliedAnywhere);
    }

    // --- Files the game rewrites -------------------------------------------

    [Fact]
    public void AVolatileFileIsNotReportedAsDrift()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix",
                  new() { ["OnlineFix.ini"] = "[Main]", ["OnlineFix.dll"] = "dll" },
                  volatiles: new[] { "OnlineFix.ini" });

        service.Apply("fix", PatchTarget.GameRoot, game);

        // The game has rewritten its config, as it does on every launch.
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), "[Main]\nBuildId=7");

        Assert.Empty(service.DetectDrift("fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void APreservedFileSurvivesTheRevertWithItsChanges()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix",
                  new() { ["OnlineFix.ini"] = "[Main]", ["OnlineFix.dll"] = "dll" },
                  volatiles: new[] { "OnlineFix.ini" });

        service.Apply("fix", PatchTarget.GameRoot, game);

        // The dll has written its first-launch hash into the config.
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), "[Main]\nHash=abc123");

        var result = service.Revert("fix", PatchTarget.GameRoot);

        // Putting the pristine copy back would make the next start look like a
        // first launch again, which opens a web page.
        Assert.Equal("[Main]\nHash=abc123", File.ReadAllText(Path.Combine(game, "OnlineFix.ini")));
        Assert.Equal(1, result.Preserved);

        // Everything else comes off, and a preserved file must not hold the
        // journal open the way a skipped one does.
        Assert.False(File.Exists(Path.Combine(game, "OnlineFix.dll")));
        Assert.Empty(result.Skipped);
        Assert.False(service.IsApplied("fix", PatchTarget.GameRoot));
    }

    [Fact]
    public void ReapplyingDoesNotClobberAPreservedFile()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["OnlineFix.ini"] = "[Main]", ["OnlineFix.dll"] = "dll" },
                  volatiles: new[] { "OnlineFix.ini" });

        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), "[Main]\nHash=abc123");
        service.Revert("fix", PatchTarget.GameRoot);

        var again = service.Apply("fix", PatchTarget.GameRoot, game);

        // The hash has to survive the round trip, or the web page comes back on
        // the second install rather than the first.
        Assert.Equal("[Main]\nHash=abc123", File.ReadAllText(Path.Combine(game, "OnlineFix.ini")));
        Assert.Equal(1, again.Preserved);
    }

    [Fact]
    public void AFirstApplyStillLaysDownTheDefaultConfig()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["OnlineFix.ini"] = "[Main]" },
                  volatiles: new[] { "OnlineFix.ini" });

        var result = service.Apply("fix", PatchTarget.GameRoot, game);

        // Nothing to preserve when it was not there: the shipped default lands.
        Assert.Equal("[Main]", File.ReadAllText(Path.Combine(game, "OnlineFix.ini")));
        Assert.Equal(0, result.Preserved);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public void ConfigFilesArePreservedWithoutBeingDeclared()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);

        // No volatiles list at all — the patch the user already had.
        GivePatch(service, "fix", new() { ["OnlineFix.ini"] = "[Main]", ["a.dll"] = "dll" });

        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), "[Main]\nHash=abc123");
        service.Revert("fix", PatchTarget.GameRoot);

        Assert.Equal("[Main]\nHash=abc123", File.ReadAllText(Path.Combine(game, "OnlineFix.ini")));
        Assert.False(File.Exists(Path.Combine(game, "a.dll")));
    }

    [Fact]
    public void ANonVolatileFileStillBlocksTheRevert()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "stock");
        GivePatch(service, "fix",
                  new() { ["isaac-ng.exe"] = "patched", ["OnlineFix.ini"] = "[Main]" },
                  volatiles: new[] { "OnlineFix.ini" });

        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "isaac-ng.exe"), "a game update");

        var result = service.Revert("fix", PatchTarget.GameRoot);

        // Marking a config volatile must not weaken the check on the exe.
        Assert.Single(result.Skipped);
        Assert.Equal("isaac-ng.exe", result.Skipped[0].Path);
        Assert.Equal("a game update", File.ReadAllText(Path.Combine(game, "isaac-ng.exe")));
    }

    [Fact]
    public void LogsAreVolatileWithoutBeingDeclared()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["repentogon.log"] = "" });

        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "repentogon.log"), "a session's worth of logging");

        Assert.Empty(service.DetectDrift("fix", PatchTarget.GameRoot));

        var result = service.Revert("fix", PatchTarget.GameRoot);
        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.Preserved);
    }

    [Fact]
    public void MarkVolatile_RecordsPathsForNextTime()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);

        // A .cfg rather than a .ini: ini files are preserved automatically, so
        // they never reach the prompt that offers to remember them.
        GivePatch(service, "fix", new() { ["prefs.cfg"] = "defaults" });

        service.Apply("fix", PatchTarget.GameRoot, game);
        File.WriteAllText(Path.Combine(game, "prefs.cfg"), "rewritten");
        Assert.Single(service.DetectDrift("fix", PatchTarget.GameRoot));

        service.MarkVolatile("fix", new[] { "prefs.cfg" });

        Assert.Empty(service.DetectDrift("fix", PatchTarget.GameRoot));
        Assert.Contains("prefs.cfg", service.LoadManifest("fix").Volatile);
    }

    [Fact]
    public void MarkVolatile_DoesNotDuplicateWhatIsAlreadyThere()
    {
        using var temp = new TempDir();
        var (service, _, _, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.ini"] = "a" }, volatiles: new[] { "a.ini" });

        service.MarkVolatile("fix", new[] { "a.ini", "a.ini" });

        Assert.Single(service.LoadManifest("fix").Volatile);
    }

    [Fact]
    public void OnlySettingsFilesMayBeLearnedAsVolatile()
    {
        // The drift prompt offers to remember what changed. The drifted set can
        // include binaries — another tool stripping this patch's dlls looks the
        // same as a config being rewritten — and remembering a dll would turn
        // off the check that stops a revert clobbering a newer file.
        Assert.True(PatchManifest.CanLearnAsVolatile("OnlineFix.ini"));
        Assert.True(PatchManifest.CanLearnAsVolatile("isaac-highfps.ini"));
        Assert.True(PatchManifest.CanLearnAsVolatile("dlllist.txt"));

        Assert.False(PatchManifest.CanLearnAsVolatile("OnlineFix.dll"));
        Assert.False(PatchManifest.CanLearnAsVolatile("isaac-ng.exe"));
        Assert.False(PatchManifest.CanLearnAsVolatile("winmm.dll"));
    }

    [Fact]
    public void AToolRemovingThePatchesFilesIsReportedAsDriftNotSilence()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["OnlineFix.dll"] = "dll", ["OnlineFix.ini"] = "[Main]" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // Something else has cleaned the patch's files out from under it.
        File.Delete(Path.Combine(game, "OnlineFix.dll"));

        var drift = service.DetectDrift("fix", PatchTarget.GameRoot);

        Assert.Single(drift);
        Assert.Equal("missing", drift[0].Actual);
    }

    [Fact]
    public void RevertingAfterFilesWereRemovedElsewhereStillFinishes()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        File.WriteAllText(Path.Combine(game, "steam_api.dll"), "the real one");
        GivePatch(service, "fix",
                  new() { ["OnlineFix.dll"] = "dll", ["steam_api.dll"] = "theirs", ["OnlineFix.ini"] = "[Main]" },
                  volatiles: new[] { "OnlineFix.ini" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        // A separate patcher removed what it did not recognise.
        File.Delete(Path.Combine(game, "OnlineFix.dll"));
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), "rewritten at launch");

        var result = service.Revert("fix", PatchTarget.GameRoot);

        // An added file that is already gone is nothing to undo, and the config
        // is volatile — so the patch comes off rather than sticking forever.
        Assert.Empty(result.Skipped);
        Assert.False(service.IsApplied("fix", PatchTarget.GameRoot));
        Assert.Equal("the real one", File.ReadAllText(Path.Combine(game, "steam_api.dll")));
    }

    [Fact]
    public void ListPatches_IgnoresTheBookkeepingFolders()
    {
        using var temp = new TempDir();
        var (service, _, game, _) = Build(temp);
        GivePatch(service, "fix", new() { ["a.dll"] = "a" });
        service.Apply("fix", PatchTarget.GameRoot, game);

        Assert.Equal(new[] { "fix" }, service.ListPatches());
    }
}
