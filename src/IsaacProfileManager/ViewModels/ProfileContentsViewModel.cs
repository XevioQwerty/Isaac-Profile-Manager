using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>One mod the profile contains, with the switch that loads or skips it.</summary>
public sealed class ProfileModViewModel : ObservableObject
{
    private bool _enabled = true;

    public required string Entry { get; init; }
    public required string DisplayName { get; init; }
    public string? PreviewPath { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>Raised when the switch is thrown, so the change lands immediately.</summary>
    public Action<ProfileModViewModel>? Toggled { get; init; }

    /// <summary>
    /// On means the mod is linked into the folder Isaac reads. Off leaves it in
    /// the profile's manifest but takes the junction away, which is what makes
    /// bisecting a desync reversible.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetField(ref _enabled, value)) return;
            OnPropertyChanged(nameof(StateText));
            Toggled?.Invoke(this);
        }
    }

    /// <summary>Set without running the toggle, for loading state off disk.</summary>
    public void SetEnabledQuietly(bool enabled)
    {
        _enabled = enabled;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StateText));
    }

    public string StateText => Enabled ? string.Empty : "OFF";

    public string SubtitleText => string.Equals(Entry, DisplayName, StringComparison.OrdinalIgnoreCase) ? "" : Entry;
}

/// <summary>
/// The mods one profile contains.
///
/// Lists the profile's own members and nothing else. It used to show the whole
/// library with a tick against the members, which meant a 26-mod profile was
/// 26 ticks scattered through 42 rows and you could not see what the profile
/// held. Adding and removing mods belongs to the Library tab, which is where
/// you choose from everything you own; this is where you look at one profile
/// and switch pieces of it off.
///
/// Switching off is a manifest change, never a <c>disable.it</c> marker: a
/// marker written through the junction lands in the shared library and would
/// switch that mod off in every profile linking it.
/// </summary>
public sealed class ProfileContentsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private string _profileName = string.Empty;
    private string _search = string.Empty;
    private string _statusText = string.Empty;
    private bool _hasLibrary;
    private bool _suspendToggles;

    public ProfileContentsViewModel(MainViewModel shell)
    {
        _shell = shell;

        View = CollectionViewSource.GetDefaultView(Mods);
        View.Filter = o => o is ProfileModViewModel m && Matches(m);

        AdoptCommand = new RelayCommand(AdoptRealFolders, () => RealFolders.Count > 0);
        AdoptMarkedDisabledCommand = new RelayCommand(AdoptMarkedDisabled, () => MarkedDisabled.Count > 0);
        EnableAllCommand = new RelayCommand(EnableAll, () => Mods.Any(m => !m.Enabled));
    }

    /// <summary>The profile's members, in library order.</summary>
    public ObservableCollection<ProfileModViewModel> Mods { get; } = new();

    /// <summary>Real copies still sitting in the profile, with what they look like a duplicate of.</summary>
    public ObservableCollection<string> RealFolders { get; } = new();

    /// <summary>Mods switched off from the in-game menu, waiting to be made a profile choice.</summary>
    public ObservableCollection<string> MarkedDisabled { get; } = new();

    public ICollectionView View { get; }

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
    public bool HasMarkedDisabled => MarkedDisabled.Count > 0;
    public bool HasMods => Mods.Count > 0;
    public bool IsEmpty => HasLibrary && Mods.Count == 0;

    public string MarkedDisabledText => MarkedDisabled.Count == 0
        ? string.Empty
        : $"{MarkedDisabled.Count} mod(s) here were switched off from the in-game menu: " +
          string.Join(", ", MarkedDisabled);

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
            if (Mods.Count == 0) return string.Empty;
            var off = Mods.Count(m => !m.Enabled);
            var loaded = Mods.Count - off;
            return off > 0 ? $"{loaded} loaded, {off} switched off" : $"{loaded} mod(s)";
        }
    }

    private ModLibraryService? Library =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new ModLibraryService(_shell.Junctions, _shell.Config!.SyncRoot!);

    private bool Matches(ProfileModViewModel mod) =>
        Search.Length == 0 ||
        mod.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
        mod.Entry.Contains(Search, StringComparison.OrdinalIgnoreCase);

    public void Load(string? profileName)
    {
        ProfileName = profileName ?? string.Empty;

        Mods.Clear();
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

        var members = new List<string>();
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysis = library.Analyse(profileName);

        // Prefer the manifest; fall back to whatever the folder already links to,
        // so a profile that predates manifests still shows its contents.
        try
        {
            var manifest = library.LoadManifest(profileName);
            members.AddRange(manifest.Mods);
            foreach (var mod in manifest.Disabled) disabled.Add(mod);
        }
        catch (ConfigSchemaMismatchException ex)
        {
            StatusText = ex.Message;
        }

        if (members.Count == 0)
            members.AddRange(analysis.Where(e => e.IsLink && e.LibraryEntry is not null).Select(e => e.LibraryEntry!));

        foreach (var entry in members.Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            var mod = new ProfileModViewModel
            {
                Entry = entry,
                DisplayName = library.GetCachedName(entry) ?? entry,
                PreviewPath = library.GetCachedImage(entry),
                Description = library.GetCachedDescription(entry) ?? string.Empty,
                Toggled = OnModToggled,
            };
            mod.SetEnabledQuietly(!disabled.Contains(entry));
            Mods.Add(mod);
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
    /// A switch was thrown. Applied immediately rather than batched behind an
    /// Apply button: one mod at a time is how a bisect actually goes, and a
    /// pending change nobody has written is a way to launch the game believing
    /// a mod is off when it is not.
    /// </summary>
    private void OnModToggled(ProfileModViewModel mod)
    {
        if (_suspendToggles || _shell.Config is null || ProfileName.Length == 0) return;

        var enabled = mod.Enabled;
        RunOnProfile(() =>
        {
            var result = _shell.ModProfileService.SetDisabled(
                _shell.Config!, ProfileName, new[] { mod.Entry }, disabled: !enabled);

            _shell.Report(result.Changed.Count == 0
                ? $"'{mod.DisplayName}' was already {(enabled ? "on" : "off")}."
                : $"{mod.DisplayName} switched {(enabled ? "on" : "off")} in '{ProfileName}'.");
        });
    }

    /// <summary>
    /// Take the mods switched off in-game and make that a property of the
    /// profile: unlink their folders, record them in the manifest, and clear the
    /// markers out of the shared library.
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

        var off = Mods.Where(m => !m.Enabled).Select(m => m.Entry).ToList();
        if (off.Count == 0) return;

        RunOnProfile(() =>
        {
            var result = _shell.ModProfileService.SetDisabled(_shell.Config!, ProfileName, off, disabled: false);
            _shell.Report("Turned back on " + result.Summary);
        });
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

    /// <summary>
    /// Run something that changes the profile, then reload from disk.
    ///
    /// Toggles are suspended across the reload: rebuilding the rows sets each
    /// switch from the manifest, and without this that would fire the toggle
    /// handler again and write the state back for every mod in the profile.
    /// </summary>
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
            _suspendToggles = true;
            try
            {
                _shell.Reload();
            }
            finally
            {
                _suspendToggles = false;
            }
            _shell.Report(message);
        }
    }

    private void RaiseSummaries()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasRealFolders));
        OnPropertyChanged(nameof(HasMarkedDisabled));
        OnPropertyChanged(nameof(MarkedDisabledText));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
