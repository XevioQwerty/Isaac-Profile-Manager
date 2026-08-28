using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;

namespace IsaacProfileManager.ViewModels;

/// <summary>
/// First-run setup, so a fresh download works without running the PowerShell
/// script first. Writes the same ConfigVersion 3 file the script reads, and all
/// the risky work lives in <see cref="SetupService"/>.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private string _isaacExe = string.Empty;
    private string _syncRoot = string.Empty;
    private string _launcherExe = string.Empty;
    private string _firstProfile = "my-mods";
    private bool _copyExistingMods = true;
    private bool _perProfileBuild;
    private bool _ownsOnSteam = true;
    private string _detectionNote = string.Empty;
    private string _progress = string.Empty;

    public SetupViewModel(MainViewModel shell)
    {
        _shell = shell;

        DetectCommand = new RelayCommand(Detect);
        BrowseExeCommand = new RelayCommand(BrowseExe);
        BrowseSyncRootCommand = new RelayCommand(BrowseSyncRoot);
        BrowseLauncherCommand = new RelayCommand(BrowseLauncher);
        RunCommand = new RelayCommand(async () => await RunAsync(), () => Problems.Count == 0 && !_shell.IsBusy);
    }

    public RelayCommand DetectCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand BrowseSyncRootCommand { get; }
    public RelayCommand BrowseLauncherCommand { get; }
    public RelayCommand RunCommand { get; }

    public string IsaacExe
    {
        get => _isaacExe;
        set { if (SetField(ref _isaacExe, value)) Revalidate(); }
    }

    public string SyncRoot
    {
        get => _syncRoot;
        set { if (SetField(ref _syncRoot, value)) Revalidate(); }
    }

    public string LauncherExe
    {
        get => _launcherExe;
        set { if (SetField(ref _launcherExe, value)) Revalidate(); }
    }

    public string FirstProfile
    {
        get => _firstProfile;
        set { if (SetField(ref _firstProfile, value)) Revalidate(); }
    }

    /// <summary>Copy whatever is in mods\ now into the first profile, so nothing is lost.</summary>
    public bool CopyExistingMods
    {
        get => _copyExistingMods;
        set => SetField(ref _copyExistingMods, value);
    }

    public bool PerProfileBuild
    {
        get => _perProfileBuild;
        set => SetField(ref _perProfileBuild, value);
    }

    public bool OwnsOnSteam
    {
        get => _ownsOnSteam;
        set => SetField(ref _ownsOnSteam, value);
    }

    public string DetectionNote
    {
        get => _detectionNote;
        private set => SetField(ref _detectionNote, value);
    }

    public string Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public string ExistingModsNote
    {
        get
        {
            if (IsaacExe.Length == 0) return string.Empty;
            var mods = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(IsaacExe)) ?? "", "mods");
            if (!Directory.Exists(mods)) return "No mods folder yet — it will be created as a link.";

            var junctions = new JunctionService();
            if (junctions.IsJunction(mods))
                return "mods\\ is already a link. It will be re-pointed; whatever it points at is untouched.";

            var count = Directory.GetDirectories(mods).Length;
            return $"{count} mod(s) installed. Your folder is renamed to mods.backup-<timestamp>, never deleted.";
        }
    }

    public IReadOnlyList<string> Problems =>
        IsaacExe.Length == 0 && SyncRoot.Length == 0
            ? new[] { "Start by finding the game." }
            : SetupService.Validate(BuildPlan());

    public string ProblemsText => string.Join("\n", Problems);
    public bool HasProblems => Problems.Count > 0;

    private SetupPlan BuildPlan() => new(
        IsaacExe: IsaacExe,
        SyncRoot: SyncRoot,
        FirstProfile: FirstProfile.Trim(),
        LauncherExe: LauncherExe.Length == 0 ? null : LauncherExe,
        PerProfileBuild: PerProfileBuild,
        OwnsOnSteam: OwnsOnSteam,
        Migration: CopyExistingMods ? MigrationMode.CopyIntoProfile : MigrationMode.None);

    private void Revalidate()
    {
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(ProblemsText));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ExistingModsNote));
    }

    /// <summary>Fill everything in from the machine, so most people press one button.</summary>
    public void Detect()
    {
        var detection = new GameDetectionService(_shell.LauncherIni);
        var install = detection.FindInstall();

        if (install is null)
        {
            DetectionNote = "Could not find the game automatically — point me at isaac-ng.exe.";
            return;
        }

        IsaacExe = install.IsaacExe;
        DetectionNote = $"Found the game via {install.Source}.";

        var launcher = detection.FindRepentogonLauncher(install.GameDir);
        if (launcher is not null)
        {
            LauncherExe = launcher;
            PerProfileBuild = true;
            DetectionNote += " Found REPENTOGONLauncher too.";
        }

        if (SyncRoot.Length == 0)
        {
            // Default beside the drive root, deliberately outside the game folder.
            var root = Path.GetPathRoot(install.GameDir);
            SyncRoot = string.IsNullOrWhiteSpace(root)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IsaacProfiles")
                : Path.Combine(root, "IsaacProfiles");
        }

        Revalidate();
    }

    private void BrowseExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select isaac-ng.exe",
            Filter = "Isaac executable|isaac-ng.exe|All executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) IsaacExe = dialog.FileName;
    }

    private void BrowseSyncRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where your mod profiles live" };
        if (dialog.ShowDialog() == true) SyncRoot = dialog.FolderName;
    }

    private void BrowseLauncher()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select REPENTOGONLauncher.exe (optional)",
            Filter = "REPENTOGON launcher|REPENTOGONLauncher.exe|All executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) LauncherExe = dialog.FileName;
    }

    /// <summary>
    /// Offer to set Steam's launch options as the last step of setup.
    ///
    /// Without the launcher line Steam starts the game directly, so
    /// <c>[Shared] LaunchMode</c> is never read and per-profile build selection
    /// silently does nothing — a setup mistake that presents much later as
    /// "switching the build didn't work". Asking here is the one moment the
    /// user is already thinking about configuration.
    ///
    /// It is an offer, not an automatic write: it edits a file Steam owns, and
    /// Steam has to be closed for the change to survive.
    /// </summary>
    private void OfferLaunchOptions(string? launcherExe)
    {
        if (string.IsNullOrWhiteSpace(launcherExe)) return;

        var service = new Core.Services.SteamLaunchOptionsService();
        if (service.Check(launcherExe).IsCorrect) return;

        var line = Core.Services.SteamLaunchOptionsService.Suggest(launcherExe);

        var answer = MessageBox.Show(
            "Set Steam's launch options for Isaac now?" + Environment.NewLine + Environment.NewLine +
            line + Environment.NewLine + Environment.NewLine +
            "Without this, Steam starts the game directly and the REPENTOGON build never follows the profile." +
            Environment.NewLine + Environment.NewLine +
            "Steam must be closed — it rewrites its config on exit, so a change made while it is running is lost. " +
            "Your existing config is backed up first.",
            "Steam launch options", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var result = service.Apply(launcherExe);

        var message = result.Message;
        if (result.BackupPath is not null)
            message += Environment.NewLine + Environment.NewLine + $"Backed up to:{Environment.NewLine}{result.BackupPath}";
        if (!result.Ok)
            message += Environment.NewLine + Environment.NewLine +
                       "You can do it later from the Settings tab, or paste it into Steam yourself: " +
                       "right-click Isaac, Properties, Launch Options.";

        MessageBox.Show(message, "Steam launch options", MessageBoxButton.OK,
                        result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        _shell.Report(result.Message);
    }

    private bool _importAfterSetup;

    /// <summary>
    /// Offer to start from someone else's profile.
    ///
    /// Asked here because this is the moment a new user has nothing: the
    /// alternative is finishing setup with an empty library and then having to
    /// discover that Import exists. Setup still completes normally first — the
    /// import needs a config to write into.
    /// </summary>
    public bool ImportAfterSetup
    {
        get => _importAfterSetup;
        set => SetField(ref _importAfterSetup, value);
    }

    /// <summary>Open the import dialog once setup has produced a config to import into.</summary>
    private void OpenImport()
    {
        var config = _shell.Config;
        if (config?.SyncRoot is null) return;

        var window = new Views.ShareImportWindow(
            new Core.Services.ModLibraryService(_shell.Junctions, config.SyncRoot),
            new Core.Services.WorkshopPullService(config.GameDir ?? string.Empty),
            _shell.Process,
            name =>
            {
                if (config.Profiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                _shell.ModProfileService.Add(config, name);
            })
        {
            Owner = Application.Current?.MainWindow,
        };

        window.ShowDialog();
        if (window.Changed) _shell.Reload();
    }

    private async Task RunAsync()
    {
        var plan = BuildPlan();

        if (MessageBox.Show(
                $"Set up Isaac Profile Manager?\n\n" +
                $"• Profiles folder: {plan.SyncRoot}\n" +
                $"• First profile: {plan.FirstProfile}\n" +
                $"• {ExistingModsNote}\n\n" +
                "Nothing is deleted at any point.",
                "First-time setup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _shell.IsBusy = true;
        var progress = new Progress<string>(m => Progress = m);

        try
        {
            // Beside the executable, which is where the PowerShell script looks too.
            // Beside the exe, not the bundle's extraction folder.
            var configPath = Path.Combine(Core.AppPaths.ExecutableDirectory, ConfigStore.FileName);
            var result = await Task.Run(() => new SetupService(_shell.Junctions).Run(plan, configPath, progress));

            _shell.Store.UseConfigAt(result.ConfigPath);
            Progress = string.Empty;

            MessageBox.Show(
                "Setup complete.\n\n" + string.Join("\n", result.Notes),
                "Ready", MessageBoxButton.OK, MessageBoxImage.Information);

            _shell.Reload();
            _shell.Report($"Set up '{plan.FirstProfile}' — {result.ModsCopied} mod(s) copied in.");

            OfferLaunchOptions(plan.LauncherExe);

            if (ImportAfterSetup) OpenImport();
        }
        catch (Exception ex)
        {
            Progress = string.Empty;
            MessageBox.Show(ex.Message, "First-time setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            _shell.Report(ex.Message);
        }
        finally
        {
            _shell.IsBusy = false;
        }
    }
}
