using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>One profile, as shown in the list. Purely presentational — no IO.</summary>
public sealed class ProfileItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool FolderExists { get; init; }
    public int ModCount { get; init; }
    public int DisabledCount { get; init; }
    public bool IsActive { get; init; }
    public bool UseRepentogon { get; init; }
    public string Notes { get; init; } = string.Empty;
    public DateTime? LastModified { get; init; }

    public string ModCountText => FolderExists ? $"{ModCount} mods" : "folder missing";

    public string DisabledText => DisabledCount > 0 ? $"{DisabledCount} disabled" : string.Empty;

    public string BuildText => UseRepentogon ? "REPENTOGON" : "vanilla";

    public string LastModifiedText => LastModified is null ? "" : LastModified.Value.ToString("yyyy-MM-dd HH:mm");

    public static ProfileItem From(ModProfile profile) => new()
    {
        Name = profile.Name,
        Path = profile.Path,
        FolderExists = profile.FolderExists,
        ModCount = profile.ModCount,
        DisabledCount = profile.DisabledCount,
        IsActive = profile.IsActive,
        UseRepentogon = profile.UseRepentogon,
        Notes = profile.Notes,
        LastModified = profile.LastModified,
    };
}

public sealed class ModProfilesViewModel : ObservableObject
{
    /// <summary>Placeholder in the seed list meaning "create it with no mods in it".</summary>
    private const string EmptySeed = "(empty)";

    private readonly MainViewModel _shell;

    private ProfileItem? _selected;
    private string _newProfileName = string.Empty;
    private string? _seedFrom;
    private string _notesDraft = string.Empty;
    private bool _useRepentogonDraft;

