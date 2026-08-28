using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// The share code: what survives a round trip, and what a damaged or hostile
/// one is allowed to do.
/// </summary>
public class ShareCodeTests
{
    private static SharedProfile Sample() => new()
    {
        Name = "RPTG_v1.0",
        Notes = "the one we play on Tuesdays",
        ExportedUtc = "2026-08-28T00:00:00.0000000Z",
        Mods = new List<string> { "external item descriptions", "repentogon", "hand written" },
        Hashes = new Dictionary<string, string>
        {
            ["external item descriptions"] = new string('a', 64),
            ["repentogon"] = new string('b', 64),
        },
        WorkshopIds = new Dictionary<string, string>
        {
            ["external item descriptions"] = "836319872",
            ["repentogon"] = "3127536138",
        },
    };

    [Fact]
    public void RoundTrip_KeepsTheIdsHashesAndNotes()
    {
        var decoded = ShareCodeService.Decode(ShareCodeService.Encode(Sample()));

        Assert.Equal("RPTG_v1.0", decoded.Name);
        Assert.Equal("the one we play on Tuesdays", decoded.Notes);
        Assert.Equal(3, decoded.Mods.Count);
        Assert.Equal("836319872", decoded.WorkshopIds["external item descriptions"]);
        Assert.Equal(new string('b', 64), decoded.Hashes["repentogon"]);
        Assert.True(decoded.IsFetchable);
    }

    [Fact]
    public void Decode_AcceptsACodeThatWasWrappedAcrossLines()
    {
        // Chat clients wrap long codes. Failing on that would send the user
        // hunting for a corruption problem they do not have.
        var code = ShareCodeService.Encode(Sample());
        var wrapped = string.Join(Environment.NewLine,
            code.Chunk(40).Select(chunk => new string(chunk)));

        Assert.Equal("RPTG_v1.0", ShareCodeService.Decode(wrapped).Name);
    }

    [Fact]
    public void Decode_RejectsSomethingThatIsNotAShareCode()
    {
        var ex = Assert.Throws<ShareCodeException>(() => ShareCodeService.Decode("hello there"));
        Assert.Contains("IPM1-", ex.Message);
    }

    [Fact]
    public void Decode_RejectsATruncatedCodeWithoutThrowingSomethingRaw()
    {
        var code = ShareCodeService.Encode(Sample());
        Assert.Throws<ShareCodeException>(() => ShareCodeService.Decode(code[..(code.Length / 2)]));
    }

    [Fact]
    public void Decode_RefusesAPayloadThatExpandsAbsurdly()
    {
        // A share code comes from someone else, so a few hundred bytes must not
        // be able to turn into gigabytes of allocation.
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
                   output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(new byte[8 * 1024 * 1024]);

        var bomb = ShareCodeService.Prefix +
                   Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<ShareCodeException>(() => ShareCodeService.Decode(bomb));
    }

    [Fact]
    public void Encode_IsShorterThanTheJsonButNotShortInAbsoluteTerms()
    {
        // Guards the claim made to users: a code is a paste, not something you
        // read out. If this ever gets small, the format changed silently.
        var code = ShareCodeService.Encode(Sample());
        Assert.StartsWith(ShareCodeService.Prefix, code);
        Assert.True(code.Length > 100, $"code was {code.Length} chars for 3 mods with hashes");
    }

    // --- Collections --------------------------------------------------------

    [Theory]
    [InlineData("3775687336", "3775687336")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3775687336", "3775687336")]
    [InlineData("steam://openurl/https://steamcommunity.com/sharedfiles/filedetails/?id=3775687336", "3775687336")]
    [InlineData("not a collection", null)]
    public void ParseId_TakesABareIdOrAPastedUrl(string input, string? expected) =>
        Assert.Equal(expected, WorkshopCollectionService.ParseId(input));

    [Fact]
    public void ParseChildren_ReadsTheItemList()
    {
        const string json = """
        {"response":{"result":1,"resultcount":1,"collectiondetails":[
          {"publishedfileid":"3775687336","result":1,"children":[
            {"publishedfileid":"3419883482","sortorder":0,"filetype":0},
            {"publishedfileid":"3605843497","sortorder":1,"filetype":0}]}]}}
        """;

        Assert.Equal(new[] { "3419883482", "3605843497" }, WorkshopCollectionService.ParseChildren(json));
    }

    [Fact]
    public void ParseChildren_SaysSoWhenTheIdIsNotACollection()
    {
        // result 9 is what a plain mod's id comes back as. An empty list would
        // read as "an empty collection" and send the user the wrong way.
        const string json = """
        {"response":{"result":1,"collectiondetails":[{"publishedfileid":"836319872","result":9}]}}
        """;

        var ex = Assert.Throws<ShareCodeException>(() => WorkshopCollectionService.ParseChildren(json));
        Assert.Contains("collection", ex.Message);
    }
}
