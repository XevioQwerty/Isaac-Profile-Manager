using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

public enum LiveSaveState
{
    /// <summary>The save folder could not be found.</summary>
    NoSaveFolder,

    /// <summary>The folder exists and holds no save files — the game will write a fresh one.</summary>
    NoSaves,

    /// <summary>Every file of a set is live and byte-identical, and nothing else is.</summary>
    Exact,

    /// <summary>A set is live but has been played since it was captured.</summary>
    Drifted,

    /// <summary>Live files match no set.</summary>
    Unrecognised,
}

public sealed record LiveIdentity(LiveSaveState State, SaveSet? Set, IReadOnlyList<string> Drift, int LiveFileCount)
{
    public bool HasSet => Set is not null;

    public string Text => State switch
    {
        LiveSaveState.NoSaveFolder => "save folder not found",
        LiveSaveState.NoSaves => "no saves — the game will write a fresh one",
        LiveSaveState.Exact => Set!.Name,
        LiveSaveState.Drifted => $"{Set!.Name} (played since last capture)",
        _ => "not a known save set",
    };
}

/// <summary>
/// Says which save set is live, by hashing the live folder against every set.
///
/// The config remembers what was last activated, but that is a hint: the
/// PowerShell tool or a hand copy can change the live folder behind the app,
/// and a hint that is wrong would let the launch guard wave through a
/// cross-build load. The hashes are the truth; the hint only names a set whose
/// bytes have moved on since it was captured.
/// </summary>
public sealed class SaveIdentityService
{
    private readonly SaveSetService _sets;

    public SaveIdentityService(SaveSetService sets) => _sets = sets;

    public LiveIdentity Identify(string? hint)
    {
        var folder = _sets.LiveFolder;
        if (folder is null || !Directory.Exists(folder))
            return new LiveIdentity(LiveSaveState.NoSaveFolder, null, Array.Empty<string>(), 0);

        var live = _sets.ReadLive().ToDictionary(f => f.FileName, f => f.Sha1, StringComparer.OrdinalIgnoreCase);
        if (live.Count == 0)
            return new LiveIdentity(LiveSaveState.NoSaves, null, Array.Empty<string>(), 0);

        var sets = new List<SaveSet>();
        foreach (var name in _sets.ListSets())
        {
            try
            {
                var set = _sets.LoadSet(name);
                if (set is not null && set.Files.Count > 0) sets.Add(set);
            }
            catch (ConfigSchemaMismatchException)
            {
                // A set this build cannot read cannot be the answer either way.
            }
        }

        // Exact first, hint breaking a tie between identical sets.
        SaveSet? exact = null;
        SaveSet? exactButExtras = null;
        IReadOnlyList<string> extrasOf = Array.Empty<string>();

        foreach (var set in Ordered(sets, hint))
        {
            if (!AllPresentAndEqual(set, live)) continue;

            var extras = live.Keys
                .Where(k => !set.Sha1.ContainsKey(k) && !set.Files.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (extras.Count == 0) { exact = set; break; }
            if (exactButExtras is null) { exactButExtras = set; extrasOf = extras; }
        }

        if (exact is not null)
            return new LiveIdentity(LiveSaveState.Exact, exact, Array.Empty<string>(), live.Count);

        // Unlock files identical but something extra is live — typically a run
        // in progress (rep+gamestate1.dat) that was not there at capture.
        if (exactButExtras is not null)
            return new LiveIdentity(LiveSaveState.Drifted, exactButExtras, extrasOf, live.Count);

        // No set matches by bytes. The hint can still say which set this is a
        // continuation of, provided its files are the ones that are live.
        var hinted = sets.FirstOrDefault(s => string.Equals(s.Name, hint, StringComparison.OrdinalIgnoreCase));
        if (hinted is not null && PersistentFilesPresent(hinted, live))
        {
            var drift = hinted.Sha1.Keys
                .Where(k => !live.TryGetValue(k, out var sha) || !string.Equals(sha, hinted.Sha1[k], StringComparison.OrdinalIgnoreCase))
                .Concat(live.Keys.Where(k => !hinted.Sha1.ContainsKey(k)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new LiveIdentity(LiveSaveState.Drifted, hinted, drift, live.Count);
        }

        return new LiveIdentity(LiveSaveState.Unrecognised, null, Array.Empty<string>(), live.Count);
    }

    private static IEnumerable<SaveSet> Ordered(IEnumerable<SaveSet> sets, string? hint) =>
        sets.OrderByDescending(s => string.Equals(s.Name, hint, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

    private static bool AllPresentAndEqual(SaveSet set, IReadOnlyDictionary<string, string> live)
    {
        if (set.Sha1.Count == 0) return false;
        foreach (var (file, sha) in set.Sha1)
        {
            if (!live.TryGetValue(file, out var liveSha)) return false;
            if (!string.Equals(liveSha, sha, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>The set's unlock files (not the transient run state) all exist live, whatever their bytes.</summary>
    private static bool PersistentFilesPresent(SaveSet set, IReadOnlyDictionary<string, string> live)
    {
        var persistent = set.Files.Where(f => f.Contains("persistentgamedata", StringComparison.OrdinalIgnoreCase)).ToList();
        return persistent.Count > 0 && persistent.All(live.ContainsKey);
    }
}
