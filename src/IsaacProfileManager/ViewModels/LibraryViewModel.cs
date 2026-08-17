using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    public string Description => Info.Description;
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
    }

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

        _targetProfile = previousProfile is not null && Profiles.Contains(previousProfile) ? previousProfile : null;
        OnPropertyChanged(nameof(TargetProfile));
        LoadTicks();

        Selected = Mods.FirstOrDefault(m => m.Entry == previous) ?? Mods.FirstOrDefault();
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