    public ModProfilesViewModel(MainViewModel shell)
    {
        _shell = shell;
        Contents = new ProfileContentsViewModel(shell);

        ActivateCommand = new RelayCommand(Activate, () => Selected is { FolderExists: true, IsActive: false });
        AddCommand = new RelayCommand(Add, () => ModProfileService.IsValidProfileName(NewProfileName));
        RemoveCommand = new RelayCommand(Remove, () => Selected is { IsActive: false });
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected is not null);
        OpenSyncRootCommand = new RelayCommand(OpenSyncRoot, () => !string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot));
        SaveDetailsCommand = new RelayCommand(SaveDetails, () => Selected is not null);
        ActivateAndLaunchCommand = new RelayCommand(
            () => { Activate(); if (_shell.Config is not null) _shell.LaunchGameCommand.Execute(null); },
            () => Selected is { FolderExists: true });
    }

    /// <summary>Switch to this profile and start the game — the two steps are always done together.</summary>
    public RelayCommand ActivateAndLaunchCommand { get; }

    /// <summary>
    /// Manifests found on disk that the config does not know about — a profile
    /// synced from someone else lands here and would otherwise be invisible.
    /// </summary>
    public ObservableCollection<DiscoveredProfile> Discovered { get; } = new();

    public bool HasDiscovered => Discovered.Count > 0;

    public RelayCommand AddDiscoveredCommand => new(
        parameter =>
        {
            if (_shell.Config is null || parameter is not DiscoveredProfile found) return;
            Run(() =>
            {
                var report = _shell.ModProfileService.RegisterProfile(_shell.Config!, found.Name);
                _shell.Report($"Added '{found.Name}' — linked {report?.Created.Count ?? 0} mod(s)" +
                              (report?.MissingFromLibrary.Count > 0
                                  ? $", {report.MissingFromLibrary.Count} not in your library yet."
                                  : "."));
            });
        },
        parameter => parameter is DiscoveredProfile && _shell.Config is not null);

    /// <summary>
    /// Import a profile from a code or a file.
    ///
    /// This used to be a separate, weaker import that only linked mods already
    /// in the library and downloaded nothing — so importing a 25-mod profile on
    /// a machine with an empty library produced an empty profile and no
    /// explanation. Two imports that did different things was the real defect;
    /// there is now one, and it can fetch.
    /// </summary>
    public RelayCommand ImportProfileCommand => new(
        () =>
        {
            var syncRoot = _shell.Config?.SyncRoot;
            var gameDir = _shell.Config?.GameDir;
            if (string.IsNullOrWhiteSpace(syncRoot)) return;

            var window = new Views.ShareImportWindow(
                new ModLibraryService(_shell.Junctions, syncRoot),
                new WorkshopPullService(gameDir ?? string.Empty),
                _shell.Process,
                name =>
                {
                    var config = _shell.Config;
                    if (config is null) return;
                    if (config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                    _shell.ModProfileService.Add(config, name);
                })
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };

            window.ShowDialog();
            if (!window.Changed) return;

            _shell.Report("Imported a shared mod set.");
            Refresh();
        },
        () => _shell.Config?.SyncRoot is not null);

    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    /// <summary>
    /// The patches for the build the selected profile will actually start, so
    /// they can be flipped next to the Launch button instead of by going to the
    /// Build tab and working out which folder applies.
    ///
    /// Which folder that is follows the profile's own build choice: a profile
    /// marked to run REPENTOGON is served by patches over the REPENTOGON build,
    /// anything else by patches over the retail install.
    /// </summary>
    public ObservableCollection<PatchSlotViewModel> QuickPatches { get; } = new();

    public bool HasQuickPatches => QuickPatches.Count > 0;

    /// <summary>Names the folder these switches act on, so "on" is not ambiguous.</summary>
    public string QuickPatchHeader => Selected is null
        ? string.Empty
        : Selected.UseRepentogon && PerProfileBuild ? "REPENTOGON" : "Retail";

    /// <summary>Reuses the Build tab's toggle, so the prompts and drift handling are the same.</summary>
    public RelayCommand TogglePatchCommand => _shell.BuildVariants.TogglePatchCommand;

    private void RefreshQuickPatches()
    {
        QuickPatches.Clear();

        var syncRoot = _shell.Config?.SyncRoot;
        if (Selected is not null && !string.IsNullOrWhiteSpace(syncRoot))
        {
            var wanted = Selected.UseRepentogon && PerProfileBuild
                ? Core.Models.PatchTarget.Repentogon
                : Core.Models.PatchTarget.GameRoot;

            var engine = new PatchService(_shell.Process, syncRoot);
            foreach (var info in engine.DescribeAll())
            {
                var state = info.States.FirstOrDefault(t => t.Target == wanted);
                if (state is null) continue;
                QuickPatches.Add(new PatchSlotViewModel
                {
                    Patch = info.Name,
                    DisplayName = info.DisplayName,
                    ShortName = info.ShortName,
                    State = state,
                });
            }
        }

        OnPropertyChanged(nameof(HasQuickPatches));
        OnPropertyChanged(nameof(QuickPatchHeader));
        _shell.NotifyQuickPatchesChanged();
    }

    /// <summary>Which library mods the selected profile contains.</summary>
    public ProfileContentsViewModel Contents { get; }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenSyncRootCommand { get; }
    public RelayCommand SaveDetailsCommand { get; }

    public ProfileItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            NotesDraft = value?.Notes ?? string.Empty;
            UseRepentogonDraft = value?.UseRepentogon ?? false;
            Contents.Load(value?.Name);
            RefreshQuickPatches();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedPathText));
        }
    }

    public bool HasSelection => Selected is not null;

    public string SelectedPathText => Selected?.Path ?? string.Empty;

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetField(ref _newProfileName, value);
    }

    public ObservableCollection<string> SeedOptions { get; } = new();

    public string? SeedFrom
    {
        get => _seedFrom;
        set => SetField(ref _seedFrom, value);
    }

    public string NotesDraft
    {
        get => _notesDraft;
        set => SetField(ref _notesDraft, value);
    }

    /// <summary>Which build this profile selects, when per-profile build is enabled.</summary>
    public bool UseRepentogonDraft
    {
        get => _useRepentogonDraft;
        set => SetField(ref _useRepentogonDraft, value);
    }

    public bool PerProfileBuild => _shell.Config?.PerProfileBuild ?? false;

    public void Refresh()
    {
        var previouslySelected = Selected?.Name;

        Profiles.Clear();
        SeedOptions.Clear();
        SeedOptions.Add(EmptySeed);

        if (_shell.Config is not null)
        {
            foreach (var profile in _shell.ModProfileService.List(_shell.Config))
            {
                Profiles.Add(ProfileItem.From(profile));
                SeedOptions.Add(profile.Name);
            }
        }

        // Assign after the items exist, so the ComboBox has something to match.
        Discovered.Clear();
        if (_shell.Config is not null)
        {
            foreach (var found in _shell.ModProfileService.FindUnregisteredProfiles(_shell.Config))
                Discovered.Add(found);
        }
        OnPropertyChanged(nameof(HasDiscovered));

        SeedFrom = SeedOptions.Contains(_seedFrom ?? string.Empty) ? _seedFrom : EmptySeed;
        Selected = Profiles.FirstOrDefault(p => p.Name == previouslySelected)
                   ?? Profiles.FirstOrDefault(p => p.IsActive)
                   ?? Profiles.FirstOrDefault();

        OnPropertyChanged(nameof(PerProfileBuild));
        RefreshQuickPatches();
    }

    private void Activate()
    {
        if (_shell.Config is null || Selected is null) return;

        // Not a hard block — Isaac reads mods\ at startup, so switching under a
        // running game affects the next launch, not this one. Still worth saying.
        if (_shell.IsIsaacRunning &&
            Ask($"Isaac is running. Switching now will not affect the session in progress.\n\nSwitch to '{Selected.Name}' anyway?") != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var result = _shell.ModProfileService.Activate(_shell.Config, Selected!.Name);
            var parts = new List<string> { $"Active profile: {result.ProfileName} ({result.ModCount} mods)" };
            if (result.ClearedMarkers > 0) parts.Add($"re-enabled {result.ClearedMarkers} disabled mod(s)");
            if (result.BuildSelected is { } mode) parts.Add($"build set to {(mode == LaunchMode.Repentogon ? "REPENTOGON" : "vanilla")}");
            _shell.Report(string.Join(" — ", parts));
        });
    }

    private void Add()
    {
        if (_shell.Config is null) return;
        var seed = SeedFrom is null or EmptySeed ? null : SeedFrom;

        // Seeding from a profile you have been switching mods off in is the
        // usual way a bisect ends: the answer is a set minus the mods that
        // caused it, and that set deserves to become its own profile.
        var seedDisabled = true;
        if (seed is not null)
        {
            var off = _shell.ModProfileService.DisabledMods(_shell.Config, seed);
            if (off.Count > 0)
            {
                var answer = MessageBox.Show(
                    $"'{seed}' has {off.Count} mod(s) switched off:\n\n" +
                    string.Join("\n", off.Select(m => "  " + m)) + "\n\n" +
                    $"Leave them out of '{NewProfileName.Trim()}' entirely?\n\n" +
                    "Yes - the new profile does not contain them at all.\n" +
                    "No - it contains them, still switched off.",
                    "Mods switched off in the source", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (answer == MessageBoxResult.Cancel) return;
                seedDisabled = answer == MessageBoxResult.No;
            }
        }

        Run(() =>
        {
            var name = NewProfileName.Trim();
            _shell.ModProfileService.Add(_shell.Config!, name, seed, seedDisabled);
            _shell.Report($"Created profile '{name}'" + (seed is null ? "" : $" seeded from '{seed}'"));
            NewProfileName = string.Empty;
            SeedFrom = EmptySeed;
        });
    }

    private void Remove()
    {
        if (_shell.Config is null || Selected is null) return;

        if (Ask($"Delete the profile '{Selected.Name}'?\n\n" +
                $"This removes it from the list, deletes its manifest, and deletes:\n{Selected.Path}\n\n" +
                "Links into the shared library are unlinked, so the library itself is untouched. " +
                "Any real mod folder inside is moved to .backup rather than deleted.") != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var name = Selected!.Name;
            var removal = _shell.ModProfileService.Remove(_shell.Config!, name);
            _shell.Report(removal.Summary);
        });
    }

    private void SaveDetails()
    {
        if (_shell.Config is null || Selected is null) return;

        Run(() =>
        {
            _shell.ModProfileService.SetNotes(_shell.Config!, Selected!.Name, NotesDraft.Trim());
            _shell.ModProfileService.SetUseRepentogon(_shell.Config!, Selected!.Name, UseRepentogonDraft);
            _shell.Report($"Saved details for '{Selected!.Name}'.");
        });
    }

    private void OpenFolder()
    {
        if (Selected is null) return;
        OpenInExplorer(Selected.Path);
    }

    private void OpenSyncRoot() => OpenInExplorer(_shell.Config?.SyncRoot);

    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Run an operation, surface any refusal verbatim, and reload afterwards so
    /// the UI reflects the filesystem rather than what we hoped happened.
    /// </summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Isaac Profile Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private static MessageBoxResult Ask(string message) =>
        MessageBox.Show(message, "Isaac Profile Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);
}
