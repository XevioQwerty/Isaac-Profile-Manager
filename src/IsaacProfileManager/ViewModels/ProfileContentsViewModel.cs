using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>A library mod as it appears in the profile's contents editor.</summary>
public sealed class LibraryEntryViewModel : ObservableObject
{
    private bool _included;

    public required string Entry { get; init; }
    public required string DisplayName { get; init; }
    public string? PreviewPath { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>Ticked entries are what the profile will contain after Apply.</summary>
    public bool Included
    {
        get => _included;
        set => SetField(ref _included, value);
    }

    public string SubtitleText => string.Equals(Entry, DisplayName, StringComparison.OrdinalIgnoreCase) ? "" : Entry;
}

/// <summary>
/// Edits which library mods a profile contains.
///
/// The manifest is the only membership mechanism — a <c>disable.it</c> marker
/// written through a junction would land in the shared library and affect every
/// profile linking that mod, so excluding a mod means unticking it here.
/// </summary>
public sealed class ProfileContentsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private string _profileName = string.Empty;
    private string _search = string.Empty;
    private string _statusText = string.Empty;
    private bool _hasLibrary;

    public ProfileContentsViewModel(MainViewModel shell)
    {
        _shell = shell;

        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = o => o is LibraryEntryViewModel e && Matches(e);

        ApplyCommand = new RelayCommand(Apply, () => HasLibrary && ProfileName.Length > 0);
        IncludeAllCommand = new RelayCommand(() => SetVisible(true));
        IncludeNoneCommand = new RelayCommand(() => SetVisible(false));
        AdoptCommand = new RelayCommand(AdoptRealFolders, () => RealFolders.Count > 0);
    }

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    /// <summary>Real copies still sitting in the profile, with what they look like a duplicate of.</summary>
    public ObservableCollection<string> RealFolders { get; } = new();

    public ICollectionView View { get; }

    public RelayCommand ApplyCommand { get; }
    public RelayCommand IncludeAllCommand { get; }
    public RelayCommand IncludeNoneCommand { get; }
    public RelayCommand AdoptCommand { get; }

    public string ProfileName
    {
        get => _profileName;
        private set => SetField(ref _profileName, value);
    }

    public bool HasLibrary
    {
        get => _hasLibrary;
        private set => SetField(ref _hasLibrary, value);
    }

    public bool HasRealFolders => RealFolders.Count > 0;

