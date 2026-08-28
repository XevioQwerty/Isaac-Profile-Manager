namespace IsaacProfileManager.Core.Services;

public enum ShareItemAction
{
    /// <summary>Already in the library with a matching hash. Left alone.</summary>
    AlreadyMatches,

    /// <summary>Present, but the bytes differ from the sender's. Will be refetched.</summary>
    Differs,

    /// <summary>Not in the library at all. Will be fetched.</summary>
    Missing,

    /// <summary>Present, and the share carries no hash to check it against.</summary>
    PresentUnverified,

    /// <summary>No Workshop id, so nothing can fetch it. The sender must send the folder.</summary>
    Unfetchable,
}

/// <summary>One entry in a share, and what importing it would do.</summary>
public sealed record ShareItemPlan(string Entry, string? WorkshopId, ShareItemAction Action)
{
    public bool NeedsFetch => Action is ShareItemAction.Differs or ShareItemAction.Missing;

    public string Summary => Action switch
    {
        ShareItemAction.AlreadyMatches => "already have it, identical",
        ShareItemAction.Differs => "have a different version — will refetch",
        ShareItemAction.Missing => "will download",
        ShareItemAction.PresentUnverified => "already have it (no hash to check against)",
        ShareItemAction.Unfetchable => "NOT on the Workshop — ask them to send this folder",
        _ => string.Empty,
    };
}

/// <summary>What an import would do, before it does any of it.</summary>
public sealed record SharePlan(string Name, string Notes, IReadOnlyList<ShareItemPlan> Items)
{
    public IReadOnlyList<ShareItemPlan> ToFetch => Items.Where(i => i.NeedsFetch).ToList();
    public IReadOnlyList<ShareItemPlan> Unfetchable => Items.Where(i => i.Action == ShareItemAction.Unfetchable).ToList();

    public int AlreadyHave => Items.Count(i => i.Action is ShareItemAction.AlreadyMatches or ShareItemAction.PresentUnverified);

    public string Summary =>
        $"{Items.Count} mods — {ToFetch.Count} to download, {AlreadyHave} already here" +
        (Unfetchable.Count > 0 ? $", {Unfetchable.Count} not on the Workshop" : "");
}

/// <summary>The outcome of running a plan.</summary>
public sealed record ShareImportReport(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> HashMismatches,
    string? ProfileWritten)
{
    public bool AnythingChanged => Installed.Count > 0;
}

/// <summary>
/// Rebuilds someone else's mod set on this machine from a share code.
///
/// The fetch is the same resubscribe cycle updates use — subscribe, download,
/// copy into the library, unsubscribe — so the account ends up back where it
/// started and no profile gains a folder behind the user's back.
///
/// Mods already present with a matching hash are skipped. That is not only
/// faster: refetching a mod that already matches would replace bytes that are
/// already provably correct, which is a needless risk.
/// </summary>
public sealed class ShareImportRunner
{
    private readonly ModLibraryService _library;
    private readonly LibraryHashService _hashes;
    private readonly IWorkshopPullService _pull;
    private readonly IGameProcessService _process;

    public ShareImportRunner(ModLibraryService library, IWorkshopPullService pull, IGameProcessService process)
    {
        _library = library;
        _hashes = new LibraryHashService(library);
        _pull = pull;
        _process = process;
    }

    /// <summary>
    /// Work out what importing would do. Reads only, so it is safe to show the
    /// user before they commit to anything.
    /// </summary>
    public SharePlan Plan(SharedProfile share)
    {
        var present = _library.ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = _hashes.LoadHashes();
        var items = new List<ShareItemPlan>();

        foreach (var entry in share.Mods)
        {
            share.WorkshopIds.TryGetValue(entry, out var id);

            if (string.IsNullOrWhiteSpace(id))
            {
                items.Add(new ShareItemPlan(entry, null, ShareItemAction.Unfetchable));
                continue;
            }

            if (!present.Contains(entry))
            {
                items.Add(new ShareItemPlan(entry, id, ShareItemAction.Missing));
                continue;
            }

            if (!share.Hashes.TryGetValue(entry, out var theirs) || string.IsNullOrWhiteSpace(theirs))
            {
                items.Add(new ShareItemPlan(entry, id, ShareItemAction.PresentUnverified));
                continue;
            }

            // Compare against what was recorded rather than rehashing every mod
            // here — this runs to draw a dialog, and hashing a library is slow.
            var mine = recorded.GetValueOrDefault(entry);
            items.Add(new ShareItemPlan(entry, id,
                mine is not null && mine == theirs ? ShareItemAction.AlreadyMatches : ShareItemAction.Differs));
        }

        return new SharePlan(share.Name, share.Notes, items);
    }

