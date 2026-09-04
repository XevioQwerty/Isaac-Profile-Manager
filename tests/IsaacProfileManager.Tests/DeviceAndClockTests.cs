using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class DeviceAndClockTests
{
    private static Dictionary<string, int> Clock(params (string Device, int Count)[] entries) =>
        entries.ToDictionary(e => e.Device, e => e.Count);

    [Fact]
    public void Compare_SameClocks_AreEqual()
    {
        Assert.Equal(ClockRelation.Equal, VectorClock.Compare(Clock(("a", 2), ("b", 1)), Clock(("b", 1), ("a", 2))));
        Assert.Equal(ClockRelation.Equal, VectorClock.Compare(null, null));
    }

    [Fact]
    public void Compare_OneSideStrictlyNewer_DominatesTheOther()
    {
        var older = Clock(("desktop", 3));
        var newer = Clock(("desktop", 3), ("laptop", 1));

        Assert.Equal(ClockRelation.Behind, VectorClock.Compare(older, newer));
        Assert.Equal(ClockRelation.Ahead, VectorClock.Compare(newer, older));
    }

    [Fact]
    public void Compare_BothSidesAdvanced_IsAFork()
    {
        // Both machines captured from revision (desktop 3): the case that must never be auto-merged.
        var desktop = Clock(("desktop", 4));
        var laptop = Clock(("desktop", 3), ("laptop", 1));

        Assert.Equal(ClockRelation.Fork, VectorClock.Compare(desktop, laptop));
        Assert.Equal(ClockRelation.Fork, VectorClock.Compare(laptop, desktop));
    }

    [Fact]
    public void Bump_AdvancesOnlyThisDevice_AndDoesNotMutateTheInput()
    {
        var original = Clock(("desktop", 2));
        var bumped = VectorClock.Bump(original, "laptop");

        Assert.Equal(1, bumped["laptop"]);
        Assert.Equal(2, bumped["desktop"]);
        Assert.False(original.ContainsKey("laptop"));
        Assert.Equal(3, VectorClock.Revision(bumped));
    }

    [Fact]
    public void Ensure_WritesAnIdOnce_AndKeepsIt()
    {
        var config = new AppConfig();

        Assert.True(DeviceService.Ensure(config, out var first));
        Assert.False(DeviceService.Ensure(config, out var second));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(32, first.Id.Length);
        Assert.Equal(8, first.ShortId.Length);
        Assert.False(string.IsNullOrWhiteSpace(first.Name));
    }

    [Fact]
    public void Ensure_LeavesAnExistingNameAlone()
    {
        var config = new AppConfig { DeviceId = "abc", DeviceName = "The Laptop" };

        Assert.False(DeviceService.Ensure(config, out var identity));
        Assert.Equal("The Laptop", identity.Name);
        Assert.Equal("abc", identity.ShortId);
    }

    [Fact]
    public void SafeName_StripsCharactersAFolderCannotHold()
    {
        Assert.Equal("my_laptop", DeviceService.SafeName("my:laptop"));
        Assert.Equal("device", DeviceService.SafeName("   "));
    }
}
