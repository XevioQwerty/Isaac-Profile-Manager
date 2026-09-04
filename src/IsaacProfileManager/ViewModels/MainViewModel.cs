using System.IO;
using System.Windows.Media;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;

namespace IsaacProfileManager.ViewModels;

public enum ShellState
{
    Ready,
    NoConfig,
    ConfigError,
}

/// <summary>
/// Owns the services and the loaded config, and holds the status bar every
/// screen reports into. The bar answers most support questions on its own if
/// it is visible: which profile, which save set, which build, and whether the
/// game is running.
///
/// Since 2.0 the shell is a rail rather than a tab strip, and the first item
/// on it — Play — is where the Launch button lives. The shell also watches for
/// the game process, so the Play screen can pick up the run you just played.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const int PlayTab = 0;
    public const int ModsTab = 1;
    public const int SavesTab = 2;
    public const int GameTab = 3;
    public const int DiagnoseTab = 4;
    public const int SettingsTab = 5;

    private readonly ConfigStore _store;
    private readonly JunctionService _junctions = new();
    private readonly GameProcessService _process = new();
    private readonly GameSessionWatcher _watcher;

    private ShellState _state;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private string _configErrorText = string.Empty;

    public MainViewModel()
    {
        _store = new ConfigStore();
        LauncherIni = new LauncherIniService();
        Detection = new GameDetectionService(LauncherIni);

        ModProfileService = new ModProfileService(_junctions, LauncherIni, _store);
        BuildVariantService = new BuildVariantService(_junctions, _process, _store);

        ModProfiles = new ModProfilesViewModel(this);
        BuildVariants = new BuildVariantsViewModel(this);
        Workshop = new WorkshopViewModel(this);
        Library = new LibraryViewModel(this);
        Saves = new SavesViewModel(this);
        Debug = new DebugViewModel(this);
        Settings = new SettingsViewModel(this);
        Setup = new SetupViewModel(this);
        Play = new PlayViewModel(this);

        RefreshCommand = new RelayCommand(() => Reload());
        LocateConfigCommand = new RelayCommand(LocateConfig);
        LaunchGameCommand = new RelayCommand(() => Play.Launch(), () => Config is not null);

        // The game is started by Steam or the launcher, not by us, so there is
        // no process handle to wait on. Poll, and marshal back to the UI thread.
        _watcher = new GameSessionWatcher(_process);
        _watcher.Started += () => OnUiThread(() => { RefreshStatusBar(); Play.OnGameStarted(); });
        _watcher.Exited += () => OnUiThread(() => { RefreshStatusBar(); Play.OnGameExited(); });

        Reload();
        _watcher.Start();
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public LauncherIniService LauncherIni { get; }
    public GameDetectionService Detection { get; }
    public ModProfileService ModProfileService { get; }
    public BuildVariantService BuildVariantService { get; }
    public ConfigStore Store => _store;

    private int _selectedTabIndex;
    private int _selectedModsSegment;

    /// <summary>Which rail item is showing.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetField(ref _selectedTabIndex, value);
    }

    /// <summary>Which of the three Mods segments is showing: profiles, library, workshop.</summary>
    public int SelectedModsSegment
    {
        get => _selectedModsSegment;
        set => SetField(ref _selectedModsSegment, value);
    }

    /// <summary>Kept for the profiles screen, which calls it when its patch list changes.</summary>
    public void NotifyQuickPatchesChanged() { }

    public PlayViewModel Play { get; }
    public ModProfilesViewModel ModProfiles { get; }
    public BuildVariantsViewModel BuildVariants { get; }
    public WorkshopViewModel Workshop { get; }
    public LibraryViewModel Library { get; }
    public SavesViewModel Saves { get; }
    public DebugViewModel Debug { get; }
    public SettingsViewModel Settings { get; }
    public SetupViewModel Setup { get; }

    /// <summary>No usable config yet, so the whole window is the setup wizard.</summary>
    public bool NeedsSetup => State == ShellState.NoConfig;

    public JunctionService Junctions => _junctions;
    public GameProcessService Process => _process;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand LocateConfigCommand { get; }

    /// <summary>Launch, through the guard on the Play screen.</summary>
    public RelayCommand LaunchGameCommand { get; }

    public GameLauncherService Launcher { get; } = new();

    /// <summary>Sits under the Launch button so it is obvious what pressing it does.</summary>
    public string LaunchButtonHint
    {
        get
        {
            if (Config is null) return string.Empty;
            return Launcher.ResolveMethod(Config) == GameLaunchMethod.Steam
                ? "via Steam"
                : $"via {Path.GetFileName(GameLauncherService.ResolveTarget(Config) ?? "?")}";
        }
    }

    public AppConfig? Config { get; private set; }

    public ShellState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(HasConfigProblem));
                OnPropertyChanged(nameof(NeedsSetup));
                OnPropertyChanged(nameof(ShowConfigError));
            }
        }
    }

    public bool IsReady => State == ShellState.Ready;
    public bool HasConfigProblem => State != ShellState.Ready;

    /// <summary>A config exists but cannot be used — a different problem from having none.</summary>
    public bool ShowConfigError => State == ShellState.ConfigError;

    public string ConfigErrorText
    {
        get => _configErrorText;
        private set => SetField(ref _configErrorText, value);
    }

    public string ConfigPathText => _store.ConfigPath ?? "(not found)";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    /// <summary>
    /// The orientation card at the top of every screen. One switch for all of
    /// them: a person who has learned the app hides them once, and Settings
    /// brings them back for the next person at the keyboard.
    /// </summary>
    public bool ShowGuides
    {
        get => Config?.ShowGuides ?? true;
        set
        {
            if (Config is null || ShowGuides == value) return;
            Config.ShowGuides = value;
            SaveConfig();
            OnPropertyChanged();
            Settings.RaiseShowGuides();
        }
    }

    public RelayCommand HideGuidesCommand => new(() => ShowGuides = false);

    /// <summary>
    /// The save set service with everything this machine knows: its device id,
    /// REPENTOGON's settings folder, and how to read the game version. One
    /// place, so the Saves and Play screens cannot disagree about it.
    /// </summary>
    public SaveSetService? CreateSaveSetService()
    {
        var config = Config;
        if (config is null || string.IsNullOrWhiteSpace(config.SyncRoot)) return null;

        return new SaveSetService(_process, new SteamCloudService(), config.SyncRoot!, config.SaveFolder, config.GameDir,
            new SaveSetOptions
            {
                RepentogonStateFolder = RepentogonStateCarrier.DefaultStateFolder,
                DeviceId = config.DeviceId,
                DeviceName = config.DeviceName,
                ReadGameVersion = () => new LogReaderService().ReadGameVersion(),
            });
    }

    // --- Save sync ----------------------------------------------------------

    public const string SyncOff = "Off";
    public const string SyncFolder = "Folder";
    public const string SyncCloud = "Cloud";

    public string SaveSyncMode => Config?.SaveSyncMode is SyncFolder or SyncCloud ? Config.SaveSyncMode : SyncOff;

    public bool SaveSyncEnabled => SaveSyncMode != SyncOff;

    public bool SaveSyncAutomatic => Config?.SaveSyncAutomatic ?? false;

    public string DefaultSaveSyncFolder =>
        string.IsNullOrWhiteSpace(Config?.SyncRoot) ? string.Empty : Path.Combine(Config!.SyncRoot!, ".savesync");

    /// <summary>The lane store the config names, or null when sync is off. Throws when Cloud is chosen but incomplete.</summary>
    public ISaveLaneStore? CreateLaneStore()
    {
        var config = Config;
        if (config is null) return null;

        return SaveSyncMode switch
        {
            SyncFolder => new FolderLaneStore(string.IsNullOrWhiteSpace(config.SaveSyncFolder) ? DefaultSaveSyncFolder : config.SaveSyncFolder!),
            SyncCloud => new HttpLaneStore(config.SaveSyncEndpoint ?? string.Empty, config.SaveSyncKey ?? string.Empty),
            _ => null,
        };
    }

    public SaveSyncService? CreateSaveSyncService()
    {
        var config = Config;
        var sets = CreateSaveSetService();
        var store = CreateLaneStore();
        if (config is null || sets is null || store is null) return null;

        if (DeviceService.Ensure(config, out var device)) SaveConfig();
        return new SaveSyncService(sets, store, device);
    }

    // --- Status bar ---------------------------------------------------------

    public string ActiveProfileText =>
        Config is null ? "—" : ModProfileService.GetActiveProfileFromDisk(Config) ?? "(not linked)";

    public string ActiveSaveSetText => Play.Identity?.Text ?? "—";

    /// <summary>The whole launch guard as one dot.</summary>
    public Brush GuardBrush => PlayViewModel.BrushFor(Play.Verdict.Worst);

    public string GuardText => Play.VerdictText;

    public string ActiveBuildText
    {
        get
        {
            if (Config is null) return "—";
            var status = BuildVariantService.GetStatus(Config);
            return status.State switch
            {
                BuildLinkState.Linked => status.ActiveVariant!,
                BuildLinkState.RealFolder => "not set up",
                BuildLinkState.LinkedElsewhere => "linked elsewhere",
                _ => "none",
            };
        }
    }

    /// <summary>
    /// What the launcher will start next. Re-read every refresh: the launcher
    /// rewrites its ini on exit, so a value we wrote is never durable.
    /// </summary>
    public string LaunchModeText => LauncherIni.GetLaunchMode() switch
    {
        LaunchMode.Repentogon => "REPENTOGON",
        LaunchMode.Vanilla => "vanilla",
        _ => "no launcher ini",
    };

    public bool IsIsaacRunning => _process.IsIsaacRunning();

    public string GameRunningText => IsIsaacRunning ? "Isaac is RUNNING" : "Isaac is closed";

    public void Report(string message) => StatusMessage = message;

    public void Reload()
    {
        try
        {
            if (!_store.Exists)
            {
                Config = null;
                State = ShellState.NoConfig;
                ConfigErrorText = "No isaac-profiles.json found.";
            }
            else
            {
                Config = _store.Load();
                State = ShellState.Ready;
                ConfigErrorText = string.Empty;

                // Name this machine once. Save sets record which device captured them.
                if (DeviceService.Ensure(Config, out _)) _store.Save(Config);
            }
        }
        catch (Exception ex)
        {
            Config = null;
            State = ShellState.ConfigError;
            ConfigErrorText = ex.Message;
        }

        ModProfiles.Refresh();
        BuildVariants.Refresh();
        Workshop.Refresh();
        Library.Refresh();
        Saves.Refresh();
        Debug.Refresh();
        Settings.Refresh();
        Play.Refresh();
        RefreshStatusBar();
        OnPropertyChanged(nameof(ConfigPathText));
        OnPropertyChanged(nameof(ShowGuides));
        Settings.RaiseShowGuides();
    }

    /// <summary>
    /// Refresh one screen, by its position on the rail. Cheaper than Reload,
    /// which re-reads the config and every screen.
    /// </summary>
    public void RefreshSelectedTab(int index)
    {
        if (State != ShellState.Ready) return;

        switch (index)
        {
            case PlayTab: Play.Refresh(); break;
            case ModsTab:
                switch (SelectedModsSegment)
                {
                    case 0: ModProfiles.Refresh(); break;
                    case 1: Library.Refresh(); break;
                    case 2: Workshop.Refresh(); break;
                }
                break;
            case SavesTab: Saves.Refresh(); break;
            case GameTab: BuildVariants.Refresh(); break;
            case DiagnoseTab: Debug.Refresh(); break;
            case SettingsTab: Settings.Refresh(); break;
        }

        RefreshStatusBar();
    }

    /// <summary>
    /// The window came back to the front — typically because the game just
    /// closed. Re-run the pre-flight check so the status bar and Play screen
    /// describe what is on disk now.
    /// </summary>
    public void OnWindowActivated()
    {
        if (State == ShellState.Ready) Play.Refresh();
        RefreshStatusBar();
    }

    public void RefreshStatusBar()
    {
        OnPropertyChanged(nameof(ActiveProfileText));
        OnPropertyChanged(nameof(ActiveSaveSetText));
        OnPropertyChanged(nameof(GuardBrush));
        OnPropertyChanged(nameof(GuardText));
        OnPropertyChanged(nameof(ActiveBuildText));
        OnPropertyChanged(nameof(LaunchModeText));
        OnPropertyChanged(nameof(IsIsaacRunning));
        OnPropertyChanged(nameof(GameRunningText));
        OnPropertyChanged(nameof(LaunchButtonHint));
    }

    public void SaveConfig()
    {
        if (Config is not null) _store.Save(Config);
    }

    /// <summary>Start the game without the guard. Only the Play screen calls this, after running it.</summary>
    public void LaunchUnguarded()
    {
        if (Config is null) return;

        try
        {
            var plan = Launcher.Resolve(Config);
            Launcher.Launch(Config);
            Report($"Launching — {plan.Summary}");
        }
        catch (Exception ex)
        {
            Report(ex.Message);
            System.Windows.MessageBox.Show(ex.Message, "Launch Isaac",
                                           System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void LocateConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate isaac-profiles.json",
            Filter = "Isaac profile config|isaac-profiles.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        _store.UseConfigAt(dialog.FileName);
        Reload();
        Report($"Using {Path.GetFileName(dialog.FileName)}");
    }

    public void Dispose() => _watcher.Dispose();
}
