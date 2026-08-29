using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>
/// Drives the build-folder switcher: the game's <c>Repentogon\</c> is a junction
/// and this re-points it at one of the folders in the build root. Same
/// indirection as the mod profiles, so switching copies nothing.
/// </summary>
public sealed class BuildVariantsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private BuildVariantStatus? _status;
    private string? _selectedVariant;
    private string _progressText = string.Empty;
    private string _gameDirDraft = string.Empty;
    private string _buildRootDraft = string.Empty;
    private string _linkFolderDraft = string.Empty;

    public BuildVariantsViewModel(MainViewModel shell)
    {
        _shell = shell;

        SwitchCommand = new RelayCommand(Switch,
            () => Status is { IsReady: true } && SelectedVariant is not null && SelectedVariant != Status.ActiveVariant);
        InitializeCommand = new RelayCommand(async () => await InitializeAsync(), () => CanInitialize);
        OpenBuildRootCommand = new RelayCommand(OpenBuildRoot, () => Status is not null && Directory.Exists(Status.BuildRoot));
        BrowseGameDirCommand = new RelayCommand(() => Browse("Where the vanilla game is installed", d => GameDirDraft = d));
        BrowseBuildRootCommand = new RelayCommand(() => Browse("Where the build folders live", d => BuildRootDraft = d));
        SavePathsCommand = new RelayCommand(SavePaths, () => _shell.Config is not null);
        ResetPathsCommand = new RelayCommand(LoadPathDrafts, () => _shell.Config is not null);
    }

    public RelayCommand BrowseGameDirCommand { get; }
    public RelayCommand BrowseBuildRootCommand { get; }
    public RelayCommand SavePathsCommand { get; }
    public RelayCommand ResetPathsCommand { get; }

    /// <summary>The vanilla install: the folder holding the retail isaac-ng.exe.</summary>
    public string GameDirDraft
    {
        get => _gameDirDraft;
        set => SetField(ref _gameDirDraft, value);
    }

    /// <summary>Where the complete builds are kept, one subfolder per variant.</summary>
    public string BuildRootDraft
    {
        get => _buildRootDraft;
        set => SetField(ref _buildRootDraft, value);
    }

    /// <summary>
    /// The subfolder of the game directory the launcher loads the downgraded
    /// build from. A bare name is relative to the game directory.
    /// </summary>
    public string LinkFolderDraft
    {
        get => _linkFolderDraft;
        set => SetField(ref _linkFolderDraft, value);
    }

    private void LoadPathDrafts()
    {
        var config = _shell.Config;
        GameDirDraft = config?.GameDir ?? string.Empty;
        BuildRootDraft = config is null ? string.Empty : BuildVariantService.ResolveBuildRoot(config);
        LinkFolderDraft = config?.BuildLinkFolder ?? BuildVariantService.LinkFolderName;
    }

    private static void Browse(string title, Action<string> accept)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true) accept(dialog.FolderName);
    }

    /// <summary>
    /// Write the three paths back to config.
    ///
    /// Deliberately does not move anything: these say where things already are,
    /// and a wrong value should be a link that reads as Absent rather than a
    /// silent relocation of a 1 GB build.
    /// </summary>
    private void SavePaths()
    {
        var config = _shell.Config;
        if (config is null) return;

        var gameDir = GameDirDraft.Trim();
        var buildRoot = BuildRootDraft.Trim();
        var linkFolder = LinkFolderDraft.Trim();

        if (gameDir.Length > 0 && !Directory.Exists(gameDir))
        {
            MessageBox.Show($"No such folder:\n{gameDir}", "Build paths",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (linkFolder.Length == 0) linkFolder = BuildVariantService.LinkFolderName;

        // A build root inside the folder we re-point would be swapped away with
        // the build it is meant to outlive.
        var resolvedLink = Path.IsPathRooted(linkFolder)
            ? linkFolder
            : Path.Combine(gameDir, linkFolder);
        if (buildRoot.Length > 0 && gameDir.Length > 0 &&
            Path.GetFullPath(buildRoot).TrimEnd('\\')
                .StartsWith(Path.GetFullPath(resolvedLink).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "The build root cannot sit inside the folder being re-pointed - switching would " +
                "take the other builds with it.",
                "Build paths", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (gameDir.Length > 0) config.GameDir = gameDir;
        config.BuildRoot = buildRoot.Length == 0 ? null : buildRoot;
        config.BuildLinkFolder =
            string.Equals(linkFolder, BuildVariantService.LinkFolderName, StringComparison.OrdinalIgnoreCase)
                ? null
                : linkFolder;

        _shell.Store.Save(config);
        _shell.Report("Saved the build paths.");
        _shell.Reload();
    }

    public ObservableCollection<string> Variants { get; } = new();

    public RelayCommand SwitchCommand { get; }
    public RelayCommand InitializeCommand { get; }
    public RelayCommand OpenBuildRootCommand { get; }

    public BuildVariantStatus? Status
    {
        get => _status;
        private set
        {
            if (!SetField(ref _status, value)) return;
            OnPropertyChanged(nameof(StateHeadline));
            OnPropertyChanged(nameof(StateDetail));
            OnPropertyChanged(nameof(LinkPathText));
            OnPropertyChanged(nameof(BuildRootText));
            OnPropertyChanged(nameof(CanInitialize));
            OnPropertyChanged(nameof(IsSwitchable));
        }
    }

    public string? SelectedVariant
    {
        get => _selectedVariant;
        set => SetField(ref _selectedVariant, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public string LinkPathText => Status?.LinkPath ?? string.Empty;
    public string BuildRootText => Status?.BuildRoot ?? string.Empty;

    public bool IsSwitchable => Status?.IsReady ?? false;

    /// <summary>First-time setup applies when there is a real build folder, or only one variant to choose from.</summary>
    public bool CanInitialize =>
        Status is not null &&
        (Status.State is BuildLinkState.RealFolder or BuildLinkState.Absent || Status.Variants.Count < 2);

    public string StateHeadline => Status?.State switch
    {
        null => "No install configured",
        BuildLinkState.Linked => $"Active build: {Status.ActiveVariant}",
        BuildLinkState.RealFolder => "Not set up yet",
        BuildLinkState.LinkedElsewhere => "Linked outside the build root",
        BuildLinkState.Absent => "No build folder found",
        _ => "Unknown",
    };

    public string StateDetail => Status?.State switch
    {
        null => "Load a config first.",

        BuildLinkState.Linked when Status.Variants.Count < 2 =>
            "Only one build folder exists. Run first-time setup to create a second one to switch to.",

        BuildLinkState.Linked =>
            "Switching re-points the junction. Nothing is copied, and neither build folder is modified.\n" +
            "The launcher refuses to run Repentogon\\isaac-ng.exe directly, so start the game through REPENTOGONLauncher.exe as usual.",

        BuildLinkState.RealFolder =>
            "Repentogon\\ is a real folder. First-time setup moves it into the build root as 'Vanilla', " +
            "copies it to a second folder named 'OnlineFix', and links Repentogon\\ back at 'Vanilla'.\n" +
            "Both start out identical to what you already have installed — what goes in the second one afterwards is up to you.",

        BuildLinkState.LinkedElsewhere =>
            $"Repentogon\\ points at {Status.LinkTarget}, which is not inside the build root. " +
            "Left alone — point it back inside the build root yourself if you want to switch from here.",

        BuildLinkState.Absent =>
            "There is no Repentogon\\ folder in the game directory. Install REPENTOGON first.",

        _ => string.Empty,
    };

    public void Refresh()
    {
        if (_shell.Config is null)
        {
            Status = null;
            Variants.Clear();
            return;
        }

        var previous = SelectedVariant;
        Status = _shell.BuildVariantService.GetStatus(_shell.Config);

        Variants.Clear();
        foreach (var variant in Status.Variants) Variants.Add(variant);

        SelectedVariant = Variants.Contains(previous ?? string.Empty) ? previous : Status.ActiveVariant;

        LoadPathDrafts();
    }

    private void Switch()
    {
        if (_shell.Config is null || SelectedVariant is null) return;

        try
        {
            _shell.BuildVariantService.Switch(_shell.Config, SelectedVariant);
            _shell.Report($"Build folder now points at '{SelectedVariant}'.");
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Build switcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private async Task InitializeAsync()
    {
        if (_shell.Config is null || Status is null) return;

        if (MessageBox.Show(
                $"Set up build switching?\n\n" +
                $"• Move {Status.LinkPath} into {Status.BuildRoot}\\Vanilla\n" +
                $"• Copy it to {Status.BuildRoot}\\OnlineFix\n" +
                $"• Link {Status.LinkPath} back at Vanilla\n\n" +
                "The copy can take a while and needs room for a second copy of the build.",
                "First-time setup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _shell.IsBusy = true;
        ProgressText = "Working...";
        var progress = new Progress<string>(m => ProgressText = m);

        try
        {
            var config = _shell.Config;
            await Task.Run(() => _shell.BuildVariantService.Initialize(config, progress));
            _shell.Report("Build switching is set up.");
            ProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            ProgressText = string.Empty;
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "First-time setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _shell.IsBusy = false;
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private void OpenBuildRoot()
    {
        if (Status is null || !Directory.Exists(Status.BuildRoot)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Status.BuildRoot}\"") { UseShellExecute = true });
    }
}
