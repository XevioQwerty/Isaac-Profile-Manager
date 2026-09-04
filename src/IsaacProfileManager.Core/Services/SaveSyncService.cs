using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

public enum SyncRelation
{
    /// <summary>Nothing anywhere else. Push to share it.</summary>
    LocalOnly,

    /// <summary>Only on the other device(s). Pull to have it here.</summary>
    RemoteOnly,

    /// <summary>The newest lane is this revision.</summary>
    Equal,

    /// <summary>This machine has captured since the newest lane. Push.</summary>
    LocalAhead,

    /// <summary>Another device has captured since this revision. Pull.</summary>
    RemoteAhead,

    /// <summary>
    /// Both played from the same point. Never resolved automatically: the
    /// remote revision can be pulled as a separate set and both kept.
    /// </summary>
    Fork,
}

public sealed record SetSyncStatus(
    string SetName,
    SyncRelation Relation,
    Dictionary<string, int>? LocalClock,
    LaneManifest? Newest,
    IReadOnlyList<LaneManifest> Lanes)
{
    public int LocalRevision => VectorClock.Revision(LocalClock);
    public int RemoteRevision => Newest?.Revision ?? 0;
    public bool NeedsPush => Relation is SyncRelation.LocalOnly or SyncRelation.LocalAhead;
    public bool CanPull => Relation is SyncRelation.RemoteOnly or SyncRelation.RemoteAhead or SyncRelation.Fork;

    public string Text => Relation switch
    {
        SyncRelation.LocalOnly => $"only here (rev {LocalRevision}) — not pushed yet",
        SyncRelation.RemoteOnly => $"only on {Newest!.DeviceName} (rev {RemoteRevision})",
        SyncRelation.Equal => $"in step (rev {LocalRevision})",
        SyncRelation.LocalAhead => $"ahead of {Newest!.DeviceName}: rev {LocalRevision} here, {RemoteRevision} there — push",
        SyncRelation.RemoteAhead => $"{Newest!.DeviceName} is ahead: rev {RemoteRevision} there, {LocalRevision} here — pull",
        SyncRelation.Fork => $"forked: rev {LocalRevision} here and rev {RemoteRevision} on {Newest!.DeviceName} both grew from the same point",
        _ => string.Empty,
    };
}

/// <summary>
/// Keeps save sets in step across your own machines, over any
/// <see cref="ISaveLaneStore"/>.
///
/// Push exports the set as a pack into this device's lane. Status compares
/// the local revision's vector clock against every lane and names the
/// relation. Pull replaces the local set's contents with a lane's pack, filing
/// what was there into history first — or, for a fork, brings the lane in as
/// a separate set so both survive. Nothing here touches the live save folder;
/// activation stays behind its gates.
/// </summary>
public sealed class SaveSyncService
{
    private readonly SaveSetService _sets;
    private readonly ISaveLaneStore _store;
    private readonly DeviceIdentity _device;

    public SaveSyncService(SaveSetService sets, ISaveLaneStore store, DeviceIdentity device)
    {
        _sets = sets;
        _store = store;
        _device = device;
    }

    public ISaveLaneStore Store => _store;

    public async Task<IReadOnlyList<SetSyncStatus>> StatusAsync(CancellationToken ct = default)
    {
        var lanes = await _store.ListAsync(ct);
        var result = new List<SetSyncStatus>();

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _sets.ListSets()) names.Add(name);
        foreach (var lane in lanes) names.Add(lane.SetName);

