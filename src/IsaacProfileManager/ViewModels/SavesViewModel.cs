using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

public sealed class SaveSetViewModel
{
    public required SaveSet Set { get; init; }
    public required IReadOnlyList<string> Drift { get; init; }

    public string Name => Set.Name;
    public string BuildText => Set.BuildText;
    public string ModProfile => Set.ModProfile.Length > 0 ? Set.ModProfile : "(not recorded)";
    public string PlayersText => Set.Players.Count > 0 ? string.Join(", ", Set.Players) : "(nobody recorded)";
    public string SlotsText => Set.Slots.Count > 0 ? $"slots {string.Join(", ", Set.Slots)}" : "no slots";
    public string Notes => Set.Notes;
    public string FilesText => Set.Files.Count == 0
        ? "(no save captured yet)"
        : string.Join("\n", Set.Files);

    /// <summary>Created but never filled: the game has not written its save yet.</summary>
    public bool IsEmpty => Set.Files.Count == 0;

    public string CapturedText => DateTime.TryParse(Set.CapturedUtc, out var d)
        ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Set.CapturedUtc;

    public string LastUsedText => Set.LastUsedUtc is not null && DateTime.TryParse(Set.LastUsedUtc, out var d)
        ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";

    public bool HasDrift => Drift.Count > 0;

    public string DriftText => HasDrift
        ? $"{Drift.Count} live file(s) differ from this set: {string.Join(", ", Drift)}"
        : string.Empty;
}

