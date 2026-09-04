using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class DisableMarkerSweepTests
{
    [Fact]
    public void Sweep_ClearsMarkersInRealFolders_AndNeverLooksThroughAJunction()
    {
        using var temp = new TempDir();
        var profile = temp.Dir("profiles", "coop");
        temp.File(@"profiles\coop\real-mod\disable.it");
        temp.File(@"profiles\coop\real-mod\nested\disable.it");

        // A library entry linked into the profile, carrying a marker of its own.
        var entry = temp.Dir("library", "linked-mod");
        var linkedMarker = temp.File(@"library\linked-mod\disable.it");
        new JunctionService().Create(Path.Combine(profile, "linked-mod"), entry);

        var cleared = ModProfileService.ClearDisableMarkers(profile);

        Assert.Equal(2, cleared);
        Assert.False(File.Exists(Path.Combine(profile, "real-mod", "disable.it")));
        Assert.True(File.Exists(linkedMarker), "a marker in the shared library must be left alone");
    }

    [Fact]
    public void Sweep_OnAMissingFolder_IsZero()
    {
        using var temp = new TempDir();
        Assert.Equal(0, ModProfileService.ClearDisableMarkers(temp.Combine("nope")));
    }
}
