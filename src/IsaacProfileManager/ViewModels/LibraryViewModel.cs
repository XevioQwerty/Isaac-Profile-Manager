using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>A library mod in the browser list.</summary>
public sealed class LibraryModViewModel : ObservableObject
{
    private bool _inProfile;

    public required LibraryEntryInfo Info { get; init; }
    public required IReadOnlyList<string> UsedBy { get; init; }

    public string Entry => Info.Entry;
    public string Name => Info.Name;
    /// <summary>
    /// Stripped of BBCode. Authors paste their whole Workshop page into
    /// metadata.xml, so raw this is a wall of [h2] and [url=...] tags.
    /// </summary>
    public string Description => BbCode.Strip(Info.Description);
    public string? PreviewPath => Info.PreviewPath;
    public string? WorkshopId => Info.WorkshopId;
    public string Path => Info.Path;

    /// <summary>Whether the profile currently being built includes this mod.</summary>
    public bool InProfile
    {
        get => _inProfile;
        set => SetField(ref _inProfile, value);
    }

    public string SubtitleText =>
        string.Equals(Entry, Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : Entry;

    public string UsedByText => UsedBy.Count == 0
        ? "not in any profile"
        : $"in {string.Join(", ", UsedBy)}";

    public string OriginText => Info.HasWorkshopOrigin ? $"Workshop {Info.WorkshopId}" : "local mod";

    public bool IsOrphan => UsedBy.Count == 0;

    private LibraryUpdateStatus? _update;

    /// <summary>What the last update check said, if one has run.</summary>
    public LibraryUpdateStatus? Update
    {
        get => _update;
        set
        {
            if (!SetField(ref _update, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate => Update?.NeedsUpdate == true;

    public string UpdateText => Update is null
        ? string.Empty
        : Update.BaselineIsImportDate && Update.State == UpdateState.UpToDate
            ? "up to date (judged from the import date)"
            : Update.Summary;
}

/// <summary>
/// Browses the shared library and builds a profile out of it.
///
/// This is the wide view of the same data the profile tab edits in miniature —
/// the manifest for the chosen profile is the thing being written, and
/// materialising it rebuilds that profile's junctions.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private LibraryModViewModel? _selected;
    private string _search = string.Empty;
    private string? _targetProfile;
    private bool _showOnlyIncluded;
    private string _detailSizeText = string.Empty;

    public LibraryViewModel(MainViewModel shell)
    {
        _shell = shell;

        View = CollectionViewSource.GetDefaultView(Mods);
        View.Filter = o => o is LibraryModViewModel m && Matches(m);

        ApplyCommand = new RelayCommand(Apply, () => TargetProfile is not null && HasLibrary);
        IncludeAllCommand = new RelayCommand(() => SetVisible(true), () => TargetProfile is not null);
        IncludeNoneCommand = new RelayCommand(() => SetVisible(false), () => TargetProfile is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected is not null);
        OpenLibraryCommand = new RelayCommand(OpenLibrary, () => HasLibrary);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected is { IsOrphan: true });

        RecordHashesCommand = new RelayCommand(async () => await HashAsync(verifyOnly: false), () => HasLibrary && !_shell.IsBusy);
        VerifyCommand = new RelayCommand(async () => await HashAsync(verifyOnly: true), () => HasLibrary && !_shell.IsBusy);
        ExportProfileCommand = new RelayCommand(ExportProfile, () => TargetProfile is not null);
        CompareCommand = new RelayCommand(CompareWithExport, () => TargetProfile is not null);

        ShareProfileCommand = new RelayCommand(() => CopyShareCode(wholeLibrary: false), () => TargetProfile is not null);
        ShareLibraryCommand = new RelayCommand(() => CopyShareCode(wholeLibrary: true), () => HasLibrary);
        ImportShareCommand = new RelayCommand(ImportShare, () => _shell.Config?.SyncRoot is not null && !_shell.IsBusy);

        CheckUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync(), () => HasLibrary && !_shell.IsBusy);
        UpdateStaleCommand = new RelayCommand(async () => await UpdateAsync(StaleEntries()), () => HasStale && !_shell.IsBusy);
        UpdateSelectedCommand = new RelayCommand(
            async () => await UpdateAsync(Selected is null ? Array.Empty<string>() : new[] { Selected.Entry }),
            () => Selected is { HasUpdate: true } && !_shell.IsBusy);
    }

    public RelayCommand ShareProfileCommand { get; }
    public RelayCommand ShareLibraryCommand { get; }
    public RelayCommand ImportShareCommand { get; }

    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand UpdateStaleCommand { get; }
    public RelayCommand UpdateSelectedCommand { get; }

    public RelayCommand RecordHashesCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand CompareCommand { get; }

    /// <summary>Lines from the last verify or compare, shown as-is.</summary>
    public ObservableCollection<string> Report { get; } = new();

    private string _reportTitle = string.Empty;
    private string _hashProgress = string.Empty;

    public string ReportTitle
    {
        get => _reportTitle;
        private set => SetField(ref _reportTitle, value);
    }

    public bool HasReport => Report.Count > 0;

    public string HashProgress
    {
        get => _hashProgress;
        private set => SetField(ref _hashProgress, value);
    }

    /// <summary>
    /// Hash the library so two people can prove they are running the same files.
    /// Identical folder names are not enough — same name with different contents
    /// is a listed desync cause and is invisible to a folder listing.
    /// </summary>
    private async Task HashAsync(bool verifyOnly)
    {
        var library = Library;
        if (library is null) return;

        var hashes = new LibraryHashService(library);
        _shell.IsBusy = true;
        HashProgress = verifyOnly ? "Re-reading every file..." : "Hashing...";

        try
        {
            var progress = new Progress<string>(e => HashProgress = e);
            var results = await Task.Run(() => verifyOnly ? hashes.VerifyAll(progress) : hashes.RecordAll(progress));

            var changed = results.Where(r => r.IsRecorded && !r.Matches).ToList();
            var unrecorded = results.Where(r => !r.IsRecorded).ToList();

            ReportTitle = verifyOnly
                ? $"Verified {results.Count} mods — {results.Count(r => r.Matches)} unchanged, {changed.Count} changed, {unrecorded.Count} not previously recorded"
                : $"Recorded hashes for {results.Count} mods";

            Report.Clear();
            foreach (var r in changed) Report.Add($"CHANGED   {r.Entry}");
            foreach (var r in unrecorded) Report.Add($"new       {r.Entry}");
            if (Report.Count == 0) Report.Add("Everything matches what was recorded.");

            _shell.Report(ReportTitle);
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Library hashes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            HashProgress = string.Empty;
            _shell.IsBusy = false;
            OnPropertyChanged(nameof(HasReport));
        }
    }

    // --- Workshop updates --------------------------------------------------

    private string _updateProgress = string.Empty;
    private string _updateSummary = string.Empty;

    public string UpdateProgress
    {
        get => _updateProgress;
        private set => SetField(ref _updateProgress, value);
    }

    /// <summary>The one-line verdict from the last check.</summary>
    public string UpdateSummary
    {
        get => _updateSummary;
        private set => SetField(ref _updateSummary, value);
    }

    public bool HasStale => Mods.Any(m => m.HasUpdate);

    /// <summary>
    /// The last check's answers, kept so rebuilding the list after an update
    /// does not lose the markers on the mods that were not part of it.
    /// </summary>
    private IReadOnlyList<LibraryUpdateStatus> _statuses = Array.Empty<LibraryUpdateStatus>();

    private void ApplyStatuses()
    {
        var byEntry = _statuses.ToDictionary(st => st.Entry, StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods) mod.Update = byEntry.GetValueOrDefault(mod.Entry);
        OnPropertyChanged(nameof(HasStale));
    }

    private string[] StaleEntries() => Mods.Where(m => m.HasUpdate).Select(m => m.Entry).ToArray();

    /// <summary>
    /// Ask the Workshop which library mods have moved on.
    ///
    /// This needs no subscription, so it never disturbs Steam's view of what
    /// belongs in a profile. That is what makes it safe to run often, and what
    /// keeps the resubscribe step narrow when it does run.
    /// </summary>
    /// <summary>
    /// Re-read the library's state against the Workshop and apply it.
    ///
    /// Always a fresh lookup, never a patched-up copy of the last one. Inferring
    /// "these are current now" after an update is what left the tab claiming
    /// mods were stale when the metadata on disk already said otherwise.
    /// </summary>
    private async Task<IReadOnlyList<LibraryUpdateStatus>?> FetchStatusesAsync()
    {
        var library = Library;
        if (library is null) return null;

        using var checker = new WorkshopUpdateService();
        var service = new LibraryUpdateService(library, checker);
        var progress = new Progress<string>(e => UpdateProgress = e);

        var statuses = await service.CheckAsync(progress: progress);

        _statuses = statuses;
        ApplyStatuses();
        UpdateSummary = Describe(statuses);
        return statuses;
    }

    private static string Describe(IReadOnlyList<LibraryUpdateStatus> statuses)
    {
        var stale = statuses.Count(st => st.NeedsUpdate);
        var gone = statuses.Count(st => st.State == UpdateState.Unavailable);

        var summary = stale == 0
            ? "Everything from the Workshop is current."
            : $"{stale} of {statuses.Count} mods have a newer version on the Workshop.";

        return gone > 0 ? summary + $"  ·  {gone} no longer on the Workshop" : summary;
    }

    private async Task CheckUpdatesAsync()
    {
        var library = Library;
        if (library is null) return;

        _shell.IsBusy = true;
        UpdateProgress = "Asking Steam what has changed...";

        try
        {
            var statuses = await FetchStatusesAsync();
            if (statuses is null) return;

            var stale = statuses.Count(st => st.NeedsUpdate);
            var guessed = statuses.Count(st => st.BaselineIsImportDate);

            Report.Clear();
            ReportTitle = UpdateSummary;
            foreach (var st in statuses.Where(x => x.NeedsUpdate).OrderByDescending(x => x.UpstreamUpdatedUtc))
                Report.Add($"UPDATE    {st.Entry}  —  changed {st.UpstreamUpdatedUtc:yyyy-MM-dd}");
            foreach (var st in statuses.Where(x => x.State == UpdateState.Unavailable))
                Report.Add($"GONE      {st.Entry}  —  Steam no longer returns this item");

            // Entries imported before revisions were recorded are judged against
            // their import date, which can miss an update that landed between
            // Steam downloading the content and the import happening. One update
            // run per entry fixes that permanently, so say it once.
            if (guessed > 0)
                Report.Add($"note      {guessed} entries were judged from their import date, not a recorded revision");

            if (Report.Count == 0) Report.Add("Nothing to do.");
            _shell.Report(UpdateSummary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UpdateSummary = "Could not reach Steam's Workshop API.";
            _shell.Report($"{UpdateSummary} {ex.Message}");
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateProgress = string.Empty;
            _shell.IsBusy = false;
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(HasStale));
        }
    }

    /// <summary>
    /// Resubscribe to the chosen mods, take the new content, and unsubscribe.
    ///
    /// Confirmed first because it touches the user's Steam account, and because
    /// the consequence lands on everyone they play with: updated mods have new
    /// hashes, and a co-op partner still on the old bytes will desync.
    /// </summary>
    private async Task UpdateAsync(IReadOnlyList<string> entries)
    {
        var library = Library;
        var gameDir = _shell.Config?.GameDir;
        if (library is null || entries.Count == 0) return;

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            MessageBox.Show("No game directory in the config, so Steam's API cannot be reached.",
                            "Update mods", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pull = new WorkshopPullService(gameDir);
        if (!pull.IsAvailable)
        {
            MessageBox.Show(WorkshopPullService.NotFoundMessage(),
                            "Update mods", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var names = string.Join(Environment.NewLine + "  ", entries.Take(12));
        var more = entries.Count > 12
            ? Environment.NewLine + $"  ...and {entries.Count - 12} more"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Resubscribe to {entries.Count} Workshop mod(s), download the new versions into the library, " +
            "then unsubscribe again?" + Environment.NewLine + Environment.NewLine +
            "  " + names + more + Environment.NewLine + Environment.NewLine +
            "Steam must be running and Isaac must be closed. The old copies are kept in .backup." +
            Environment.NewLine + Environment.NewLine +
            "Everyone you play with needs the same update afterwards, or you will desync.",
            "Update from the Workshop", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _shell.IsBusy = true;
        UpdateProgress = "Starting...";

        try
        {
            var runner = new LibraryUpdateRunner(library, pull, _shell.Process);
            var progress = new Progress<string>(e => UpdateProgress = e);
            var report = await runner.RunAsync(entries, progress);

            if (report.AnythingChanged)
            {
                // Hashes on record now describe the previous bytes. Leaving them
                // would make a verify report every updated mod as tampered with.
                // Done before the report is written because hashing writes its
                // own, and the update's is the one worth keeping on screen.
                Refresh();
                await HashAsync(verifyOnly: false);

                // Ask Steam again rather than assuming the run made these
                // current. The recorded revisions are on disk now, so a fresh
                // lookup is the only answer that cannot drift from them.
                UpdateProgress = "Re-checking...";
                try
                {
                    await FetchStatusesAsync();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // The update itself succeeded; a failed re-check only costs
                    // the markers, so do not present it as an update failure.
                    UpdateSummary = "Updated. Could not re-check afterwards — press Check for updates.";
                }
            }

            Report.Clear();
            foreach (var entry in report.Updated) Report.Add($"updated   {entry}");
            foreach (var failure in report.Failed) Report.Add($"FAILED    {failure}");
            foreach (var warning in report.Warnings) Report.Add($"note      {warning}");
            if (report.Backups.Count > 0)
                Report.Add($"backup    the previous copies are in {library.BackupRoot}");
            if (report.AnythingChanged)
                Report.Add("hashes    re-recorded, so an export now describes the updated files");

            ReportTitle = report.AnythingChanged
                ? $"Updated {report.Updated.Count} mod(s)" +
                  (report.Failed.Count > 0 ? $", {report.Failed.Count} failed" : string.Empty)
                : "Nothing was updated.";

            _shell.Report(ReportTitle);
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Update mods", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateProgress = string.Empty;
            _shell.IsBusy = false;
            OnPropertyChanged(nameof(HasReport));
            OnPropertyChanged(nameof(HasStale));
        }
    }

    // --- Share codes -------------------------------------------------------

    /// <summary>
    /// Put a whole mod set on the clipboard as one string.
    ///
    /// The code carries Workshop ids, entry names and hashes, so the recipient
    /// can fetch the set and prove their copy matches. It cannot be short: ids
    /// are essentially random 34-bit numbers and hashes are incompressible, so
    /// 40 mods lands around 3.5 KB. A Steam collection id is the short
    /// alternative, and it is short only because Steam stores the list.
    /// </summary>
    private void CopyShareCode(bool wholeLibrary)
    {
        var library = Library;
        if (library is null) return;

        try
        {
            var hashes = new LibraryHashService(library);
            var profile = wholeLibrary
                ? hashes.ExportLibrary("library")
                : hashes.Export(TargetProfile!, library.LoadManifest(TargetProfile!));

            if (profile.Mods.Count == 0)
            {
                MessageBox.Show("There is nothing to share — that set has no mods in it.",
                                "Share code", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var code = ShareCodeService.Encode(profile);

            try
            {
                Clipboard.SetText(code);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show("Another program is holding the clipboard. Try again in a moment.",
                                "Share code", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var unhashed = profile.Mods.Count - profile.Hashes.Count;
            var unfetchable = profile.Mods.Count - profile.WorkshopIds.Count;

            var message =
                $"Copied a share code for {profile.Mods.Count} mods ({code.Length:N0} characters)." +
                Environment.NewLine + Environment.NewLine +
                "Paste it to whoever you play with. They paste it into Import and the app downloads the set for them.";

            // Both of these silently weaken what the recipient gets, so say so
            // now rather than letting them find out at the far end.
            if (unhashed > 0)
                message += Environment.NewLine + Environment.NewLine +
                           $"{unhashed} mod(s) have no recorded hash, so those cannot be verified. " +
                           "Press Record hashes first if that matters.";

            if (unfetchable > 0)
                message += Environment.NewLine + Environment.NewLine +
                           $"{unfetchable} mod(s) are not Workshop items, so nothing can download those for them. " +
                           "You will have to send those folders yourself.";

            _shell.Report($"Copied a {code.Length:N0} character share code for {profile.Mods.Count} mods.");
            MessageBox.Show(message, "Share code", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Share code", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportShare()
    {
        var library = Library;
        var gameDir = _shell.Config?.GameDir;
        if (library is null || string.IsNullOrWhiteSpace(gameDir)) return;

        var window = new Views.ShareImportWindow(library, new WorkshopPullService(gameDir), _shell.Process,
            name =>
                {
                    var config = _shell.Config;
                    if (config is null) return;
                    if (config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                    _shell.ModProfileService.Add(config, name);
                })
        {
            Owner = Application.Current?.MainWindow,
        };

        window.ShowDialog();

        if (!window.Changed) return;

        _shell.Report("Imported a shared mod set.");
        Refresh();
    }

    /// <summary>Write a profile plus its hashes to one small file to send someone.</summary>
    private void ExportProfile()
    {
        var library = Library;
        if (library is null || TargetProfile is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile for someone else",
            FileName = $"{TargetProfile}.ipmprofile.json",
            Filter = "Isaac profile export|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var hashes = new LibraryHashService(library);
            var export = hashes.Export(TargetProfile, library.LoadManifest(TargetProfile));
            hashes.WriteExport(export, dialog.FileName);

            var missing = export.Mods.Count - export.Hashes.Count;
            _shell.Report(missing > 0
                ? $"Exported '{TargetProfile}' ({export.Mods.Count} mods) — {missing} without a hash. Record hashes first so they can be verified."
                : $"Exported '{TargetProfile}' with {export.Mods.Count} mods and hashes for all of them.");
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Export profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Compare the chosen profile against someone else's export.</summary>
    private void CompareWithExport()
    {
        var library = Library;
        if (library is null || TargetProfile is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Compare against someone else's export",
            Filter = "Isaac profile export|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var hashes = new LibraryHashService(library);
            var theirs = LibraryHashService.ReadExport(dialog.FileName);
            var diff = hashes.Compare(library.LoadManifest(TargetProfile), theirs);

            ReportTitle = $"'{TargetProfile}' vs '{theirs.Name}' — {diff.Summary}";
            Report.Clear();

            foreach (var entry in diff.Problems)
            {
                Report.Add(entry.Kind switch
                {
                    ProfileDiffKind.ContentDiffers => $"DIFFERENT  {entry.Entry}  (same name, different files)",
                    ProfileDiffKind.OnlyMine => $"only yours {entry.Entry}",
                    ProfileDiffKind.OnlyTheirs => $"only THEIRS {entry.Entry}",
                    _ => $"unverified {entry.Entry}  (no hash on one side)",
                });
            }

            if (Report.Count == 0) Report.Add("Identical — same mods, same bytes. This profile is safe to play together.");
            _shell.Report(ReportTitle);
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Compare profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OnPropertyChanged(nameof(HasReport));
        }
    }

    public ObservableCollection<LibraryModViewModel> Mods { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public ICollectionView View { get; }

    public RelayCommand ApplyCommand { get; }
    public RelayCommand IncludeAllCommand { get; }
    public RelayCommand IncludeNoneCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenLibraryCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public bool HasLibrary { get; private set; }
    public string LibraryPathText { get; private set; } = string.Empty;
    public string SummaryText { get; private set; } = string.Empty;

    private ModLibraryService? Library =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new ModLibraryService(_shell.Junctions, _shell.Config!.SyncRoot!);

    public LibraryModViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            MeasureSelected();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>Size is measured only for the selected mod — scanning all of them would walk gigabytes.</summary>
    public string DetailSizeText
    {
        get => _detailSizeText;
        private set => SetField(ref _detailSizeText, value);
    }

    public string Search
    {
        get => _search;
        set { if (SetField(ref _search, value)) View.Refresh(); }
    }

    /// <summary>The profile the tick boxes are editing.</summary>
    public string? TargetProfile
    {
        get => _targetProfile;
        set
        {
            if (!SetField(ref _targetProfile, value)) return;
            LoadTicks();
            OnPropertyChanged(nameof(TargetProfileText));
        }
    }

    public string TargetProfileText => TargetProfile is null
        ? "Choose a profile to start ticking mods into it."
        : $"Ticking mods into '{TargetProfile}'.";

    public bool ShowOnlyIncluded
    {
        get => _showOnlyIncluded;
        set { if (SetField(ref _showOnlyIncluded, value)) View.Refresh(); }
    }

    public string SelectionSummary =>
        TargetProfile is null ? "" : $"{Mods.Count(m => m.InProfile)} of {Mods.Count} mods selected";

    private bool Matches(LibraryModViewModel mod)
    {
        if (ShowOnlyIncluded && !mod.InProfile) return false;
        if (Search.Length == 0) return true;
        return mod.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || mod.Entry.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || mod.Description.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    public void Refresh()
    {
        var previous = Selected?.Entry;
        var previousProfile = TargetProfile;

        Mods.Clear();
        Profiles.Clear();

        var library = Library;
        var config = _shell.Config;

        if (library is null || config is null)
        {
            HasLibrary = false;
            SummaryText = "Load a config first.";
            RaiseHeader();
            return;
        }

        LibraryPathText = library.LibraryRoot;
        var entries = library.ListEntries();
        HasLibrary = entries.Count > 0;

        foreach (var profile in config.Profiles) Profiles.Add(profile);

        foreach (var entry in entries)
        {
            Mods.Add(new LibraryModViewModel
            {
                // measure:false — the list must not walk every mod on refresh.
                Info = library.Describe(entry, measure: false),
                UsedBy = library.ProfilesUsing(entry),
            });
        }

        var orphans = Mods.Count(m => m.IsOrphan);
        SummaryText = HasLibrary
            ? $"{Mods.Count} mods  ·  {Mods.Count(m => m.Info.HasWorkshopOrigin)} from the Workshop  ·  " +
              $"{Mods.Count(m => m.PreviewPath is not null)} with a preview" +
              (orphans > 0 ? $"  ·  {orphans} in no profile" : "")
            : "The library is empty. Import mods on the Workshop tab, or adopt an existing profile's folders from the Mod profiles tab.";

        // Default to the profile that is actually active. Leaving it null meant
        // arriving at a list where every switch reads off, whatever the mods are
        // really in, and no clue that a profile has to be picked first.
        _targetProfile = previousProfile is not null && Profiles.Contains(previousProfile)
            ? previousProfile
            : Profiles.FirstOrDefault(p => string.Equals(p, config.ActiveProfile, StringComparison.OrdinalIgnoreCase))
              ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(TargetProfile));
        LoadTicks();

        Selected = Mods.FirstOrDefault(m => m.Entry == previous) ?? Mods.FirstOrDefault();

        // Refresh rebuilds every row, so without this a tab switch quietly drops
        // the update markers while the summary line above them stayed behind.
        ApplyStatuses();

        View.Refresh();
        RaiseHeader();
    }

    private void LoadTicks()
    {
        var library = Library;
        if (library is null || TargetProfile is null)
        {
            foreach (var mod in Mods) mod.InProfile = false;
            RaiseHeader();
            return;
        }

        HashSet<string> included;
        try
        {
            included = library.LoadManifest(TargetProfile).Mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (ConfigSchemaMismatchException)
        {
            included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // A profile with no manifest yet still has junctions; read those instead
        // so an adopted profile opens with the right ticks.
        if (included.Count == 0)
        {
            foreach (var entry in library.Analyse(TargetProfile).Where(e => e.IsLink && e.LibraryEntry is not null))
                included.Add(entry.LibraryEntry!);
        }

        foreach (var mod in Mods) mod.InProfile = included.Contains(mod.Entry);
        RaiseHeader();
    }

    private void MeasureSelected()
    {
        DetailSizeText = string.Empty;
        var library = Library;
        var entry = Selected?.Entry;
        if (library is null || entry is null) return;

        Task.Run(() =>
        {
            var info = library.Describe(entry, measure: true);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Selected?.Entry == entry)
                    DetailSizeText = $"{info.SizeMb:N1} MB  ·  {info.FileCount} files";
            });
        });
    }

    private void SetVisible(bool included)
    {
        foreach (var mod in View.Cast<LibraryModViewModel>().ToList()) mod.InProfile = included;
        RaiseHeader();
    }

    private void Apply()
    {
        var library = Library;
        if (library is null || TargetProfile is null) return;

        var chosen = Mods.Where(m => m.InProfile).Select(m => m.Entry).ToList();

        try
        {
            var manifest = library.LoadManifest(TargetProfile);
            manifest.Mods = chosen;
            library.SaveManifest(TargetProfile, manifest);

            var report = library.Materialise(TargetProfile, manifest);

            var parts = new List<string> { $"{TargetProfile}: {chosen.Count} mods" };
            if (report.Created.Count > 0) parts.Add($"linked {report.Created.Count}");
            if (report.Removed.Count > 0) parts.Add($"unlinked {report.Removed.Count}");
            if (report.Repointed.Count > 0) parts.Add($"repointed {report.Repointed.Count}");
            if (report.LeftAlone.Count > 0) parts.Add($"left {report.LeftAlone.Count} real folder(s) alone");

            _shell.Report(string.Join(" — ", parts));
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private void RemoveSelected()
    {
        var library = Library;
        if (library is null || Selected is null) return;

        if (MessageBox.Show(
                $"Remove '{Selected.Name}' from the library?\n\n" +
                "It is moved to a timestamped folder under .backup, not deleted.",
                "Remove from library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var moved = library.RemoveFromLibrary(Selected.Entry);
            _shell.Report($"Moved to {moved}");
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Remove from library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private void OpenFolder()
    {
        if (Selected is null || !Directory.Exists(Selected.Path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Selected.Path}\"") { UseShellExecute = true });
    }

    private void OpenLibrary()
    {
        var root = Library?.LibraryRoot;
        if (root is null) return;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(HasLibrary));
        OnPropertyChanged(nameof(LibraryPathText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectionSummary));
    }
}
