using System.IO;
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
/// Owns the services and the loaded config, and holds the status bar every tab
/// reports into. The tabs answer most support questions on their own if this bar
/// is visible: which profile, which build, and whether the game is running.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly JunctionService _junctions = new();
    private readonly GameProcessService _process = new();

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

        RefreshCommand = new RelayCommand(() => Reload());
        LocateConfigCommand = new RelayCommand(LocateConfig);
        LaunchGameCommand = new RelayCommand(LaunchGame, () => Config is not null);

        Reload();
    }

    public LauncherIniService LauncherIni { get; }
    public GameDetectionService Detection { get; }
    public ModProfileService ModProfileService { get; }
    public BuildVariantService BuildVariantService { get; }
    public ConfigStore Store => _store;

    private int _selectedTabIndex;

    /// <summary>
    /// Which tab is showing. Only the shell knows, and the quick patch toggles
    /// belong to the Mod profiles tab alone — they sit in the tab strip beside
    /// the Launch button, which is shared by every tab.
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetField(ref _selectedTabIndex, value)) OnPropertyChanged(nameof(ShowQuickPatches));
        }
    }

    public bool ShowQuickPatches => SelectedTabIndex == 0 && ModProfiles.HasQuickPatches;

    /// <summary>
    /// The profiles tab owns which patches are relevant, but the panel showing
    /// them lives in the tab strip, which is the shell's. Without this the panel
    /// would keep whatever it decided at startup as the selection changed.
    /// </summary>
    public void NotifyQuickPatchesChanged() => OnPropertyChanged(nameof(ShowQuickPatches));

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

    // --- Status bar ---------------------------------------------------------

    public string ActiveProfileText =>
        Config is null ? "—" : ModProfileService.GetActiveProfileFromDisk(Config) ?? "(not linked)";

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
        RefreshStatusBar();
        OnPropertyChanged(nameof(ConfigPathText));
    }

    /// <summary>
    /// Refresh one tab, by its position in the shell. Cheaper than Reload, which
    /// re-reads the config and every tab.
    /// </summary>
    public void RefreshSelectedTab(int index)
    {
        if (State != ShellState.Ready) return;

        switch (index)
        {
            case 0: ModProfiles.Refresh(); break;
            case 1: Library.Refresh(); break;
            case 2: Workshop.Refresh(); break;
            case 3: BuildVariants.Refresh(); break;
            case 4: Saves.Refresh(); break;
            case 5: Debug.Refresh(); break;
            case 6: Settings.Refresh(); break;
        }

        RefreshStatusBar();
    }

    public void RefreshStatusBar()
    {
        OnPropertyChanged(nameof(ActiveProfileText));
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

    private void LaunchGame()
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
}
