using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>One patch as it appears in a target's list.</summary>
public sealed class PatchItemViewModel : ObservableObject
{
    public required PatchInfo Info { get; init; }
    public required IReadOnlyList<PatchDrift> Drift { get; init; }

    public string Name => Info.Name;
    public string Description => Info.Description;
    public string SummaryText => Info.SummaryText;
    public bool IsApplied => Info.IsApplied;

    public string StateText => Info.IsApplied ? "APPLIED" : string.Empty;

    public string AppliedText => Info.AppliedUtc is not null && DateTime.TryParse(Info.AppliedUtc, out var when)
        ? $"applied {when.ToLocalTime():yyyy-MM-dd HH:mm}"
        : string.Empty;

    public bool HasDrift => Drift.Count > 0;

    /// <summary>
    /// Something has written over the patch since it went on — a game update is
    /// the ordinary cause, and it means a plain revert would put an old file
    /// back over a newer one.
    /// </summary>
    public string DriftText => Drift.Count == 0
        ? string.Empty
        : $"{Drift.Count} file(s) changed since this was applied: " +
          string.Join(", ", Drift.Take(4).Select(d => d.Path)) +
          (Drift.Count > 4 ? ", ..." : "");
}

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

        AddPatchCommand = new RelayCommand(AddPatch, () => Patches is not null);
        ApplyPatchCommand = new RelayCommand(p => TogglePatch(p as PatchItemViewModel, apply: true),
                                             p => p is PatchItemViewModel { IsApplied: false });
        RevertPatchCommand = new RelayCommand(p => TogglePatch(p as PatchItemViewModel, apply: false),
                                              p => p is PatchItemViewModel { IsApplied: true });
        RemovePatchCommand = new RelayCommand(p => RemovePatch(p as PatchItemViewModel),
                                              p => p is PatchItemViewModel { IsApplied: false });
        CollapseJunctionCommand = new RelayCommand(CollapseJunction, () => BuildLinkIsJunction);
        OpenPatchesFolderCommand = new RelayCommand(OpenPatchesFolder, () => Patches is not null);
    }

    /// <summary>Patches laid over the retail install.</summary>
    public ObservableCollection<PatchItemViewModel> RootPatches { get; } = new();

    /// <summary>Patches laid over the folder the REPENTOGON launcher loads.</summary>
    public ObservableCollection<PatchItemViewModel> RepentogonPatches { get; } = new();

    public RelayCommand AddPatchCommand { get; }
    public RelayCommand ApplyPatchCommand { get; }
    public RelayCommand RevertPatchCommand { get; }
    public RelayCommand RemovePatchCommand { get; }
    public RelayCommand CollapseJunctionCommand { get; }
    public RelayCommand OpenPatchesFolderCommand { get; }

    public bool HasRootPatches => RootPatches.Count > 0;
    public bool HasRepentogonPatches => RepentogonPatches.Count > 0;
    public bool HasNoPatches => RootPatches.Count == 0 && RepentogonPatches.Count == 0;

    private PatchService? Patches =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new PatchService(_shell.Process, _shell.Config!.SyncRoot!);

    private string TargetDirFor(PatchTarget target) =>
        _shell.Config is null
            ? string.Empty
            : target == PatchTarget.GameRoot
                ? _shell.Config.GameDir ?? string.Empty
                : BuildVariantService.ResolveLinkPath(_shell.Config);

    /// <summary>
    /// The old mechanism still in place: Repentogon\ pointing at a complete
    /// build. Patches lay files over a real folder, so the link has to become
    /// one before they mean anything.
    /// </summary>
    public bool BuildLinkIsJunction => Status?.State is BuildLinkState.Linked or BuildLinkState.LinkedElsewhere;

    public string JunctionMigrationText =>
        Status is null || !BuildLinkIsJunction
            ? string.Empty
            : $"Repentogon\\ is a link to {Status.LinkTarget}. Patches write into a real folder, so this " +
              "has to be turned back into one before you can lay anything over it. The build is copied " +
              "in place and the folder it came from is left exactly where it is.";

    private void RefreshPatches()
    {
        RootPatches.Clear();
        RepentogonPatches.Clear();

        var service = Patches;
        if (service is not null)
        {
            foreach (var info in service.DescribeAll())
            {
                var item = new PatchItemViewModel
                {
                    Info = info,
                    Drift = info.IsApplied ? service.DetectDrift(info.Name) : Array.Empty<PatchDrift>(),
                };
                (info.Target == PatchTarget.GameRoot ? RootPatches : RepentogonPatches).Add(item);
            }
        }

        OnPropertyChanged(nameof(HasRootPatches));
        OnPropertyChanged(nameof(HasRepentogonPatches));
        OnPropertyChanged(nameof(HasNoPatches));
        OnPropertyChanged(nameof(BuildLinkIsJunction));
        OnPropertyChanged(nameof(JunctionMigrationText));
    }

    /// <summary>Register an unzipped folder as a patch, and ask what it is laid over.</summary>
    private void AddPatch()
    {
        var service = Patches;
        if (service is null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the unzipped folder to lay over the game",
        };
        if (dialog.ShowDialog() != true) return;

        var name = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar));

        var answer = MessageBox.Show(
            $"Where does '{name}' go?\n\n" +
            "Yes - over the retail install (the folder with isaac-ng.exe and mods\\).\n" +
            "No - over the REPENTOGON folder the launcher loads.\n\n" +
            "Nothing is applied yet; this only files it away so you can apply it when you want it.",
            "Add a patch", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) return;
        var target = answer == MessageBoxResult.Yes ? PatchTarget.GameRoot : PatchTarget.Repentogon;

        RunPatchOperation(() =>
        {
            var info = service.Install(dialog.FolderName, name, target);
            _shell.Report($"Added '{info.Name}' - {info.SummaryText}, for the {info.TargetText}.");
        });
    }

    private void TogglePatch(PatchItemViewModel? item, bool apply)
    {
        var service = Patches;
        if (service is null || item is null || _shell.Config is null) return;

        var targetDir = TargetDirFor(item.Info.Target);
        if (!Directory.Exists(targetDir))
        {
            MessageBox.Show($"The {item.Info.TargetText} does not exist:\n{targetDir}\n\nCheck the paths below.",
                            "Patches", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (apply)
        {
            if (MessageBox.Show(
                    $"Lay '{item.Name}' over the {item.Info.TargetText}?\n\n" +
                    $"{targetDir}\n\n" +
                    $"{item.SummaryText}. Every file it replaces is copied aside first, and reverting " +
                    "puts them all back. Nothing is deleted.",
                    "Apply patch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            RunPatchOperation(() =>
            {
                var result = service.Apply(item.Name, targetDir);
                _shell.Report("Applied " + result.Summary);
                if (result.Skipped.Count > 0)
                    MessageBox.Show(
                        "Applied, but these were left alone:\n\n" +
                        string.Join("\n", result.Skipped.Select(sk => $"  {sk.Path} - {sk.Reason}")),
                        "Apply patch", MessageBoxButton.OK, MessageBoxImage.Information);
            });
            return;
        }

        // Reverting: drift is the case worth stopping on.
        var force = false;
        if (item.HasDrift)
        {
            var answer = MessageBox.Show(
                $"{item.DriftText}\n\n" +
                "That is usually a game update written over the patch, which makes those files newer " +
                "than the copies kept when it was applied.\n\n" +
                "Yes - put the old files back anyway.\n" +
                "No - leave the changed ones and undo the rest.",
                "Files have changed since", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel) return;
            force = answer == MessageBoxResult.Yes;
        }
        else if (MessageBox.Show(
                     $"Take '{item.Name}' back off the {item.Info.TargetText}?\n\n" +
                     "The files it replaced are restored and the ones it added are removed. " +
                     "The backups are kept either way.",
                     "Revert patch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        RunPatchOperation(() =>
        {
            var result = service.Revert(item.Name, force);
            _shell.Report("Reverted " + result.Summary);
            if (result.Skipped.Count > 0)
                MessageBox.Show(
                    "These were left as they are, so the patch still counts as applied:\n\n" +
                    string.Join("\n", result.Skipped.Select(sk => $"  {sk.Path} - {sk.Reason}")),
                    "Revert patch", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void RemovePatch(PatchItemViewModel? item)
    {
        var service = Patches;
        if (service is null || item is null) return;

        if (MessageBox.Show(
                $"Forget '{item.Name}'?\n\nIts folder is deleted from your patches. The game is not touched.",
                "Remove patch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RunPatchOperation(() =>
        {
            service.Remove(item.Name);
            _shell.Report($"Removed the patch '{item.Name}'.");
        });
    }

    /// <summary>
    /// Turn a Repentogon\ junction back into a real folder by copying what it
    /// points at. The source is left alone: this is the one step that cannot be
    /// undone by the patch journal, so the old build stays until the user is
    /// satisfied and deletes it themselves.
    /// </summary>
    private void CollapseJunction()
    {
        if (_shell.Config is null || Status is null || !BuildLinkIsJunction) return;

        var link = Status.LinkPath;
        var source = Status.LinkTarget;
        if (source is null || !Directory.Exists(source)) return;

        if (MessageBox.Show(
                $"Copy the build at\n{source}\n\ninto\n{link}\n\n" +
                "as a real folder, and remove the link?\n\n" +
                "The folder it points at is left exactly where it is - delete it yourself once you are " +
                "happy. This is about 1 GB and takes a minute.",
                "Turn the link into a folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RunPatchOperation(() =>
        {
            if (_shell.Process.IsIsaacRunning())
                throw new UnsafePathException("Isaac is running. Close it first.");

            // Remove the link before copying, or the copy would follow it back
            // into the source and duplicate the build inside itself.
            _shell.Junctions.RemoveLink(link);
            DirectoryCopier.Copy(source, link, overwrite: false);

            _shell.Report($"Repentogon\\ is a real folder now, copied from {Path.GetFileName(source)}.");
        });
    }

    private void OpenPatchesFolder()
    {
        var service = Patches;
        if (service is null) return;
        Directory.CreateDirectory(service.PatchesRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{service.PatchesRoot}\"") { UseShellExecute = true });
    }

    private void RunPatchOperation(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Patches", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
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
        RefreshPatches();
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