    /// <summary>
    /// Fetch what the plan says is needed, then optionally write and materialise
    /// a profile from the whole set.
    /// </summary>
    public async Task<ShareImportReport> RunAsync(
        SharedProfile share,
        SharePlan plan,
        string? profileName,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        var installed = new List<string>();
        var failed = new List<string>();
        var warnings = new List<string>();
        var mismatches = new List<string>();

        if (_process.IsIsaacRunning())
            return new ShareImportReport(installed, failed,
                new[] { "Isaac is running. Close it first — Steam re-lays subscribed mods into the active profile on launch." },
                mismatches, null);

        var toFetch = plan.ToFetch;

        if (toFetch.Count > 0)
        {
            if (!_pull.IsAvailable)
                return new ShareImportReport(installed, failed,
                    new[] { "The Steam helper is missing, so nothing can be downloaded." }, mismatches, null);

            var byId = toFetch.Where(i => i.WorkshopId is not null)
                              .ToDictionary(i => i.WorkshopId!, i => i.Entry, StringComparer.Ordinal);
            var ids = byId.Keys.ToList();

            try
            {
                progress?.Report($"Subscribing to {ids.Count} mod(s)");
                var pulled = await _pull.PullAsync(ids, progress, cancellation).ConfigureAwait(false);

                foreach (var item in pulled.Items)
                {
                    var entry = byId[item.Id];

                    if (!item.Installed)
                    {
                        failed.Add(ExplainState(entry, item.State));
                        continue;
                    }

                    try
                    {
                        progress?.Report($"Installing {entry}");
                        _library.InstallFromShare(entry, item.Path, item.Id, item.Timestamp, item.SizeOnDisk, progress);
                        installed.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnsafePathException or UnauthorizedAccessException)
                    {
                        failed.Add($"{entry}: {ex.Message}");
                    }
                }

                foreach (var error in pulled.Errors) warnings.Add(error);
                foreach (var note in pulled.Warnings) warnings.Add(note);

                if (pulled.OwnsApp == false)
                    warnings.Add("This Steam account does not own The Binding of Isaac: Rebirth. Steam will " +
                                 "not let it subscribe to Workshop items, which is why nothing downloaded.");
            }
            finally
            {
                progress?.Report("Unsubscribing");
                var cleanup = await _pull.UnsubscribeAsync(ids, progress, CancellationToken.None).ConfigureAwait(false);

                foreach (var error in cleanup.Errors) warnings.Add(error);
                if (cleanup.SubscribedAfter > 0)
                    warnings.Add($"Steam still reports {cleanup.SubscribedAfter} subscription(s). " +
                                 "Anything left subscribed will be re-laid into the active profile on launch.");
            }
        }

        if (plan.Unfetchable.Count > 0)
            warnings.Add($"{plan.Unfetchable.Count} mod(s) are not Workshop items and could not be downloaded: " +
                         string.Join(", ", plan.Unfetchable.Select(i => i.Entry)) +
                         ". Ask whoever sent this for those folders.");

        // Verify what arrived against the sender's hashes. This is the point of
        // sharing a code rather than a folder listing: same name, different
        // bytes is a listed desync cause and is invisible to a name comparison.
        if (installed.Count > 0)
        {
            progress?.Report("Hashing what arrived");
            _hashes.RecordAll(progress, cancellation);
            var mine = _hashes.LoadHashes();

            foreach (var entry in installed)
            {
                if (!share.Hashes.TryGetValue(entry, out var theirs) || string.IsNullOrWhiteSpace(theirs)) continue;
                if (mine.GetValueOrDefault(entry) != theirs) mismatches.Add(entry);
            }

            if (mismatches.Count > 0)
                warnings.Add($"{mismatches.Count} mod(s) do not match the sender's hashes even after downloading. " +
                             "The Workshop version has probably moved on since they made the code.");
        }

        string? written = null;
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            // Only mods actually in the library go into the manifest — pointing a
            // profile at an entry that failed to arrive would silently short it.
            var available = _library.ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mods = share.Mods.Where(available.Contains).ToList();

            _library.SaveManifest(profileName, new ProfileManifest { Mods = mods, Notes = share.Notes });
            var report = _library.Materialise(profileName, _library.LoadManifest(profileName));
            written = profileName;

            progress?.Report($"Built '{profileName}' with {mods.Count} mods");

            var short_ = share.Mods.Count - mods.Count;
            if (short_ > 0)
                warnings.Add($"'{profileName}' was built with {short_} fewer mod(s) than the share lists, " +
                             "because those did not arrive.");

            foreach (var name in report.LeftAlone)
                warnings.Add($"'{name}' in {profileName} is a real folder, not a link, and was left alone.");
        }

        return new ShareImportReport(installed, failed, warnings, mismatches, written);
    }

    /// <summary>Turn a helper item state into something worth reading.</summary>
    private static string ExplainState(string entry, string state) => state switch
    {
        "not-subscribed" => $"{entry}: Steam never registered the subscription. The item may have been removed " +
                            "from the Workshop, or this account cannot get it.",
        "timeout" => $"{entry}: the download did not finish in time.",
        "missing" => $"{entry}: Steam reported it installed but could not say where.",
        _ => $"{entry}: Steam reported '{state}'.",
    };
}
