using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Round-trips the patch engine over real Isaac binaries rather than the
/// few-byte fixtures the unit tests use.
///
/// Skipped unless IPM_PATCHLAB points at a prepared folder holding
/// <c>game\</c> and <c>sync\.patches\onlinefix\</c>, because it needs a copy of
/// a real install. It exists because "apply then revert leaves the folder
/// identical" is the claim the whole feature rests on, and 9 MB executables and
/// read-only attributes are where that claim would break if it were going to.
/// </summary>
public class PatchRoundTripHarness
{
    private sealed class ClosedGame : IGameProcessService
    {
        public bool IsIsaacRunning() => false;
    }

    private static string? Lab => Environment.GetEnvironmentVariable("IPM_PATCHLAB");

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), PatchService.Sha1Of, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void RealBinaries_SurviveApplyAndRevertUnchanged()
    {
        var lab = Lab;

        // No skip attribute in this project's xunit setup, and a real install is
        // not something CI can be given, so an absent lab is a pass by default.
        if (string.IsNullOrWhiteSpace(lab) || !Directory.Exists(lab)) return;

        var game = Path.Combine(lab!, "game");
        var sync = Path.Combine(lab!, "sync");
        var service = new PatchService(new ClosedGame(), sync);

        var before = Snapshot(game);
        Assert.NotEmpty(before);

        var result = service.Apply("onlinefix", game);

        // It really did something: the exe is now the patched one.
        Assert.True(result.Changed >= 6, $"expected a real payload, got {result.Summary}");
        Assert.Empty(result.Skipped);
        Assert.NotEqual(before[@"isaac-ng.exe"], Snapshot(game)[@"isaac-ng.exe"]);

        Assert.Empty(service.DetectDrift("onlinefix"));

        var reverted = service.Revert("onlinefix");
        Assert.Empty(reverted.Skipped);

        var after = Snapshot(game);
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, hash) in before)
            Assert.Equal(hash, after[path]);
    }
}