        foreach (var name in names)
        {
            SaveSet? local = null;
            try { local = _sets.LoadSet(name); }
            catch (ConfigSchemaMismatchException) { }

            var laneList = lanes.Where(l => string.Equals(l.SetName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            result.Add(Classify(name, local, laneList));
        }

        return result;
    }

    public async Task<SetSyncStatus?> StatusOfAsync(string setName, CancellationToken ct = default) =>
        (await StatusAsync(ct)).FirstOrDefault(s => string.Equals(s.SetName, setName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The newest lane is the one no other lane is ahead of. Two lanes that are
    /// forks of each other are reported as a fork whichever is newer by count,
    /// because "newer" has no meaning between them.
    /// </summary>
    private SetSyncStatus Classify(string name, SaveSet? local, IReadOnlyList<LaneManifest> lanes)
    {
        var localClock = local is null || local.Files.Count == 0 ? null : local.Clock;

        if (lanes.Count == 0)
            return new SetSyncStatus(name, localClock is null ? SyncRelation.Equal : SyncRelation.LocalOnly, localClock, null, lanes);

        var newest = lanes.OrderByDescending(l => l.Revision).ThenBy(l => l.DeviceId, StringComparer.Ordinal).First();
        var lanesForked = lanes.Any(l => !ReferenceEquals(l, newest) && VectorClock.Compare(l.Clock, newest.Clock) == ClockRelation.Fork);

        if (localClock is null)
            return new SetSyncStatus(name, SyncRelation.RemoteOnly, null, newest, lanes);

        var relation = VectorClock.Compare(localClock, newest.Clock) switch
        {
            ClockRelation.Equal => SyncRelation.Equal,
            ClockRelation.Ahead => SyncRelation.LocalAhead,
            ClockRelation.Behind => SyncRelation.RemoteAhead,
            _ => SyncRelation.Fork,
        };

        if (lanesForked && relation != SyncRelation.LocalAhead) relation = SyncRelation.Fork;
        return new SetSyncStatus(name, relation, localClock, newest, lanes);
    }

    /// <summary>Export the set and write it into this device's lane.</summary>
    public async Task<LaneManifest> PushAsync(string setName, CancellationToken ct = default)
    {
        var set = _sets.LoadSet(setName) ?? throw new SaveSyncException($"No save set called '{setName}'.");
        if (set.Files.Count == 0) throw new SaveSyncException($"'{setName}' has no save in it yet; there is nothing to push.");

        var temp = Path.Combine(Path.GetTempPath(), "ipm-sync", Guid.NewGuid().ToString("N") + SaveSetService.PackExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            _sets.ExportPack(setName, temp);
            var manifest = new LaneManifest
            {
                SetName = set.Name,
                DeviceId = _device.Id,
                DeviceName = _device.Name,
                Clock = new Dictionary<string, int>(set.Clock),
                CapturedUtc = set.CapturedUtc,
                PushedUtc = DateTime.UtcNow.ToString("o"),
                GameVersion = set.GameVersion,
                Build = set.Build,
                PackBytes = new FileInfo(temp).Length,
                PackSha1 = SaveSetService.Sha1Of(temp),
            };
            await _store.PushAsync(manifest, temp, ct);
            return manifest;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Take a lane's revision as the local set's contents. Refuses a fork —
    /// use <see cref="PullAsCopyAsync"/> for that, which keeps both.
    /// </summary>
    public async Task<SaveSet> PullAsync(string setName, LaneManifest lane, CancellationToken ct = default)
    {
        var local = _sets.LoadSet(setName);
        if (local is not null && local.Files.Count > 0 && VectorClock.Compare(local.Clock, lane.Clock) == ClockRelation.Fork)
            throw new SaveSyncException(
                $"'{setName}' here and the revision on {lane.DeviceName} both grew from the same point. " +
                "Neither can replace the other safely; pull it as a copy and pick.");

        var temp = await Download(lane, ct);
        try
        {
            var pulled = local is null ? _sets.ImportPack(temp, setName) : _sets.ReplaceFromPack(setName, temp);
            var verify = SaveSetService.Sha1Of(temp);
            if (lane.PackSha1.Length > 0 && !string.Equals(verify, lane.PackSha1, StringComparison.OrdinalIgnoreCase))
                throw new SaveSyncException("The pack that arrived does not match what the other device pushed. Try again.");
            return pulled;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Bring a lane in as a separate set, named after the device it came from, and leave the local one alone.</summary>
    public async Task<SaveSet> PullAsCopyAsync(string setName, LaneManifest lane, CancellationToken ct = default)
    {
        var temp = await Download(lane, ct);
        try
        {
            var baseName = $"{setName} (from {DeviceService.SafeName(lane.DeviceName)})";
            var name = baseName;
            for (var n = 2; _sets.ListSets().Contains(name, StringComparer.OrdinalIgnoreCase); n++)
                name = $"{baseName} {n}";
            return _sets.ImportPack(temp, name);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private async Task<string> Download(LaneManifest lane, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ipm-sync", Guid.NewGuid().ToString("N") + SaveSetService.PackExtension);
        await _store.PullAsync(lane.SetName, lane.DeviceId, temp, ct);
        if (lane.PackSha1.Length > 0 && !string.Equals(SaveSetService.Sha1Of(temp), lane.PackSha1, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw new SaveSyncException($"The pack for '{lane.SetName}' from {lane.DeviceName} arrived damaged or half-written. Try again in a moment.");
        }
        return temp;
    }
}
