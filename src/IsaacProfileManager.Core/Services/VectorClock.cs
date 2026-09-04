namespace IsaacProfileManager.Core.Services;

/// <summary>How one copy of a save set relates to another.</summary>
public enum ClockRelation
{
    /// <summary>Same revision.</summary>
    Equal,

    /// <summary>Ours is strictly newer: safe to keep.</summary>
    Ahead,

    /// <summary>Theirs is strictly newer: safe to take.</summary>
    Behind,

    /// <summary>
    /// Both machines captured from the same starting point. Two divergent
    /// unlock states cannot be merged correctly, so this is surfaced and never
    /// resolved automatically.
    /// </summary>
    Fork,
}

/// <summary>
/// A vector clock over device ids. About fifteen lines, and the difference
/// between "sync" and "sometimes eats your unlocks".
/// </summary>
public static class VectorClock
{
    public static ClockRelation Compare(IReadOnlyDictionary<string, int>? ours, IReadOnlyDictionary<string, int>? theirs)
    {
        ours ??= new Dictionary<string, int>();
        theirs ??= new Dictionary<string, int>();

        var anyGreater = false;
        var anyLess = false;

        foreach (var device in ours.Keys.Union(theirs.Keys, StringComparer.Ordinal))
        {
            var o = ours.TryGetValue(device, out var ov) ? ov : 0;
            var t = theirs.TryGetValue(device, out var tv) ? tv : 0;
            if (o > t) anyGreater = true;
            if (o < t) anyLess = true;
        }

        return (anyGreater, anyLess) switch
        {
            (false, false) => ClockRelation.Equal,
            (true, false) => ClockRelation.Ahead,
            (false, true) => ClockRelation.Behind,
            _ => ClockRelation.Fork,
        };
    }

    /// <summary>A copy of the clock with this device's counter advanced by one.</summary>
    public static Dictionary<string, int> Bump(IReadOnlyDictionary<string, int>? clock, string device)
    {
        var next = clock is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(clock, StringComparer.Ordinal);

        next[device] = next.TryGetValue(device, out var current) ? current + 1 : 1;
        return next;
    }

    /// <summary>Total number of captures across every device, for display.</summary>
    public static int Revision(IReadOnlyDictionary<string, int>? clock) =>
        clock is null ? 0 : clock.Values.Sum();
}