/// <summary>
/// Captures and restores sets of save files.
///
/// The highest-risk screen in the app. Save structures differ between builds and
/// a cross-build load can destroy every achievement, so activation is gated on
/// checks that cannot be overridden from the UI — including Steam Cloud being
/// off, since with it on Steam would restore the files we replace.
/// </summary>
public sealed class SavesViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private SaveSetViewModel? _selected;
    private string _newSetName = string.Empty;
    private string _newSetPlayers = string.Empty;
    private string _newSetNotes = string.Empty;
    private string? _selectedBackup;
    private bool _cloudAcknowledged;
    private string _editName = string.Empty;
    private string _editPlayers = string.Empty;
    private string _editNotes = string.Empty;
    private string _editSlot1 = string.Empty;
    private string _editSlot2 = string.Empty;
    private string _editSlot3 = string.Empty;

    public SavesViewModel(MainViewModel shell)
    {
        _shell = shell;

        CaptureCommand = new RelayCommand(Capture, () => Service is not null && NewSetName.Trim().Length > 0);
        ActivateCommand = new RelayCommand(Activate, () => Selected is not null && CanActivate);
        BackupNowCommand = new RelayCommand(BackupNow, () => Service is not null);
        RestoreCommand = new RelayCommand(Restore, () => SelectedBackup is not null);
        OpenLiveFolderCommand = new RelayCommand(OpenLiveFolder, () => LiveFolder is not null);
        OpenSetsFolderCommand = new RelayCommand(OpenSetsFolder, () => Service is not null);
        OpenSteamPropertiesCommand = new RelayCommand(OpenSteamProperties);
        RecheckCommand = new RelayCommand(() => { Refresh(); _shell.Report("Re-read Steam's Cloud setting."); });
        TurnCloudOffCommand = new RelayCommand(TurnCloudOff, () => !SteamCloudService.IsSteamRunning());
        SaveEditsCommand = new RelayCommand(SaveEdits, () => Selected is not null && EditName.Trim().Length > 0);
        DeleteSetCommand = new RelayCommand(DeleteSet, () => Selected is not null);
        StartFreshCommand = new RelayCommand(StartFresh, () => Service is not null && NewSetName.Trim().Length > 0);
        CaptureIntoCommand = new RelayCommand(CaptureInto, () => Selected is not null);
        DeleteBackupCommand = new RelayCommand(DeleteBackup, () => SelectedBackup is not null);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder, () => Service is not null);
        ChooseSaveFolderCommand = new RelayCommand(ChooseSaveFolder, () => _shell.Config is not null);
        ClearSaveFolderCommand = new RelayCommand(ClearSaveFolder, () => _shell.Config?.SaveFolder is not null);
    }

    /// <summary>Point the app at the folder the game really uses.</summary>
    public RelayCommand ChooseSaveFolderCommand { get; }

    /// <summary>Go back to working it out.</summary>
    public RelayCommand ClearSaveFolderCommand { get; }

    /// <summary>
    /// Whether Steam Cloud has any bearing on these saves at all. It does not
    /// when the game writes somewhere Steam does not own, which is every copy
    /// running an emulated steam_api.
    /// </summary>
    public bool CloudApplies => SaveFolder?.Source is null or SaveFolderSource.SteamUserdata;

    /// <summary>
    /// The "Steam was running when this was read" caveat, which is only worth
    /// showing when the Cloud setting matters at all.
    /// </summary>
    public bool ShowCloudStaleness => CloudApplies && ShowStaleness;

    public string CloudIrrelevantText =>
        CloudApplies
            ? string.Empty
            : "Steam Cloud does not apply here. These saves are not in Steam's folder, so Steam has " +
              "no copy of them and cannot put one back - nothing below needs turning off.";

    /// <summary>Which folder was chosen for the live saves, and why.</summary>
    public SaveFolderResolution? SaveFolder => Service?.ResolveLiveFolder();

    public string SaveFolderSourceText => SaveFolder is null
        ? string.Empty
        : $"Chosen because it is {SaveFolder.SourceText}.";

    /// <summary>
    /// True when the chosen folder holds no save files at all. That is the state
    /// where every save feature quietly does nothing, so it is worth saying so
    /// rather than showing an empty list and letting it be discovered later.
    /// </summary>
    public bool SaveFolderLooksWrong => SaveFolder is { Found: true, SaveFileCount: 0 };

    public string SaveFolderWarning =>
        !SaveFolderLooksWrong
            ? string.Empty
            : "There are no save files here yet, so nothing on this tab can act on your saves. " +
              "That is expected right after starting a fresh save - the game writes one the next " +
              "time it saves. Launch it, play far enough to save, then press Re-check. If a " +
              "different folder is the one that gains a persistentgamedata file, point the app at " +
              "that one instead.";

    private void ChooseSaveFolder()
    {
        var config = _shell.Config;
        if (config is null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where the game keeps its live saves",
        };
        if (dialog.ShowDialog() != true) return;

        config.SaveFolder = dialog.FolderName;
        _shell.Store.Save(config);
        _shell.Report($"Live saves will be read from {dialog.FolderName}.");
        _shell.Reload();
    }

    private void ClearSaveFolder()
    {
        var config = _shell.Config;
        if (config is null) return;

        config.SaveFolder = null;
        _shell.Store.Save(config);
        _shell.Report("Back to working out the save folder automatically.");
        _shell.Reload();
    }

    /// <summary>Delete the selected backup. The one destructive action in this tab.</summary>
    public RelayCommand DeleteBackupCommand { get; }

    public RelayCommand OpenBackupsFolderCommand { get; }

    /// <summary>Begin a save set with no save in it, for a fresh unlock state.</summary>
    public RelayCommand StartFreshCommand { get; }

    /// <summary>Adopt whatever the game has since written into the selected set.</summary>
    public RelayCommand CaptureIntoCommand { get; }

    public RelayCommand SaveEditsCommand { get; }
    public RelayCommand DeleteSetCommand { get; }

    // --- Editing an existing set -------------------------------------------
    // Notes are usually written after the fact — "slot 2 is the no-mods run" is
    // something you learn once you have played it, not when you captured it.

    public string EditName
    {
        get => _editName;
        set => SetField(ref _editName, value);
    }

    public string EditPlayers
    {
        get => _editPlayers;
        set => SetField(ref _editPlayers, value);
    }

    public string EditNotes
    {
        get => _editNotes;
        set => SetField(ref _editNotes, value);
    }

    public string EditSlot1
    {
        get => _editSlot1;
        set => SetField(ref _editSlot1, value);
    }

    public string EditSlot2
    {
        get => _editSlot2;
        set => SetField(ref _editSlot2, value);
    }

    public string EditSlot3
    {
        get => _editSlot3;
        set => SetField(ref _editSlot3, value);
    }

    private void LoadEditFields(SaveSet? set)
    {
        EditName = set?.Name ?? string.Empty;
        EditPlayers = set is null ? string.Empty : string.Join(", ", set.Players);
        EditNotes = set?.Notes ?? string.Empty;
        EditSlot1 = SlotNote(set, "1");
        EditSlot2 = SlotNote(set, "2");
        EditSlot3 = SlotNote(set, "3");
    }

    private static string SlotNote(SaveSet? set, string slot) =>
        set is not null && set.SlotNotes.TryGetValue(slot, out var note) ? note : string.Empty;

    private void SaveEdits()
    {
        var service = Service;
        if (service is null || Selected is null) return;

        var originalName = Selected.Set.Name;
        var newName = EditName.Trim();

        Run(() =>
        {
            var name = originalName;
            if (!string.Equals(name, newName, StringComparison.Ordinal))
            {
                service.Rename(name, newName);
                name = newName;
            }

            service.EditMetadata(name,
                notes: EditNotes.Trim(),
                players: EditPlayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                slotNotes: new Dictionary<string, string>
                {
                    ["1"] = EditSlot1, ["2"] = EditSlot2, ["3"] = EditSlot3,
                });

            _shell.Report($"Saved changes to '{name}'.");
        });
    }

    private void DeleteSet()
    {
        var service = Service;
        if (service is null || Selected is null) return;

        if (MessageBox.Show(
                $"Remove save set '{Selected.Name}'?\n\n" +
                "Its folder is moved to .backup, not deleted, so it can be recovered.",
                "Remove save set", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var name = Selected.Set.Name;
        Run(() =>
        {
            var moved = service.DeleteSet(name);
            _shell.Report($"Removed '{name}' — kept at {Path.GetFileName(moved)}.");
        });
    }

    public RelayCommand RecheckCommand { get; }
    public RelayCommand TurnCloudOffCommand { get; }

    /// <summary>
    /// The user has checked Steam's own dialog. Steam only writes the setting on
    /// exit, so the file can disagree with what they are looking at — without an
    /// acknowledgement the gate can lock the feature shut permanently.
    /// Covers Steam Cloud only; the game-running and build checks still apply.
    /// </summary>
    public bool CloudAcknowledged
    {
        get => _cloudAcknowledged;
        set { if (SetField(ref _cloudAcknowledged, value)) RaiseGate(); }
    }

    public bool SteamRunning => SteamCloudService.IsSteamRunning();

    public string TurnCloudOffHint => SteamRunning
        ? "Exit Steam completely (tray icon → Exit) to enable this — Steam rewrites the file when it closes, so a change made now would be discarded."
        : "Writes cloudenabled \"0\" straight into Steam's config. The original is backed up first.";

    public ObservableCollection<SaveSetViewModel> Sets { get; } = new();
    public ObservableCollection<string> Backups { get; } = new();
    public ObservableCollection<string> LiveFiles { get; } = new();

    public RelayCommand CaptureCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand BackupNowCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand OpenLiveFolderCommand { get; }
    public RelayCommand OpenSetsFolderCommand { get; }
    public RelayCommand OpenSteamPropertiesCommand { get; }

    private SaveSetService? Service =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new SaveSetService(_shell.Process, new SteamCloudService(), _shell.Config!.SyncRoot!,
                                 _shell.Config!.SaveFolder, _shell.Config!.GameDir);

    public SteamCloudStatus? Cloud { get; private set; }
    /// <summary>
    /// The folder the game actually uses, not Steam's.
    ///
    /// This read Cloud.RemoteDir directly, which meant the path on screen and
    /// the reason printed under it could disagree - the panel said "reported by
    /// the game" above Steam's path. One source of truth, and it is the resolver.
    /// </summary>
    public string? LiveFolder => SaveFolder?.Path;

    public SaveSetViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            LoadEditFields(value?.Set);
            OnPropertyChanged(nameof(HasSelection));
            RaiseGate();
        }
    }

    public bool HasSelection => Selected is not null;

    public string NewSetName
    {
        get => _newSetName;
        set => SetField(ref _newSetName, value);
    }

    public string NewSetPlayers
    {
        get => _newSetPlayers;
        set => SetField(ref _newSetPlayers, value);
    }

    public string NewSetNotes
    {
        get => _newSetNotes;
        set => SetField(ref _newSetNotes, value);
    }

    public string? SelectedBackup
    {
        get => _selectedBackup;
        set => SetField(ref _selectedBackup, value);
    }

    // --- The gate -----------------------------------------------------------

    public bool CloudSafe => Cloud?.SafeToSwapSaves ?? false;

    public bool IsaacClosed => !_shell.IsIsaacRunning;

    /// <summary>Which build the launcher will start, so a mismatched set can be blocked.</summary>
    public GameBuild SelectedBuild => _shell.LauncherIni.GetLaunchMode() switch
    {
        LaunchMode.Repentogon => GameBuild.Repentogon,
        LaunchMode.Vanilla => GameBuild.Vanilla,
        _ => GameBuild.Unknown,
    };

    public bool CanActivate => Blockers.Count == 0;

    public IReadOnlyList<string> Blockers
    {
        get
        {
            var service = Service;
            if (service is null || Selected is null) return new[] { "No save set selected." };
            return service.Check(Selected.Set, SelectedBuild, CloudAcknowledged).Blockers;
        }
    }

    public string BlockersText => string.Join("\n\n", Blockers);

    public bool HasBlockers => Selected is not null && Blockers.Count > 0;

    public string CloudStateText => Cloud?.State switch
    {
        SteamCloudState.Disabled => "Steam Cloud is off for Isaac — save switching is safe.",
        SteamCloudState.Enabled => Cloud.ExplicitSetting
            ? "Steam Cloud is ON for Isaac. Turn it off before switching saves."
            : "Steam Cloud has never been turned off for Isaac, and the default is on. Turn it off before switching saves.",
        _ => "Steam Cloud state could not be read. Turn it off for Isaac to be sure.",
    };

    /// <summary>
    /// Steam keeps this setting in memory and writes it out when it exits, so a
    /// reading taken while it runs can disagree with the properties dialog.
    /// Without saying so, a stale value looks like the tool being broken.
    /// </summary>
    public string StalenessText
    {
        get
        {
            if (Cloud is null || !Cloud.SettingMayBeStale) return string.Empty;

            var written = Cloud.SettingWritten?.ToString("HH:mm:ss") ?? "unknown";
            return Cloud.State == SteamCloudState.Disabled
                ? $"Steam is running; this was read from its config file, last written at {written}."
                : "Steam is running, and it only writes this setting to disk when it exits. " +
                  $"If you have already turned Cloud off, close Steam completely and press Re-check — the file still says on, last written at {written}.";
        }
    }

    public bool ShowStaleness => StalenessText.Length > 0;

    public string LiveFolderText => LiveFolder ?? "(the game's save folder could not be found)";

    public string LastSyncText => Cloud?.LastSyncState is { Length: > 0 } s ? $"Steam's last sync state: {s}" : "";

    public void Refresh()
    {
        var previous = Selected?.Name;
        var previousBackup = SelectedBackup;

        Sets.Clear();
        Backups.Clear();
        LiveFiles.Clear();

        var service = Service;
        Cloud = service is null ? null : new SteamCloudService().GetStatus();

        if (service is not null)
        {
            foreach (var name in service.ListSets())
            {
                SaveSet? set;
                try { set = service.LoadSet(name); }
                catch (ConfigSchemaMismatchException ex) { _shell.Report(ex.Message); continue; }
                if (set is null) continue;

                Sets.Add(new SaveSetViewModel { Set = set, Drift = service.DetectDrift(set) });
            }

            foreach (var backup in service.ListBackups()) Backups.Add(backup);
            foreach (var file in service.ReadLive())
                LiveFiles.Add($"{file.FileName}  ({file.Length:N0} bytes, {file.Modified:MM-dd HH:mm})");
        }

        Selected = Sets.FirstOrDefault(s => s.Name == previous) ?? Sets.FirstOrDefault();
        SelectedBackup = Backups.Contains(previousBackup ?? "") ? previousBackup : Backups.FirstOrDefault();

        RaiseGate();
    }

    private void RaiseGate()
    {
        foreach (var name in new[]
                 {
                     nameof(Cloud), nameof(CloudSafe), nameof(CloudStateText), nameof(IsaacClosed),
                     nameof(CanActivate), nameof(Blockers), nameof(BlockersText), nameof(HasBlockers),
                     nameof(SaveFolder), nameof(SaveFolderSourceText), nameof(SaveFolderLooksWrong),
                     nameof(CloudApplies), nameof(CloudIrrelevantText), nameof(ShowCloudStaleness),
                     nameof(SaveFolderWarning),
                     nameof(LiveFolder), nameof(LiveFolderText), nameof(LastSyncText), nameof(SelectedBuild),
                     nameof(StalenessText), nameof(ShowStaleness), nameof(SteamRunning), nameof(TurnCloudOffHint),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // --- Actions ------------------------------------------------------------

    private void Capture()
    {
        var service = Service;
        if (service is null) return;

        var activeProfile = _shell.Config is null
            ? string.Empty
            : _shell.ModProfileService.GetActiveProfileFromDisk(_shell.Config) ?? _shell.Config.ActiveProfile ?? string.Empty;

        Run(() =>
        {
            var players = NewSetPlayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var set = service.Capture(NewSetName.Trim(), activeProfile, players, NewSetNotes.Trim());
            _shell.Report($"Captured '{set.Name}' — {set.BuildText}, {set.Files.Count} files, {set.SlotsText()}.");
            NewSetName = string.Empty;
            NewSetPlayers = string.Empty;
            NewSetNotes = string.Empty;
        });
    }

    /// <summary>
    /// Make an empty set and switch to it, so the next launch generates a new
    /// save rather than reusing the one that is live now.
    ///
    /// Two steps rather than one because Isaac writes the save, not us: this
    /// clears the folder, and the set stays empty until the game has run and
    /// the files are captured back into it.
    /// </summary>
    private void StartFresh()
    {
        var service = Service;
        if (service is null) return;

        var build = SelectedBuild;
        if (build == GameBuild.Unknown)
        {
            MessageBox.Show(
                "The launcher is not set to a build right now, so a new set cannot record which " +
                "build its save will belong to. Pick a build on the Build tab first.",
                "New save set", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var buildName = build == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
        var name = NewSetName.Trim();

        if (MessageBox.Show(
                $"Start '{name}' as a brand-new {buildName} save?\n\n" +
                "Your current save files are backed up and then cleared out of Steam's folder, so " +
                "Isaac writes a fresh one the next time it starts. Nothing is deleted - the backup " +
                "is kept, and any save set you already made is untouched.\n\n" +
                "Launch the game, get to the menu, then come back and press " +
                "\"Capture the new save\" to fill this set in.",
                "New save set", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var activeProfile = _shell.Config is null
            ? string.Empty
            : _shell.ModProfileService.GetActiveProfileFromDisk(_shell.Config) ?? _shell.Config.ActiveProfile ?? string.Empty;

        Run(() =>
        {
            var players = NewSetPlayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var set = service.CreateEmpty(name, build, activeProfile, players, NewSetNotes.Trim());
            var backup = service.Activate(set, build, CloudAcknowledged);

            _shell.Report($"'{set.Name}' is empty and live saves are cleared - launch Isaac, then capture. " +
                          $"Previous saves backed up to {Path.GetFileName(backup)}.");
            NewSetName = string.Empty;
            NewSetPlayers = string.Empty;
            NewSetNotes = string.Empty;
        });
    }

    /// <summary>Copy the live saves into the set that is already selected.</summary>
    private void CaptureInto()
    {
        var service = Service;
        if (service is null || Selected is null) return;

        var set = Selected.Set;
        var replacing = set.Files.Count > 0;

        if (replacing && MessageBox.Show(
                $"'{set.Name}' already holds {set.Files.Count} file(s).\n\n" +
                "Replace them with what is live now? The set will describe the current save instead " +
                "of the one it was captured from.",
                "Capture into set", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var filled = service.CaptureInto(set);
            _shell.Report($"Captured into '{filled.Name}' - {filled.Files.Count} files, {filled.SlotsText()}.");
        });
    }

    private void DeleteBackup()
    {
        var service = Service;
        if (service is null || SelectedBackup is null) return;

        var name = SelectedBackup;
        var (files, bytes) = service.MeasureBackup(name);

        if (MessageBox.Show(
                $"Delete the backup '{name}'?\n\n" +
                $"{files} file(s), {bytes / 1024d:N0} KB.\n\n" +
                "This one is not recoverable. Backups are taken automatically before every swap, so " +
                "this may be the only copy of the saves it holds.",
                "Delete backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            service.DeleteBackup(name);
            _shell.Report($"Deleted the backup '{name}'.");
        });
    }

    private void OpenBackupsFolder()
    {
        var service = Service;
        if (service is null) return;

        Directory.CreateDirectory(service.BackupRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{service.BackupRoot}\"") { UseShellExecute = true });
    }

    private void Activate()
    {
        var service = Service;
        if (service is null || Selected is null) return;

        var drift = Selected.Drift;
        var warning = drift.Count > 0
            ? $"\n\nWARNING: {drift.Count} live file(s) have changed since this set was captured " +
              "— that is progress made since, and it will be overwritten (a backup is taken first)."
            : string.Empty;

        if (MessageBox.Show(
                $"Load save set '{Selected.Name}'?\n\n" +
                $"Build: {Selected.BuildText}\nSlots: {Selected.SlotsText}\n\n" +
                $"Your current saves are backed up first." + warning,
                "Load save set", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var backup = service.Activate(Selected.Set, SelectedBuild, CloudAcknowledged);
            _shell.Report($"Loaded '{Selected.Name}'. Previous saves backed up to {Path.GetFileName(backup)}.");
        });
    }

    private void BackupNow()
    {
        var service = Service;
        if (service is null) return;

        Run(() =>
        {
            var folder = service.BackupLive("manual");
            _shell.Report($"Backed up current saves to {Path.GetFileName(folder)}.");
        });
    }

    private void Restore()
    {
        var service = Service;
        if (service is null || SelectedBackup is null) return;

        if (MessageBox.Show(
                $"Restore backup '{SelectedBackup}' over your current saves?\n\n" +
                "What is there now is backed up first.",
                "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Run(() =>
        {
            var safety = service.RestoreBackup(SelectedBackup);
            _shell.Report($"Restored '{SelectedBackup}'. Previous state kept at {Path.GetFileName(safety)}.");
        });
    }

    private void OpenLiveFolder()
    {
        if (LiveFolder is null || !Directory.Exists(LiveFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LiveFolder}\"") { UseShellExecute = true });
    }

    private void OpenSetsFolder()
    {
        var root = Service?.SetsRoot;
        if (root is null) return;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    /// <summary>Opens the game's properties dialog, which is where the Cloud toggle lives.</summary>
    /// <summary>Set Steam's own flag, with Steam closed so the change survives.</summary>
    private void TurnCloudOff()
    {
        if (MessageBox.Show(
                "Turn Steam Cloud off for Isaac by editing Steam's config directly?\n\n" +
                "Steam must stay closed until this finishes. Your current sharedconfig.vdf is backed up first, " +
                "and you can turn Cloud back on in Steam whenever you like.",
                "Turn Cloud off", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var backup = new SteamCloudService().SetCloudEnabled(false);
            _shell.Report($"Steam Cloud turned off for Isaac. Original config backed up to {Path.GetFileName(backup)}.");
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Turn Cloud off", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private void OpenSteamProperties()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SteamCloudService.PropertiesUrl()) { UseShellExecute = true });
            _shell.Report("Opened Steam. Under General, turn off 'Keep game saves in the Steam Cloud'.");
        }
        catch (Exception ex)
        {
            _shell.Report($"Could not open Steam: {ex.Message}");
        }
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Saves", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }
}

internal static class SaveSetExtensions
{
    public static string SlotsText(this SaveSet set) =>
        set.Slots.Count > 0 ? $"slots {string.Join(", ", set.Slots)}" : "no slots";
}
