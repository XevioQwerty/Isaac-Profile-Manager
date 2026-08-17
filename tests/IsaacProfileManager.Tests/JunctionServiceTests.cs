using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class JunctionServiceTests
{
    private readonly JunctionService _junctions = new();

    [Fact]
    public void Create_MakesALinkThatResolvesToTheTarget()
    {
        using var temp = new TempDir();
        var target = temp.Dir("target");
        temp.File(@"target\marker.txt", "hello");
        var link = temp.Combine("link");

        _junctions.Create(link, target);

        Assert.True(_junctions.IsJunction(link));
        Assert.Equal(target, _junctions.GetTarget(link), ignoreCase: true);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "marker.txt")));
    }

    [Fact]
    public void IsJunction_IsFalseForARealFolderAndForNothing()
    {
        using var temp = new TempDir();
        Assert.False(_junctions.IsJunction(temp.Dir("real")));
        Assert.False(_junctions.IsJunction(temp.Combine("does-not-exist")));
        Assert.Null(_junctions.GetTarget(temp.Combine("real")));
    }

    [Fact]
    public void RemoveLink_DeletesTheLinkAndLeavesTheTargetIntact()
    {
        using var temp = new TempDir();
        var target = temp.Dir("target");
        temp.File(@"target\mod\main.lua", "-- content");
        var link = temp.Combine("link");
        _junctions.Create(link, target);

        _junctions.RemoveLink(link);

        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(target, "mod", "main.lua")));
    }

    [Fact]
    public void RemoveLink_RefusesToDeleteARealFolder()
    {
        using var temp = new TempDir();
        var real = temp.Dir("real");
        temp.File(@"real\precious.txt", "user data");

        var ex = Assert.Throws<UnsafePathException>(() => _junctions.RemoveLink(real));

        Assert.Contains("real folder", ex.Message);
        Assert.True(File.Exists(Path.Combine(real, "precious.txt")));
    }

    [Fact]
    public void RemoveLink_OnAMissingPathIsANoOp()
    {
        using var temp = new TempDir();
        _junctions.RemoveLink(temp.Combine("nothing-here"));
    }

    [Fact]
    public void Create_RefusesWhenSomethingAlreadyOccupiesTheLinkPath()
    {
        using var temp = new TempDir();
        var target = temp.Dir("target");
        var occupied = temp.Dir("occupied");

        Assert.Throws<UnsafePathException>(() => _junctions.Create(occupied, target));
    }

    [Fact]
    public void Create_RefusesWhenTheTargetDoesNotExist()
    {
        using var temp = new TempDir();
        Assert.Throws<UnsafePathException>(() => _junctions.Create(temp.Combine("link"), temp.Combine("missing")));
        Assert.False(Directory.Exists(temp.Combine("link")));
    }

    [Fact]
    public void Repoint_MovesTheLinkWithoutDisturbingEitherTarget()
    {
        using var temp = new TempDir();
        var first = temp.Dir("first");
        var second = temp.Dir("second");
        temp.File(@"first\a.txt");
        temp.File(@"second\b.txt");
        var link = temp.Combine("link");

        _junctions.Create(link, first);
        _junctions.Repoint(link, second);

        Assert.Equal(second, _junctions.GetTarget(link), ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(first, "a.txt")));
        Assert.True(File.Exists(Path.Combine(second, "b.txt")));
    }

    [Fact]
    public void Create_HandlesPathsWithSpacesAndUnusualCharacters()
    {
        using var temp = new TempDir();
        // The real install uses "~" as the build root and the game folder name
        // has spaces; both must survive the NT-namespace round trip.
        var target = temp.Dir("~", "The Binding of Isaac Rebirth");
        var link = temp.Combine("my link");

        _junctions.Create(link, target);

        Assert.Equal(target, _junctions.GetTarget(link), ignoreCase: true);
    }
}
