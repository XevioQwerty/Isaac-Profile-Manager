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
    private bool _disabled;

    public required string Entry { get; init; }
    public required string DisplayName { get; init; }
    public string? PreviewPath { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>Ticked entries are what the profile will contain after Apply.</summary>
    public bool Included
    {
        get => _included;
        set { if (SetField(ref _included, value)) OnPropertyChanged(nameof(StateText)); }
    }

    /// <summary>
    /// In the profile but switched off: the manifest keeps listing it, and no
    /// junction is laid down, so Isaac cannot load it. Meaningless unless
    /// <see cref="Included"/> — a mod that is not a member is not "off", it is absent.
    /// </summary>
    public bool Disabled
    {
        get => _disabled;
        set { if (SetField(ref _disabled, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => !Included ? "" : Disabled ? "OFF" : "";

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
        AdoptMarkedDisabledCommand = new RelayCommand(AdoptMarkedDisabled, () => MarkedDisabled.Count > 0);
        EnableAllCommand = new RelayCommand(EnableAll, () => Entries.Any(e => e.Included && e.Disabled));
    }

    /// <summary>Mods switched off from the in-game menu, waiting to be made a profile choice.</summary>
    public ObservableCollection<string> MarkedDisabled { get; } = new();

    public bool HasMarkedDisabled => MarkedDisabled.Count > 0;

    public string MarkedDisabledText => MarkedDisabled.Count == 0
        ? string.Empty
        : $"{MarkedDisabled.Count} mod(s) here were switched off from the in-game menu: " +
          string.Join(", ", MarkedDisabled);

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    /// <summary>Real copies still sitting in the profile, with what they look like a duplicate of.</summary>
    public ObservableCollection<string> RealFolders { get; } = new();

    public ICollectionView View { get; }

    public RelayCommand ApplyCommand { get; }
    public RelayCommand IncludeAllCommand { get; }
    public RelayCommand IncludeNoneCommand { get; }
    public RelayCommand AdoptCommand { get; }
    public RelayCommand AdoptMarkedDisabledCommand { get; }
    public RelayCommand EnableAllCommand { get; }

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

    public string SelectionSummary
    {
        get
        {
            var included = Entries.Count(e => e.Included);
            var off = Entries.Count(e => e.Included && e.Disabled);
            var summary = $"{included} of {Entries.Count} library mods in this profile";
            return off > 0 ? $"{summary} — {off} switched off" : summary;
        }
    }

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
        MarkedDisabled.Clear();

        var library = Library;
        HasLibrary = library is not null && library.ListEntries().Count > 0;

        if (library is null || profileName is null)
        {
            StatusText = "No library yet — import mods from the Workshop tab first.";
            RaiseSummaries();
            return;
        }

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysis = library.Analyse(profileName);

        // Prefer the manifest; fall back to whatever the folder already links to,
        // so a profile that predates manifests still opens with the right ticks.
        try
        {
            var manifest = library.LoadManifest(profileName);
            foreach (var mod in manifest.Mods) included.Add(mod);
            foreach (var mod in manifest.Disabled) disabled.Add(mod);
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
                Disabled = disabled.Contains(entry),
            });
        }

        if (_shell.Config is not null)
        {
            foreach (var marked in _shell.ModProfileService.FindMarkedDisabled(_shell.Config, profileName))
                MarkedDisabled.Add(marked);
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

    /// <summary>
    /// Take the mods switched off in-game and make that a property of the
    /// profile: unlink their folders, record them in the manifest, and clear the
    /// markers out of the shared library.
    ///
    /// Worth doing rather than leaving alone, because a marker written through a
    /// junction lands in the library and switches that mod off in every profile
    /// linking it — and activating any profile deletes markers, so the choice
    /// would silently evaporate at the next switch.
    /// </summary>
    private void AdoptMarkedDisabled()
    {
        if (_shell.Config is null || ProfileName.Length == 0 || MarkedDisabled.Count == 0) return;

        var names = MarkedDisabled.ToList();

        if (MessageBox.Show(
                $"Switch these {names.Count} mod(s) off in '{ProfileName}'?\n\n" +
                string.Join("\n", names.Select(n => "  • " + n)) + "\n\n" +
                "Their folders leave the profile, so Isaac stops loading them, but the profile " +
                "goes on listing them — turning one back on is a re-link, not a re-download.\n\n" +
                "The disable.it markers are cleared out of the shared library, where they were " +
                "switching these mods off in every other profile too. Nothing is deleted.",
                "Switch mods off", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RunOnProfile(() =>
        {
            var result = _shell.ModProfileService.SetDisabled(_shell.Config!, ProfileName, names, disabled: true);
            _shell.Report("Switched off " + result.Summary);
        });
    }

    /// <summary>Put every switched-off mod in this profile back.</summary>
    private void EnableAll()
    {
        if (_shell.Config is null || ProfileName.Length == 0) return;

        var off = Entries.Where(e => e.Included && e.Disabled).Select(e => e.Entry).ToList();
        if (off.Count == 0) return;

        RunOnProfile(() =>
        {
            var result = _shell.ModProfileService.SetDisabled(_shell.Config!, ProfileName, off, disabled: false);
            _shell.Report("Turned back on " + result.Summary);
        });
    }

    private void RunOnProfile(Action action)
    {
        try
        {
            action();
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

            // Membership and on/off are edited together here, and a mod that is
            // no longer a member cannot be "switched off" — leaving it listed
            // would keep unticked mods coming back off if they were re-added.
            manifest.Disabled = Entries
                .Where(e => e.Included && e.Disabled)
                .Select(e => e.Entry)
                .ToList();

            library.SaveManifest(ProfileName, manifest);

            var report = library.Materialise(ProfileName, manifest);

            var off = manifest.Disabled.Count;
            var parts = new List<string>
            {
                $"{ProfileName}: {chosen.Count} mods" + (off > 0 ? $" ({off} switched off)" : ""),
            };
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
        OnPropertyChanged(nameof(HasMarkedDisabled));
        OnPropertyChanged(nameof(MarkedDisabledText));
    }
}
