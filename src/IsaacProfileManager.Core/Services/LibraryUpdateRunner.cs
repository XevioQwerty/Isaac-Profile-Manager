namespace IsaacProfileManager.Core.Services;

/// <summary>What an update run did, entry by entry.</summary>
public sealed record UpdateRunReport(
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Backups)
{
    public bool AnythingChanged => Updated.Count > 0;
}

/// <summary>
/// Resubscribes to the Workshop items behind chosen library entries, takes the
/// updated content, and unsubscribes again.
///
/// The subscription is a means, not the goal: the library holds detached copies
/// precisely so Steam cannot re-lay them into a profile, and it goes straight
/// back to that state. The window is kept to the items that actually changed,
/// which is why <see cref="LibraryUpdateService"/> runs first.
///
/// The unsubscribe is in a finally block. Leaving an account subscribed after a
/// crash is the one outcome that quietly breaks the invariant this whole tool
/// rests on — a subscribed item missing from the active profile gets
/// re-downloaded into it.
/// </summary>
public sealed class LibraryUpdateRunner
{
    private readonly ModLibraryService _library;
    private readonly IWorkshopPullService _pull;
    private readonly IGameProcessService _process;

    public LibraryUpdateRunner(ModLibraryService library, IWorkshopPullService pull, IGameProcessService process)
    {
        _library = library;
        _pull = pull;
        _process = process;
    }

    public async Task<UpdateRunReport> RunAsync(
        IReadOnlyList<string> entries,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        var updated = new List<string>();
        var failed = new List<string>();
        var warnings = new List<string>();
        var backups = new List<string>();

        if (entries.Count == 0)
            return new UpdateRunReport(updated, failed, new[] { "Nothing selected." }, backups);

        // Launching the game is what makes Steam materialise subscribed items
        // into mods\, which points at a profile. Subscribing while it runs is
        // how a profile silently gains folders.
        if (_process.IsIsaacRunning())
            return new UpdateRunReport(updated, failed,
                new[] { "Isaac is running. Close it before updating — Steam re-lays subscribed mods into the active profile on launch." },
                backups);

        if (!_pull.IsAvailable)
            return new UpdateRunReport(updated, failed,
                new[] { "The Steam helper is missing, so nothing can be resubscribed." }, backups);

        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = _library.GetCachedId(entry);
            if (string.IsNullOrWhiteSpace(id)) { failed.Add($"{entry}: no Workshop id recorded."); continue; }
            byId[id] = entry;
        }

        if (byId.Count == 0)
            return new UpdateRunReport(updated, failed, warnings, backups);

        var ids = byId.Keys.ToList();
        PullResult pulled;

        try
        {
            progress?.Report($"Resubscribing to {ids.Count} item(s)");
            pulled = await _pull.PullAsync(ids, progress, cancellation).ConfigureAwait(false);

            foreach (var item in pulled.Items)
            {
                var entry = byId[item.Id];

                if (!item.Installed)
                {
                    failed.Add($"{entry}: Steam reported '{item.State}'.");
                    continue;
                }

                try
                {
                    progress?.Report($"Updating {entry}");

                    // The install timestamp is Steam's own view of which revision
                    // this content is, and matched the Workshop's time_updated
                    // exactly when verified. Recording it means the next check
                    // compares revisions rather than guessing from import dates.
                    var backup = _library.UpdateFromContent(entry, item.Path, item.Timestamp, item.SizeOnDisk, progress);
                    updated.Add(entry);
                    if (backup is not null) backups.Add(backup);
                }
                catch (Exception ex) when (ex is IOException or UnsafePathException or UnauthorizedAccessException)
                {
                    failed.Add($"{entry}: {ex.Message}");
                }
            }

            foreach (var error in pulled.Errors) warnings.Add(error);
        }
        finally
        {
            progress?.Report("Unsubscribing");
            var cleanup = await _pull.UnsubscribeAsync(ids, progress, CancellationToken.None).ConfigureAwait(false);

            foreach (var error in cleanup.Errors) warnings.Add(error);
            if (cleanup.SubscribedAfter > 0)
                warnings.Add($"Steam still reports {cleanup.SubscribedAfter} subscription(s). " +
                             "Check the Workshop tab — anything left subscribed will be re-laid into the active profile on launch.");
        }

        if (updated.Count > 0)
            warnings.Add($"{updated.Count} mod(s) changed on disk, so their hashes changed too. " +
                         "Everyone you play with has to take the same update or you will desync.");

        return new UpdateRunReport(updated, failed, warnings, backups);
    }
}
