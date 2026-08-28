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