    public string Search
    {
        get => _search;
        set { if (SetField(ref _search, value)) View.Refresh(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SelectionSummary =>
        $"{Entries.Count(e => e.Included)} of {Entries.Count} library mods in this profile";

    private ModLibraryService? Library =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new ModLibraryService(_shell.Junctions, _shell.Config!.SyncRoot!);

    private bool Matches(LibraryEntryViewModel entry) =>
        Search.Length == 0 ||
        entry.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
        entry.Entry.Contains(Search, StringComparison.OrdinalIgnoreCase);

    public void Load(string? profileName)
    {
        ProfileName = profileName ?? string.Empty;

        Entries.Clear();
        RealFolders.Clear();

        var library = Library;
        HasLibrary = library is not null && library.ListEntries().Count > 0;

        if (library is null || profileName is null)
        {
            StatusText = "No library yet — import mods from the Workshop tab first.";
            RaiseSummaries();
            return;
        }

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysis = library.Analyse(profileName);

        // Prefer the manifest; fall back to whatever the folder already links to,
        // so a profile that predates manifests still opens with the right ticks.
        try
        {
            var manifest = library.LoadManifest(profileName);
            foreach (var mod in manifest.Mods) included.Add(mod);
        }
        catch (ConfigSchemaMismatchException ex)
        {
            StatusText = ex.Message;
        }

        if (included.Count == 0)
            foreach (var entry in analysis.Where(e => e.IsLink && e.LibraryEntry is not null))
                included.Add(entry.LibraryEntry!);

        foreach (var entry in library.ListEntries())
        {
            Entries.Add(new LibraryEntryViewModel
            {
                Entry = entry,
                DisplayName = library.GetCachedName(entry) ?? entry,
                PreviewPath = library.GetCachedImage(entry),
                Description = library.GetCachedDescription(entry) ?? string.Empty,
                Included = included.Contains(entry),
            });
        }

        foreach (var entry in analysis.Where(e => !e.IsLink))
        {
            RealFolders.Add(entry.Suggestion is null
                ? $"{entry.Name}  —  not in the library"
                : $"{entry.Name}  —  duplicate of '{entry.Suggestion}'");
        }

        if (StatusText.Length == 0 || !StatusText.StartsWith("No library"))
            StatusText = RealFolders.Count > 0
                ? $"{RealFolders.Count} folder(s) here are real copies, not links. Adopt them to finish the migration."
                : string.Empty;

        View.Refresh();
        RaiseSummaries();
    }

    private void SetVisible(bool included)
    {
        foreach (var entry in View.Cast<LibraryEntryViewModel>().ToList()) entry.Included = included;
        RaiseSummaries();
    }

    private void Apply()
    {
        var library = Library;
        if (library is null || ProfileName.Length == 0) return;

        var chosen = Entries.Where(e => e.Included).Select(e => e.Entry).ToList();

        try
        {
            var manifest = library.LoadManifest(ProfileName);
            manifest.Mods = chosen;
            library.SaveManifest(ProfileName, manifest);

            var report = library.Materialise(ProfileName, manifest);

            var parts = new List<string> { $"{ProfileName}: {chosen.Count} mods" };
            if (report.Created.Count > 0) parts.Add($"linked {report.Created.Count}");
            if (report.Removed.Count > 0) parts.Add($"unlinked {report.Removed.Count}");
            if (report.Repointed.Count > 0) parts.Add($"repointed {report.Repointed.Count}");
            if (report.LeftAlone.Count > 0) parts.Add($"left {report.LeftAlone.Count} real folder(s) alone");
            if (report.MissingFromLibrary.Count > 0) parts.Add($"{report.MissingFromLibrary.Count} not in library");

            _shell.Report(string.Join(" — ", parts));
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Profile contents", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    /// <summary>
    /// Turn the profile's remaining real copies into links: move hand-installed
    /// mods into the library, and swap redundant duplicates for links with the
    /// displaced copy kept under .backup.
    /// </summary>
    private void AdoptRealFolders()
    {
        var library = Library;
        if (library is null || ProfileName.Length == 0) return;

        var analysis = library.Analyse(ProfileName).Where(e => !e.IsLink).ToList();
        if (analysis.Count == 0) return;

        var adopting = analysis.Count(e => e.NeedsAdopting);
        var replacing = analysis.Count(e => e.IsRedundantCopy);

        if (MessageBox.Show(
                $"Convert {analysis.Count} real folder(s) in '{ProfileName}' to library links?\n\n" +
                $"• {adopting} moved into the library (they exist nowhere else)\n" +
                $"• {replacing} replaced by a link, the copy kept under .backup\n\n" +
                "Nothing is deleted.",
                "Adopt into library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var done = 0;
        var failures = new List<string>();

        foreach (var entry in analysis)
        {
            try
            {
                if (entry.Suggestion is not null) library.ReplaceWithLink(ProfileName, entry.Name, entry.Suggestion);
                else library.AdoptIntoLibrary(ProfileName, entry.Name);
                done++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {ex.Message}");
            }
        }

        _shell.Report(failures.Count == 0
            ? $"Converted {done} folder(s) in '{ProfileName}' to library links."
            : $"Converted {done}; {failures.Count} could not be converted.");

        if (failures.Count > 0)
            MessageBox.Show(string.Join("\n\n", failures), "Adopt into library", MessageBoxButton.OK, MessageBoxImage.Warning);

        var message = _shell.StatusMessage;
        _shell.Reload();
        _shell.Report(message);
    }

    private void RaiseSummaries()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasRealFolders));
    }
}
