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

    public RelayCommand ImportProfileCommand => new(
        () =>
        {
            if (_shell.Config is null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a profile someone sent you",
                Filter = "Isaac profile export|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            Run(() =>
            {
                var (name, report, missing) = _shell.ModProfileService.ImportSharedProfile(_shell.Config!, dialog.FileName);
                _shell.Report(missing.Count == 0
                    ? $"Imported '{name}' — all {report?.Created.Count ?? 0} mod(s) linked from your library."
                    : $"Imported '{name}', but {missing.Count} mod(s) are missing from your library: {string.Join(", ", missing.Take(4))}" +
                      (missing.Count > 4 ? "..." : ""));
            });
        },
        () => _shell.Config is not null);

    public ObservableCollection<ProfileItem> Profiles { get; } = new();

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

        Run(() =>
        {
            _shell.ModProfileService.Add(_shell.Config!, NewProfileName.Trim(), seed);
            _shell.Report($"Created profile '{NewProfileName.Trim()}'" + (seed is null ? "" : $" seeded from '{seed}'"));
            NewProfileName = string.Empty;
            SeedFrom = EmptySeed;
        });
    }

    private void Remove()
    {
        if (_shell.Config is null || Selected is null) return;

        if (Ask($"Remove '{Selected.Name}' from the profile list?\n\n" +
                $"The mod folder stays on disk:\n{Selected.Path}") != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var name = Selected!.Name;
            _shell.ModProfileService.Remove(_shell.Config!, name);
            _shell.Report($"Removed '{name}' from the list. Its folder was left alone.");
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
