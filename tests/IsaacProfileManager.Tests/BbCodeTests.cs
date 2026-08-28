using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Workshop descriptions are BBCode. A lot of Isaac authors paste their whole
/// store page into metadata.xml, so shown raw the library list was full of
/// [h2] and [url=...] instead of sentences.
/// </summary>
public class BbCodeTests
{
    [Fact]
    public void Strip_RemovesTagsButKeepsTheWordsBetweenThem()
    {
        // Taken from a real library entry.
        const string description =
            "[h2]Controller Optimizer[/h2]\n\n" +
            "This is a controller-focused input-feel mod.\n\n" +
            "[b]Brimstone input stabilization[/b]";

        var stripped = BbCode.Strip(description);

        Assert.DoesNotContain("[h2]", stripped);
        Assert.DoesNotContain("[/b]", stripped);
        Assert.Contains("Controller Optimizer", stripped);
        Assert.Contains("Brimstone input stabilization", stripped);
    }

    [Fact]
    public void Strip_KeepsALinkLabelAndDropsTheUrl()
    {
        Assert.Equal("our install guide",
                     BbCode.Strip("[url=https://repentogon.com/install.html]our install guide[/url]"));
    }

    [Fact]
    public void Strip_CollapsesTheGapsLeftByImageTags()
    {
        var stripped = BbCode.Strip("First.\n[img]https://i.imgur.com/x.gif[/img]\n\n\n\nSecond.");

        Assert.DoesNotContain("imgur", stripped);
        Assert.DoesNotContain("\n\n\n", stripped.ReplaceLineEndings("\n"));
        Assert.StartsWith("First.", stripped);
        Assert.EndsWith("Second.", stripped);
    }

    [Fact]
    public void Strip_LeavesOrdinaryProseAlone()
    {
        const string plain = "Adds a chargebar. Works with everything [probably].";
        Assert.Equal(plain, BbCode.Strip(plain));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Strip_HandlesNothing(string? input) => Assert.Equal(string.Empty, BbCode.Strip(input));

    [Fact]
    public void Summarise_TakesTheFirstRealLineAndShortensIt()
    {
        var summary = BbCode.Summarise("[h2]Title[/h2]\n\nA long description follows here.", maxLength: 10);

        Assert.Equal("Title", summary[..5]);
        Assert.True(summary.Length <= 13, summary);
    }
}
