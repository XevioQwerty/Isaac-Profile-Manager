using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class DirectoryCopierTests
{
    [Fact]
    public void Copy_ReproducesTheTreeIncludingNestedFiles()
    {
        using var temp = new TempDir();
        temp.File(@"src\a.txt", "a");
        temp.File(@"src\nested\deep\b.txt", "b");

        DirectoryCopier.Copy(temp.Combine("src"), temp.Combine("dst"));

        Assert.Equal("a", File.ReadAllText(temp.Combine("dst", "a.txt")));
        Assert.Equal("b", File.ReadAllText(temp.Combine("dst", "nested", "deep", "b.txt")));
    }

    [Fact]
    public void Copy_DoesNotFollowJunctionsInsideTheTree()
    {
        using var temp = new TempDir();
        temp.File(@"src\a.txt", "a");
        var outside = temp.Dir("outside");
        temp.File(@"outside\enormous.bin", new string('x', 64));
        new JunctionService().Create(temp.Combine("src", "link"), outside);

        var skipped = new List<string>();
        DirectoryCopier.Copy(temp.Combine("src"), temp.Combine("dst"),
                             progress: new Progress<string>(m => { lock (skipped) skipped.Add(m); }));

        // Following the link would have duplicated whatever it points at —
        // for a build folder, gigabytes of game resources.
        Assert.False(Directory.Exists(temp.Combine("dst", "link")));
        Assert.True(File.Exists(temp.Combine("dst", "a.txt")));
    }

    [Fact]
    public void Copy_RefusesWhenTheSourceItselfIsALink()
    {
        using var temp = new TempDir();
        var real = temp.Dir("real");
        new JunctionService().Create(temp.Combine("link"), real);

        Assert.Throws<UnsafePathException>(() => DirectoryCopier.Copy(temp.Combine("link"), temp.Combine("dst")));
    }

    [Fact]
    public void Copy_WithoutOverwriteKeepsWhatIsAlreadyThere()
    {
        using var temp = new TempDir();
        temp.File(@"src\a.txt", "from source");
        temp.File(@"dst\a.txt", "user edited");

        DirectoryCopier.Copy(temp.Combine("src"), temp.Combine("dst"), overwrite: false);

        Assert.Equal("user edited", File.ReadAllText(temp.Combine("dst", "a.txt")));
    }

    [Fact]
    public void Copy_RefusesAMissingSource()
    {
        using var temp = new TempDir();
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryCopier.Copy(temp.Combine("nope"), temp.Combine("dst")));
    }
}
