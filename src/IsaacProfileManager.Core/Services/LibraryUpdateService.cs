namespace IsaacProfileManager.Core.Services;

public enum UpdateState
{
    /// <summary>A hand-installed mod. Nothing upstream to compare against.</summary>
    NoWorkshopOrigin,

    /// <summary>The Workshop has not changed this item since we took our copy.</summary>
    UpToDate,

    /// <summary>The Workshop copy is newer than ours.</summary>
    UpdateAvailable,

    /// <summary>Removed, hidden or otherwise not returned by Steam.</summary>
    Unavailable,

    /// <summary>Not looked up — no answer either way.</summary>
    NotChecked,
}

/// <summary>One library entry measured against the Workshop.</summary>
public sealed record LibraryUpdateStatus(
    string Entry,
    string? WorkshopId,
    UpdateState State,
    string Title,
    DateTimeOffset? UpstreamUpdatedUtc,
    DateTimeOffset? BaselineUtc,
    long UpstreamFileSize)
{
    public bool NeedsUpdate => State == UpdateState.UpdateAvailable;

    /// <summary>
    /// True when the answer rests on the import date rather than a recorded
    /// revision. Steam may have downloaded the content before the import, so a
    /// mod updated in that gap reads as current. Fixed permanently the first
    /// time an entry goes through an update run.
    /// </summary>
    public bool BaselineIsImportDate { get; init; }

    public string Summary => State switch
    {
        UpdateState.UpdateAvailable => $"update available — changed {UpstreamUpdatedUtc:yyyy-MM-dd}",
        UpdateState.UpToDate => "up to date",
        UpdateState.Unavailable => "not on the Workshop any more",
        UpdateState.NoWorkshopOrigin => "local mod",
        _ => "not checked",
    };
}

/// <summary>
/// Compares the shared library against the Workshop.
///
/// Deliberately read-only and offline-tolerant: it answers "what changed" so a
/// resubscribe run can be aimed at the few items that need it. Subscribing to
/// all 40 to find the 9 that moved would materialise 40 folders into the active
/// profile for no reason.
/// </summary>
public sealed class LibraryUpdateService
{
    private readonly ModLibraryService _library;
    private readonly IWorkshopUpdateChecker _checker;

    public LibraryUpdateService(ModLibraryService library, IWorkshopUpdateChecker checker)
    {
        _library = library;
        _checker = checker;
    }

    public async Task<IReadOnlyList<LibraryUpdateStatus>> CheckAsync(
        IReadOnlyList<string>? entries = null,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        var names = entries ?? _library.ListEntries();

        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var local = new List<string>();

        foreach (var entry in names)
        {
            var id = _library.GetCachedId(entry);
            if (string.IsNullOrWhiteSpace(id)) { local.Add(entry); continue; }

            // Two entries can carry the same id — a collision resolved with the
            // id suffix — so both must be answered by one lookup.
            if (!byId.TryGetValue(id, out var sharing)) byId[id] = sharing = new List<string>();
            sharing.Add(entry);
        }

        var results = new List<LibraryUpdateStatus>();
        foreach (var entry in local)
            results.Add(new LibraryUpdateStatus(entry, null, UpdateState.NoWorkshopOrigin,
                                                _library.GetCachedName(entry) ?? entry, null, null, 0));

        if (byId.Count > 0)
        {
            progress?.Report($"Asking Steam about {byId.Count} Workshop items");
            var details = await _checker.FetchAsync(byId.Keys.ToList(), cancellation).ConfigureAwait(false);

            foreach (var (id, sharing) in byId)
                foreach (var entry in sharing)
                    results.Add(Compare(entry, id, details.GetValueOrDefault(id)));
        }

        return results.OrderBy(r => r.Entry, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private LibraryUpdateStatus Compare(string entry, string id, WorkshopFileDetails? upstream)
    {
        var info = _library.Describe(entry, measure: false);
        var title = info.Name;

        var (baseline, fromImportDate) = Baseline(info);

        if (upstream is null)
            return new LibraryUpdateStatus(entry, id, UpdateState.NotChecked, title, null, baseline, 0);

        if (!upstream.Available)
            return new LibraryUpdateStatus(entry, id, UpdateState.Unavailable, title,
                                           upstream.UpdatedUtc, baseline, upstream.FileSize);

        var state = upstream.UpdatedUtc is { } updated && baseline is { } known && updated > known
            ? UpdateState.UpdateAvailable
            : UpdateState.UpToDate;

        // With no baseline at all we cannot claim it is current.
        if (baseline is null) state = UpdateState.NotChecked;

        return new LibraryUpdateStatus(entry, id, state, upstream.Title.Length > 0 ? upstream.Title : title,
                                       upstream.UpdatedUtc, baseline, upstream.FileSize)
        {
            BaselineIsImportDate = fromImportDate,
        };
    }

    /// <summary>
    /// The revision we believe we hold: the recorded Workshop stamp, or failing
    /// that the import date.
    /// </summary>
    private static (DateTimeOffset? Baseline, bool FromImportDate) Baseline(LibraryEntryInfo info)
    {
        if (info.UpstreamTimeUpdated > 0)
            return (DateTimeOffset.FromUnixTimeSeconds(info.UpstreamTimeUpdated), false);

        if (!string.IsNullOrWhiteSpace(info.ImportedUtc) &&
            DateTimeOffset.TryParse(info.ImportedUtc, null,
                                    System.Globalization.DateTimeStyles.AssumeUniversal |
                                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var imported))
            return (imported, true);

        return (null, false);
    }
}
