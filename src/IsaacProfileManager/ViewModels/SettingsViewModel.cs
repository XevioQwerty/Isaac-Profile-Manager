using System.Diagnostics;
using System.IO;
using System.Windows;

namespace IsaacProfileManager.ViewModels;

/// <summary>
/// Shows what the config points at and lets the paths be corrected. Deliberately
/// thin: IsaacProfiles.ps1 -Setup remains the first-run wizard, and this must not
/// grow into a second one that writes a subtly different config.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private bool _perProfileBuild;
    private string _detectionResult = string.Empty;

    public SettingsViewModel(MainViewModel shell)
    {
        _shell = shell;

        BrowseSyncRootCommand = new RelayCommand(BrowseSyncRoot, () => _shell.Config is not null);
        BrowseIsaacExeCommand = new RelayCommand(BrowseIsaacExe, () => _shell.Config is not null);
        BrowseLauncherCommand = new RelayCommand(BrowseLauncher, () => _shell.Config is not null);
        DetectCommand = new RelayCommand(Detect);
        OpenLauncherIniCommand = new RelayCommand(OpenLauncherIni, () => _shell.LauncherIni.Exists);
        StartLauncherCommand = new RelayCommand(StartLauncher, () => File.Exists(_shell.Config?.LauncherExe));
        BrowseLaunchTargetCommand = new RelayCommand(BrowseLaunchTarget, () => _shell.Config is not null);
    }

    public RelayCommand BrowseLaunchTargetCommand { get; }

    // --- Backups ------------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<Core.Services.BackupEntry> Backups { get; } = new();

    public string BackupSummary { get; private set; } = string.Empty;
    public string PrunePlanText { get; private set; } = string.Empty;

    private Core.Services.BackupService? BackupService =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot) ? null : new(_shell.Config!.SyncRoot!);

    public RelayCommand ScanBackupsCommand => new(ScanBackups, () => BackupService is not null);

    public RelayCommand PruneBackupsCommand => new(
        () =>
        {
            var service = BackupService;
            if (service is null) return;

            var plan = service.PlanPrune();
            if (plan.Count == 0) { _shell.Report("Nothing to prune."); return; }

            if (MessageBox.Show(
                    $"Delete {plan.Count} old backup(s), freeing {plan.Sum(e => e.SizeMb):N1} MB?\n\n" +
                    "Only copies are removed — anything moved here instead of deleted is never touched, " +
                    "because it may be the only remaining instance.",
                    "Prune backups", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var removed = service.Prune();
            _shell.Report($"Removed {removed.Count} old backup(s).");
            ScanBackups();
        },
        () => BackupService is not null);

    private void ScanBackups()
    {
        var service = BackupService;
        Backups.Clear();
        if (service is null) { BackupSummary = string.Empty; RaiseBackups(); return; }

        foreach (var entry in service.Scan()) Backups.Add(entry);

        var moved = Backups.Count(b => b.Kind == Core.Services.BackupKind.MovedOriginal);
        BackupSummary = $"{Backups.Count} backup(s), {Backups.Sum(b => b.SizeMb):N1} MB — " +
                        $"{Backups.Count - moved} copies, {moved} moved here (never pruned automatically).";

        var plan = service.PlanPrune();
        PrunePlanText = plan.Count == 0
            ? "Nothing is old enough to prune."
            : $"Pruning would remove {plan.Count} copy backup(s), freeing {plan.Sum(e => e.SizeMb):N1} MB.";

        RaiseBackups();
    }

    private void RaiseBackups()
    {
        OnPropertyChanged(nameof(BackupSummary));
        OnPropertyChanged(nameof(PrunePlanText));
    }

    /// <summary>Hand off to Steam, so its launch options apply.</summary>
    public bool LaunchViaSteam
    {
        get => _shell.Config is not null &&
               _shell.Launcher.ResolveMethod(_shell.Config) == Core.Services.GameLaunchMethod.Steam;
        set { if (value) SetLaunchMethod(Core.Services.GameLaunchMethod.Steam); }
    }

    /// <summary>Run an executable directly — normally REPENTOGONLauncher.exe.</summary>
    public bool LaunchViaFile
    {
        get => _shell.Config is not null &&
               _shell.Launcher.ResolveMethod(_shell.Config) == Core.Services.GameLaunchMethod.File;
        set { if (value) SetLaunchMethod(Core.Services.GameLaunchMethod.File); }
    }

    public string LaunchTargetText =>
        Core.Services.GameLauncherService.ResolveTarget(_shell.Config ?? new Core.Models.AppConfig())
        ?? "(nothing chosen)";

    public string SteamUrlText => Core.Services.GameLauncherService.SteamUrl;

    /// <summary>Exactly what the Launch button will run, resolved the same way it resolves.</summary>
    public string LaunchPreviewText
    {
        get
        {
            if (_shell.Config is null) return string.Empty;
            try
            {
                var plan = _shell.Launcher.Resolve(_shell.Config);
                return plan.Arguments.Length > 0
                    ? $"{plan.Target} {plan.Arguments}\n{plan.Summary}"
                    : $"{plan.Target}\n{plan.Summary}";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    private void SetLaunchMethod(Core.Services.GameLaunchMethod method)
    {
        if (_shell.Config is null) return;
        _shell.Config.LaunchMethod = method.ToString();
        _shell.SaveConfig();
        RaiseLaunchProperties();
        _shell.RefreshStatusBar();
    }

    private void BrowseLaunchTarget()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose what the Launch button should run",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true || _shell.Config is null) return;

        _shell.Config.LaunchTarget = dialog.FileName;
        _shell.Config.LaunchMethod = nameof(Core.Services.GameLaunchMethod.File);
        _shell.SaveConfig();
        RaiseLaunchProperties();
        _shell.RefreshStatusBar();
        _shell.Report($"Launch button will run {Path.GetFileName(dialog.FileName)}");
    }

    private void RaiseLaunchProperties()
    {
        OnPropertyChanged(nameof(LaunchViaSteam));
        OnPropertyChanged(nameof(LaunchViaFile));
        OnPropertyChanged(nameof(LaunchTargetText));
        OnPropertyChanged(nameof(LaunchPreviewText));
    }

    public RelayCommand BrowseSyncRootCommand { get; }
    public RelayCommand BrowseIsaacExeCommand { get; }
    public RelayCommand BrowseLauncherCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand OpenLauncherIniCommand { get; }
    public RelayCommand StartLauncherCommand { get; }

    public string ConfigPath => _shell.Store.ConfigPath ?? "(not found)";

    // --- Save sync between your own machines --------------------------------

    public string SyncMode
    {
        get => _shell.SaveSyncMode;
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            config.SaveSyncMode = value;
            _shell.SaveConfig();
            RaiseSync();
        }
    }

    public bool SyncIsFolder => SyncMode == MainViewModel.SyncFolder;
    public bool SyncIsCloud => SyncMode == MainViewModel.SyncCloud;

    public string SyncFolder
    {
        get => _shell.Config?.SaveSyncFolder ?? _shell.DefaultSaveSyncFolder;
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            config.SaveSyncFolder = string.IsNullOrWhiteSpace(value) || value == _shell.DefaultSaveSyncFolder ? null : value.Trim();
            _shell.SaveConfig();
            OnPropertyChanged();
        }
    }

    public string SyncEndpoint
    {
        get => _shell.Config?.SaveSyncEndpoint ?? string.Empty;
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            config.SaveSyncEndpoint = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _shell.SaveConfig();
            OnPropertyChanged();
        }
    }

    public string SyncKey
    {
        get => _shell.Config?.SaveSyncKey ?? string.Empty;
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            config.SaveSyncKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _shell.SaveConfig();
            OnPropertyChanged();
        }
    }

    public bool SyncAutomatic
    {
        get => _shell.SaveSyncAutomatic;
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            config.SaveSyncAutomatic = value;
            _shell.SaveConfig();
            OnPropertyChanged();
        }
    }

    private string _syncTestText = string.Empty;

    public string SyncTestText
    {
        get => _syncTestText;
        private set => SetField(ref _syncTestText, value);
    }

    /// <summary>A fresh key: 32 random bytes, URL-safe. The Worker hashes it into the namespace; the key itself never leaves the two machines.</summary>
    public RelayCommand GenerateSyncKeyCommand => new(() =>
    {
        if (SyncKey.Length > 0 && System.Windows.MessageBox.Show(
                "Replace the current sync key? Every other machine will need the new one, and lanes pushed under the old key become unreachable from here.",
                "New sync key", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        SyncKey = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _shell.Report("Generated a sync key. Copy it to your other machine.");
    }, () => _shell.Config is not null);

    public RelayCommand CopySyncKeyCommand => new(() =>
    {
        try { System.Windows.Clipboard.SetText(SyncKey); _shell.Report("Sync key copied."); }
        catch (Exception ex) { _shell.Report(ex.Message); }
    }, () => SyncKey.Length > 0);

    public RelayCommand BrowseSyncFolderCommand => new(() =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder your sync client carries between machines" };
        if (dialog.ShowDialog() == true) SyncFolder = dialog.FolderName;
    }, () => _shell.Config is not null);

    public RelayCommand TestSyncCommand => new(() => _ = TestSyncAsync(), () => _shell.SaveSyncEnabled);

    private async Task TestSyncAsync()
    {
        SyncTestText = "checking…";
        try
        {
            var service = _shell.CreateSaveSyncService();
            if (service is null) { SyncTestText = "Sync is off."; return; }
            var statuses = await Task.Run(() => service.StatusAsync());
            var remote = statuses.Count(s => s.Newest is not null);
            SyncTestText = $"OK — {service.Store.Description}: {remote} set(s) with a lane, " +
                           $"{statuses.Count(s => s.NeedsPush)} to push, {statuses.Count(s => s.CanPull)} to pull.";
        }
        catch (Exception ex) when (ex is Core.Services.SaveSyncException or IOException or UnauthorizedAccessException)
        {
            SyncTestText = ex.Message;
        }
    }

    private void RaiseSync()
    {
        foreach (var name in new[] { nameof(SyncMode), nameof(SyncIsFolder), nameof(SyncIsCloud), nameof(SyncFolder),
                                     nameof(SyncEndpoint), nameof(SyncKey), nameof(SyncAutomatic) })
            OnPropertyChanged(name);
    }

    /// <summary>The orientation cards at the top of each screen. Lives on the shell; this is the checkbox.</summary>
    public bool ShowGuides
    {
        get => _shell.ShowGuides;
        set { _shell.ShowGuides = value; OnPropertyChanged(); }
    }

    /// <summary>The shell changed it (the Hide tips link); the checkbox must follow or it shows a stale tick.</summary>
    public void RaiseShowGuides()
    {
        OnPropertyChanged(nameof(ShowGuides));
        RaiseSync();
    }

    /// <summary>How this machine is named in the save sets it captures.</summary>
    public string DeviceText => _shell.Config is { DeviceId: { Length: > 0 } id, DeviceName: var name }
        ? $"This device: {name ?? "(unnamed)"}  ·  id {(id.Length > 8 ? id[..8] : id)}"
        : string.Empty;
    public string GameDir => _shell.Config?.GameDir ?? string.Empty;
    public string ModsDir => _shell.Config?.ModsDir ?? string.Empty;
    public string SyncRoot => _shell.Config?.SyncRoot ?? string.Empty;
    public string IsaacExe => _shell.Config?.IsaacExe ?? string.Empty;
    public string LauncherExe => _shell.Config?.LauncherExe ?? "(none configured)";
    public string LauncherIniPath => _shell.LauncherIni.IniPath;
    public string BuildRoot => _shell.Config is null ? string.Empty : Core.Services.BuildVariantService.ResolveBuildRoot(_shell.Config);

    public string DetectionResult
    {
        get => _detectionResult;
        private set => SetField(ref _detectionResult, value);
    }

    // --- Steam launch options ----------------------------------------------
    // Without the launcher line Steam starts the game directly, [Shared]
    // LaunchMode is never read, and per-profile build selection silently does
    // nothing — a setup error that looks like the switcher being broken.

    private Core.Services.LaunchOptionsStatus? LaunchOptions =>
        _shell.Config is null ? null : new Core.Services.SteamLaunchOptionsService().Check(_shell.Config.LauncherExe);

    public string LaunchOptionsText => LaunchOptions?.State switch
    {
        Core.Services.LaunchOptionsState.LauncherConfigured => "Steam is set to start REPENTOGONLauncher — per-profile build selection will work.",
        Core.Services.LaunchOptionsState.Empty => "Steam has no launch options set, so it starts the game directly and the build never follows the profile.",
        Core.Services.LaunchOptionsState.Other => "Steam's launch options do not start REPENTOGONLauncher with %command%, so the build will not follow the profile.",
        _ => "Steam's launch options could not be read.",
    };

    public string LaunchOptionsCurrent => LaunchOptions?.Current is { Length: > 0 } c ? c : "(none set)";

    public string LaunchOptionsSuggested => LaunchOptions?.Suggested ?? "(set the REPENTOGON launcher path first)";

    public bool LaunchOptionsCorrect => LaunchOptions?.IsCorrect ?? false;

    public RelayCommand CopyLaunchOptionsCommand => new(
        () =>
        {
            var suggested = LaunchOptions?.Suggested;
            if (suggested is null) return;
            try { System.Windows.Clipboard.SetText(suggested); _shell.Report("Copied. Paste it into Steam → Isaac → Properties → Launch Options."); }
            catch (Exception ex) { _shell.Report($"Could not copy: {ex.Message}"); }
        },
        () => LaunchOptions?.Suggested is not null);

    /// <summary>
    /// Write the launch options into Steam's config rather than making the user
    /// paste them into a dialog.
    ///
    /// Steam must be closed: it holds localconfig.vdf in memory and rewrites it
    /// on exit, so a write while it runs is silently discarded. The service
    /// refuses in that case rather than reporting a success that will evaporate.
    /// </summary>
    public RelayCommand ApplyLaunchOptionsCommand => new(
        () =>
        {
            var launcher = _shell.Config?.LauncherExe;
            if (string.IsNullOrWhiteSpace(launcher)) return;

            var result = new Core.Services.SteamLaunchOptionsService().Apply(launcher);

            var message = result.Message;
            if (result.BackupPath is not null)
                message += Environment.NewLine + Environment.NewLine +
                           $"Steam's previous config was backed up to:{Environment.NewLine}{result.BackupPath}";

            _shell.Report(result.Message);
            System.Windows.MessageBox.Show(message, "Steam launch options",
                                           System.Windows.MessageBoxButton.OK,
                                           result.Ok ? System.Windows.MessageBoxImage.Information
                                                     : System.Windows.MessageBoxImage.Warning);
            Refresh();
        },
        () => _shell.Config?.LauncherExe is { Length: > 0 });

    /// <summary>
    /// When on, activating a profile also writes [Shared] LaunchMode so the build
    /// follows the profile. Requires Steam to launch the launcher rather than the
    /// game — see the README's launch-options section.
    /// </summary>
    public bool PerProfileBuild
    {
        get => _perProfileBuild;
        set
        {
            if (!SetField(ref _perProfileBuild, value)) return;
            if (_shell.Config is null) return;
            _shell.Config.PerProfileBuild = value;
            _shell.SaveConfig();
            _shell.ModProfiles.Refresh();
            _shell.Report(value
                ? "Per-profile build selection is on. Activating a profile now sets the launcher's build."
                : "Per-profile build selection is off. The launcher keeps whatever build it is set to.");
        }
    }

    public void Refresh()
    {
        _perProfileBuild = _shell.Config?.PerProfileBuild ?? false;
        OnPropertyChanged(nameof(PerProfileBuild));

        foreach (var name in new[]
                 {
                     nameof(ConfigPath), nameof(GameDir), nameof(ModsDir), nameof(SyncRoot),
                     nameof(IsaacExe), nameof(LauncherExe), nameof(LauncherIniPath), nameof(BuildRoot),
                     nameof(LaunchOptionsText), nameof(LaunchOptionsCurrent), nameof(LaunchOptionsSuggested),
                     nameof(LaunchOptionsCorrect),
                 })
        {
            OnPropertyChanged(name);
        }

        RaiseLaunchProperties();
    }

    private void Detect()
    {
        var install = _shell.Detection.FindInstall();
        if (install is null)
        {
            DetectionResult = "No install found automatically. Set the paths by hand.";
            return;
        }

        var launcher = _shell.Detection.FindRepentogonLauncher(install.GameDir);
        DetectionResult =
            $"Found via {install.Source}:\n{install.IsaacExe}\n" +
            (launcher is null ? "No REPENTOGONLauncher.exe found." : $"Launcher: {launcher}");

        if (_shell.Config is null) return;
        if (MessageBox.Show($"Use this install?\n\n{install.IsaacExe}", "Detected install",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _shell.Config.IsaacExe = install.IsaacExe;
        _shell.Config.GameDir = install.GameDir;
        _shell.Config.ModsDir = install.ModsDir;
        if (launcher is not null) _shell.Config.LauncherExe = launcher;
        _shell.SaveConfig();
        _shell.Reload();
        _shell.Report("Paths updated from detection.");
    }

    private void BrowseSyncRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder that holds your mod profiles",
            InitialDirectory = Directory.Exists(SyncRoot) ? SyncRoot : string.Empty,
        };
        if (dialog.ShowDialog() != true || _shell.Config is null) return;

        var chosen = dialog.FolderName;
        // Profiles inside the game directory would sit under the very folder we
        // replace with a junction.
        if (!string.IsNullOrWhiteSpace(GameDir) &&
            chosen.TrimEnd('\\').StartsWith(GameDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("That folder is inside the game directory. Pick somewhere else.",
                            "Profiles folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _shell.Config.SyncRoot = chosen;
        _shell.SaveConfig();
        _shell.Reload();
        _shell.Report($"Profiles folder set to {chosen}");
    }

    private void BrowseIsaacExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select isaac-ng.exe",
            Filter = "Isaac executable|isaac-ng.exe|All executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true || _shell.Config is null) return;

        var gameDir = Path.GetDirectoryName(dialog.FileName)!;
        _shell.Config.IsaacExe = dialog.FileName;
        _shell.Config.GameDir = gameDir;
        _shell.Config.ModsDir = Path.Combine(gameDir, "mods");
        _shell.SaveConfig();
        _shell.Reload();
        _shell.Report($"Game directory set to {gameDir}");
    }

    private void BrowseLauncher()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select REPENTOGONLauncher.exe",
            Filter = "REPENTOGON launcher|REPENTOGONLauncher.exe|All executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true || _shell.Config is null) return;

        var resolved = Core.Services.GameDetectionService.ResolveLauncherPath(dialog.FileName);
        if (resolved is null)
        {
            MessageBox.Show("That is not REPENTOGONLauncher.exe.", "Launcher",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _shell.Config.LauncherExe = resolved;
        _shell.SaveConfig();
        _shell.Reload();
        _shell.Report($"Launcher set to {resolved}");
    }

    private void OpenLauncherIni()
    {
        if (!_shell.LauncherIni.Exists) return;
        Process.Start(new ProcessStartInfo(_shell.LauncherIni.IniPath) { UseShellExecute = true });
    }

    /// <summary>
    /// Start the launcher with the vanilla exe path; it resolves the Repentogon
    /// build itself. Repentogon\isaac-ng.exe refuses to be started directly.
    /// </summary>
    private void StartLauncher()
    {
        var launcher = _shell.Config?.LauncherExe;
        var isaac = _shell.Config?.IsaacExe;
        if (launcher is null || !File.Exists(launcher)) return;

        try
        {
            var info = new ProcessStartInfo(launcher) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launcher)! };
            if (!string.IsNullOrWhiteSpace(isaac)) info.Arguments = $"--isaac=\"{isaac}\"";
            Process.Start(info);
            _shell.Report("Started REPENTOGONLauncher.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
