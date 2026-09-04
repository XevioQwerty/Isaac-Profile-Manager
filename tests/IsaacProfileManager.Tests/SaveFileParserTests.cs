using System.Buffers.Binary;
using System.Text;
using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class SaveFileParserTests
{
    /// <summary>A save shaped like the real ones: magic, u32, chunks with 12-byte headers, counter, checksum.</summary>
    private static byte[] Build(int[] achievements, int[] items, int[] challenges, uint counter = 7, int achievementSlots = 642)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("ISAACNGSAVE09R  "));
        WriteInt(ms, 0);

        void Chunk(int type, int elementSize, int count, Action<byte[]> fill)
        {
            var data = new byte[count * elementSize];
            fill(data);
            WriteInt(ms, type);
            WriteInt(ms, count * 4);     // the size-as-written field counts four per element for flag chunks too
            WriteInt(ms, count);
            ms.Write(data);
        }

        Chunk(1, 1, achievementSlots, d => { foreach (var a in achievements) d[a] = 1; });
        Chunk(2, 4, 3, d => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 545));
        Chunk(3, 4, 2, _ => { });
        Chunk(4, 1, 733, d => { foreach (var i in items) d[i] = 1; });
        Chunk(5, 1, 7, d => d[1] = 1);
        Chunk(6, 1, 104, d => { d[3] = 1; d[9] = 1; });
        Chunk(7, 1, 46, d => { foreach (var c in challenges) d[c] = 1; });
        Chunk(8, 4, 2, _ => { });
        Chunk(9, 4, 2, _ => { });
        Chunk(10, 1, 4, _ => { });

        // Bestiary: opaque. Header then some bytes.
        WriteInt(ms, 11); WriteInt(ms, 8); WriteInt(ms, 4);
        ms.Write(new byte[24]);

        WriteInt(ms, (int)counter);
        WriteInt(ms, unchecked((int)0xDEADBEEF));
        return ms.ToArray();
    }

    private static void WriteInt(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    [Fact]
    public void Parse_ReadsFlagsCountersAndTheTrailer()
    {
        var summary = SaveFileParser.Parse(Build(new[] { 1, 5, 640 }, new[] { 1, 2, 700 }, new[] { 3 }, counter: 1574));

        Assert.True(summary.Parsed);
        Assert.Equal("ISAACNGSAVE09R", summary.Header);
        Assert.Equal(new[] { 1, 5, 640 }, summary.Achievements);
        Assert.Equal(new[] { 1, 2, 700 }, summary.Items);
        Assert.Equal(new[] { 3 }, summary.Challenges);
        Assert.Equal(new[] { 3, 9 }, summary.Bosses);
        Assert.Equal(new[] { 1 }, summary.Minibosses);
        Assert.Equal(642, summary.AchievementSlots);
        Assert.Equal(545, summary.EventCounters[1]);
        Assert.Equal(1574u, summary.Counter);
        Assert.Equal(0xDEADBEEF, summary.Checksum);
        Assert.Equal(11, summary.Chunks.Count);
        Assert.Contains("3/641 achievements", summary.Summary);
    }

    [Fact]
    public void Parse_UsesTheElementSizeTable_NotTheSizeWrittenField()
    {
        // Items are one byte each on disk even though the header says four; a
        // parser trusting the field would land in the wrong place for every
        // later chunk. Challenges come after items, so they prove the walk.
        var summary = SaveFileParser.Parse(Build(Array.Empty<int>(), new[] { 10 }, new[] { 7, 8 }));

        Assert.Equal(new[] { 7, 8 }, summary.Challenges);
        Assert.Equal(733, summary.ItemSlots);
    }

    [Fact]
    public void Parse_RejectsThingsThatAreNotSaves()
    {
        Assert.False(SaveFileParser.Parse(Encoding.ASCII.GetBytes("hello")).Parsed);
        Assert.False(SaveFileParser.Parse(new byte[64]).Parsed);

        var wrongMagic = Build(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        Encoding.ASCII.GetBytes("NOTASAVEFILE").CopyTo(wrongMagic, 0);
        Assert.False(SaveFileParser.Parse(wrongMagic).Parsed);
    }

    [Fact]
    public void Compare_ListsWhatEachSideHasThatTheOtherLacks()
    {
        var a = SaveFileParser.Parse(Build(new[] { 1, 2 }, new[] { 5 }, new[] { 1 }));
        var b = SaveFileParser.Parse(Build(new[] { 2, 3 }, new[] { 5, 6 }, new[] { 1 }));

        var diff = SaveFileParser.Compare(a, b);

        Assert.Equal(new[] { 1 }, diff.AchievementsOnlyInFirst);
        Assert.Equal(new[] { 3 }, diff.AchievementsOnlyInSecond);
        Assert.Empty(diff.ItemsOnlyInFirst);
        Assert.Equal(new[] { 6 }, diff.ItemsOnlyInSecond);
        Assert.False(diff.Identical);
        Assert.True(SaveFileParser.Compare(a, a).Identical);
    }

    [Fact]
    public void ParseFile_ReportsAMissingFileInsteadOfThrowing()
    {
        using var temp = new TempDir();
        var summary = SaveFileParser.ParseFile(temp.Combine("nope.dat"));
        Assert.False(summary.Parsed);
        Assert.NotNull(summary.Problem);
    }
}
