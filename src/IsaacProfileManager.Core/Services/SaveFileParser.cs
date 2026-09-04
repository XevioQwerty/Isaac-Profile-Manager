using System.Buffers.Binary;
using System.Text;

namespace IsaacProfileManager.Core.Services;

/// <summary>One chunk of a save file: where it is and how it is shaped.</summary>
public sealed record SaveChunk(int Type, string Name, int SizeWritten, int Count, int DataOffset, int ElementSize)
{
    public int DataLength => Count * ElementSize;
}

/// <summary>What a <c>persistentgamedata&lt;N&gt;.dat</c> says about itself, read-only.</summary>
public sealed class SaveFileSummary
{
    public bool Parsed { get; init; }
    public string? Problem { get; init; }
    public string Header { get; init; } = string.Empty;
    public long Length { get; init; }

    /// <summary>The game's own save counter: goes up on every save, including the one it makes at the menu on launch.</summary>
    public uint Counter { get; init; }
    public uint Checksum { get; init; }

    public IReadOnlyList<SaveChunk> Chunks { get; init; } = Array.Empty<SaveChunk>();

    /// <summary>Ids whose flag byte is set, per chunk type.</summary>
    public IReadOnlyList<int> Achievements { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Items { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Challenges { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Bosses { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Minibosses { get; init; } = Array.Empty<int>();

    public int AchievementSlots { get; init; }
    public int ItemSlots { get; init; }
    public int ChallengeSlots { get; init; }
    public int BossSlots { get; init; }

    /// <summary>Event counters as stored: deaths, kills and the rest, by index. Indices are not named here.</summary>
    public IReadOnlyList<int> EventCounters { get; init; } = Array.Empty<int>();

    public string Summary => !Parsed
        ? Problem ?? "not a save file"
        : $"{Achievements.Count}/{AchievementSlots - 1} achievements · {Items.Count} items touched · " +
          $"{Challenges.Count}/{ChallengeSlots - 1} challenges · {Bosses.Count} bosses · save #{Counter}";
}

/// <summary>How two saves' unlock state differ, as id lists.</summary>
public sealed record SaveDiff(
    IReadOnlyList<int> AchievementsOnlyInFirst,
    IReadOnlyList<int> AchievementsOnlyInSecond,
    IReadOnlyList<int> ItemsOnlyInFirst,
    IReadOnlyList<int> ItemsOnlyInSecond,
    IReadOnlyList<int> ChallengesOnlyInFirst,
    IReadOnlyList<int> ChallengesOnlyInSecond)
{
    public bool Identical =>
        AchievementsOnlyInFirst.Count == 0 && AchievementsOnlyInSecond.Count == 0 &&
        ItemsOnlyInFirst.Count == 0 && ItemsOnlyInSecond.Count == 0 &&
        ChallengesOnlyInFirst.Count == 0 && ChallengesOnlyInSecond.Count == 0;
}

/// <summary>
/// Reads the structure of a Repentance save file. Never writes one.
///
/// The layout was taken from what REPENTOGON logs while parsing
/// (<c>repentogon.log</c>, <c>[SaveFile]</c> lines) and checked against four
/// real files on 2026-09-04: a 16-byte magic <c>ISAACNGSAVE09R</c>, a u32,
/// then chunks each with a 12-byte header (type, size-as-written, element
/// count) followed by the elements. The size-as-written field is not the
/// on-disk length — item flags are written as one byte each while the field
/// counts four — so the element size is taken from a table per chunk type,
/// which is what REPENTOGON does too. The bestiary chunk is left opaque. The
/// last eight bytes are the save counter and a checksum.
///
/// Achievement chunk length differs by build — 642 on retail J460, 641 on
/// REPENTOGON's J273 — so it doubles as a version fingerprint.
/// </summary>
public static class SaveFileParser
{
    public const string Magic = "ISAACNGSAVE";
    private const int FirstChunkOffset = 20;
    private const int ChunkHeaderSize = 12;

    private static readonly Dictionary<int, (string Name, int ElementSize)> ChunkTypes = new()
    {
        [1] = ("Achievements", 1),
        [2] = ("Event counters", 4),
        [3] = ("Level counters", 4),
        [4] = ("Collectibles", 1),
        [5] = ("Minibosses", 1),
        [6] = ("Bosses", 1),
        [7] = ("Challenges", 1),
        [8] = ("Cutscenes", 4),
        [9] = ("Settings", 4),
        [10] = ("Special seeds", 1),
        [11] = ("Bestiary", 0),
    };

    public static SaveFileSummary ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllBytes(path));
        }
        catch (IOException ex)
        {
            return new SaveFileSummary { Parsed = false, Problem = ex.Message };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SaveFileSummary { Parsed = false, Problem = ex.Message };
        }
    }

    public static SaveFileSummary Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FirstChunkOffset + 8)
            return new SaveFileSummary { Parsed = false, Problem = "too short to be a save file", Length = bytes.Length };

        var header = Encoding.ASCII.GetString(bytes[..16]).TrimEnd(' ', '\0');
        if (!header.StartsWith(Magic, StringComparison.Ordinal))
            return new SaveFileSummary { Parsed = false, Problem = "not an Isaac save (bad header)", Header = header, Length = bytes.Length };

        var chunks = new List<SaveChunk>();
        var flags = new Dictionary<int, IReadOnlyList<int>>();
        var counters = Array.Empty<int>();
        var slots = new Dictionary<int, int>();

        var pos = FirstChunkOffset;
        while (pos + ChunkHeaderSize <= bytes.Length - 8)
        {
            var type = BinaryPrimitives.ReadInt32LittleEndian(bytes[pos..]);
            var sizeWritten = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 4)..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 8)..]);

            if (!ChunkTypes.TryGetValue(type, out var shape) || count < 0 || count > 1 << 20) break;

            var chunk = new SaveChunk(type, shape.Name, sizeWritten, count, pos + ChunkHeaderSize, shape.ElementSize);
            chunks.Add(chunk);
            if (type == 11) break;   // opaque; the counter and checksum follow at the very end

            var end = chunk.DataOffset + chunk.DataLength;
            if (end > bytes.Length - 8) break;

            var data = bytes[chunk.DataOffset..end];
            slots[type] = count;

            if (shape.ElementSize == 1)
            {
                var set = new List<int>();
                for (var i = 0; i < count; i++) if (data[i] != 0) set.Add(i);
                flags[type] = set;
            }
            else if (type == 2)
            {
                counters = new int[count];
                for (var i = 0; i < count; i++) counters[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]);
            }

            pos = end;
        }

        if (chunks.Count == 0)
            return new SaveFileSummary { Parsed = false, Problem = "no chunks found", Header = header, Length = bytes.Length };

        return new SaveFileSummary
        {
            Parsed = true,
            Header = header,
            Length = bytes.Length,
            Counter = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^8..]),
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]),
            Chunks = chunks,
            Achievements = Flags(flags, 1),
            Items = Flags(flags, 4),
            Minibosses = Flags(flags, 5),
            Bosses = Flags(flags, 6),
            Challenges = Flags(flags, 7),
            AchievementSlots = slots.GetValueOrDefault(1),
            ItemSlots = slots.GetValueOrDefault(4),
            BossSlots = slots.GetValueOrDefault(6),
            ChallengeSlots = slots.GetValueOrDefault(7),
            EventCounters = counters,
        };
    }

    private static IReadOnlyList<int> Flags(Dictionary<int, IReadOnlyList<int>> flags, int type) =>
        flags.TryGetValue(type, out var list) ? list : Array.Empty<int>();

    public static SaveDiff Compare(SaveFileSummary first, SaveFileSummary second) => new(
        first.Achievements.Except(second.Achievements).ToList(),
        second.Achievements.Except(first.Achievements).ToList(),
        first.Items.Except(second.Items).ToList(),
        second.Items.Except(first.Items).ToList(),
        first.Challenges.Except(second.Challenges).ToList(),
        second.Challenges.Except(first.Challenges).ToList());
}
