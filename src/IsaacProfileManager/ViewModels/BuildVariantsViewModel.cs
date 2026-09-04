using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>One patch against one of the two folders it can be laid over.</summary>
public sealed class PatchSlotViewModel : ObservableObject
{
    /// <summary>The folder name, which is what the service is addressed by.</summary>
    public required string Patch { get; init; }

    /// <summary>What to show instead of it.</summary>
    public required string DisplayName { get; init; }

    public required string ShortName { get; init; }

    public required PatchTargetState State { get; init; }

    public PatchTarget Target => State.Target;
    public string Label => State.ShortText;
    public bool IsApplied => State.IsApplied;
    public string ActionText => State.IsApplied ? "Revert" : "Apply";

    public string AppliedText => State.AppliedUtc is not null && DateTime.TryParse(State.AppliedUtc, out var when)
        ? $"on since {when.ToLocalTime():MMM d HH:mm}"
        : "not applied";

    public bool HasDrift => State.DriftCount > 0;

    /// <summary>
    /// Something has written over the patch since it went on — a game update is
    /// the ordinary cause, and it means a plain revert would put an old file
    /// back over a newer one.
    /// </summary>
    public string DriftText => State.DriftCount == 0
        ? string.Empty
        : $"{State.DriftCount} file(s) changed since";
}

/// <summary>Something shipped with the app, waiting to be taken up.</summary>
public sealed class BundledItemViewModel : ObservableObject
{
    public required BundledPatch Item { get; init; }

    public string Name => Item.Name;
    public string Description => Item.Description;
    public bool CanInstall => !Item.AlreadyInstalled;

    public string StateText => Item.AlreadyInstalled ? "already added" : string.Empty;
}

/// <summary>One patch as it appears in the list, with a slot per folder.</summary>
public sealed class PatchItemViewModel : ObservableObject
{
    public required PatchInfo Info { get; init; }
    public required IReadOnlyList<PatchSlotViewModel> Slots { get; init; }

    public string Name => Info.DisplayName;
    public string FolderName => Info.Name;
    public string Description => Info.Description;
    public string SummaryText => Info.SummaryText;
    public bool IsAppliedAnywhere => Info.IsAppliedAnywhere;
    public bool CanRemove => !Info.IsAppliedAnywhere;
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

        AddPatchCommand = new RelayCommand(AddPatch, () => PatchEngine is not null);
        TogglePatchCommand = new RelayCommand(p => ToggleSlot(p as PatchSlotViewModel),
                                              p => p is PatchSlotViewModel);
        RemovePatchCommand = new RelayCommand(p => RemovePatch(p as PatchItemViewModel),
                                              p => p is PatchItemViewModel { CanRemove: true });
        CollapseJunctionCommand = new RelayCommand(CollapseJunction, () => BuildLinkIsJunction);
        OpenPatchesFolderCommand = new RelayCommand(OpenPatchesFolder, () => PatchEngine is not null);
        RenamePatchCommand = new RelayCommand(p => RenamePatch(p as PatchItemViewModel),
                                              p => p is PatchItemViewModel);
        AddBundledCommand = new RelayCommand(p => AddBundled(p as BundledItemViewModel),
                                             p => p is BundledItemViewModel { CanInstall: true });
        RunOnlineToolCommand = new RelayCommand(RunOnlineTool, () => Bundled.OnlineToolPath is not null);
    }

    /// <summary>
    /// Every patch, once. Each row carries a slot per folder rather than the
    /// patch living under one heading: the same fix usually has to go over the
    /// retail install and the REPENTOGON build both, and listing it twice made
    /// that look like two different patches.
    /// </summary>
    public ObservableCollection<PatchItemViewModel> Patches { get; } = new();

    public RelayCommand AddPatchCommand { get; }
    public RelayCommand TogglePatchCommand { get; }
    public RelayCommand RemovePatchCommand { get; }
    public RelayCommand CollapseJunctionCommand { get; }
    public RelayCommand OpenPatchesFolderCommand { get; }
    public RelayCommand RenamePatchCommand { get; }
    public RelayCommand AddBundledCommand { get; }
    public RelayCommand RunOnlineToolCommand { get; }

    /// <summary>Patches shipped with this build that are not in the user's folder yet.</summary>
    public ObservableCollection<BundledItemViewModel> BundledPatches { get; } = new();

    public bool HasBundledPatches => BundledPatches.Count > 0;

    private BundledContentService Bundled { get; } = new();

    public bool HasOnlineTool => Bundled.OnlineToolPath is not null;

    public bool HasPatches => Patches.Count > 0;
    public bool HasNoPatches => Patches.Count == 0;

    private PatchService? PatchEngine =>
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
        Patches.Clear();

        var service = PatchEngine;
        if (service is not null)
        {
            foreach (var info in service.DescribeAll())
            {
                Patches.Add(new PatchItemViewModel
                {
                    Info = info,
                    Slots = info.States
                        .Select(state => new PatchSlotViewModel
                        {
                            Patch = info.Name,
                            DisplayName = info.DisplayName,
                            ShortName = info.ShortName,
                            State = state,
                        })
                        .ToList(),
                });
            }
        }

        BundledPatches.Clear();
        foreach (var item in Bundled.ListPatches(service?.ListPatches() ?? Array.Empty<string>()))
            BundledPatches.Add(new BundledItemViewModel { Item = item });

        OnPropertyChanged(nameof(HasBundledPatches));
        OnPropertyChanged(nameof(HasOnlineTool));
        OnPropertyChanged(nameof(HasPatches));
        OnPropertyChanged(nameof(HasNoPatches));
        OnPropertyChanged(nameof(BuildLinkIsJunction));
        OnPropertyChanged(nameof(JunctionMigrationText));
    }

    /// <summary>Register an unzipped folder as a patch, and ask what it is laid over.</summary>
    private void AddPatch()
    {
        var service = PatchEngine;
        if (service is null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the unzipped folder to lay over the game",
        };
        if (dialog.ShowDialog() != true) return;

        var name = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar));

        RunPatchOperation(() =>
        {
            // No folder is chosen here any more. A patch is filed once and can
            // then be laid over either folder, or both — which is what the same
            // fix usually needs.
            var info = service.Install(dialog.FolderName, name, PatchTarget.GameRoot);
            _shell.Report($"Added '{info.Name}' - {info.SummaryText}. Apply it to either folder below.");
        });
    }

    private void ToggleSlot(PatchSlotViewModel? slot)
    {
        var service = PatchEngine;
        if (service is null || slot is null || _shell.Config is null) return;

        var targetDir = TargetDirFor(slot.Target);
        var where = slot.State.TargetText;

        if (!Directory.Exists(targetDir))
        {
            MessageBox.Show($"The {where} does not exist:\n{targetDir}\n\nCheck the paths below.",
                            "Patches", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!slot.IsApplied)
        {
            if (MessageBox.Show(
                    $"Lay '{slot.DisplayName}' over the {where}?\n\n{targetDir}\n\n" +
                    "Every file it replaces is copied aside first, and reverting puts them all back. " +
                    "Nothing is deleted.",
                    "Apply patch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            RunPatchOperation(() =>
            {
                var result = service.Apply(slot.Patch, slot.Target, targetDir);
                _shell.Report($"Applied to the {where} - " + result.Summary);
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
        if (slot.HasDrift)
        {
            var drifted = service.DetectDrift(slot.Patch, slot.Target);
            var names = string.Join("\n", drifted.Take(8).Select(d => "  " + d.Path)) +
                        (drifted.Count > 8 ? "\n  ..." : "");

            var answer = MessageBox.Show(
                $"{drifted.Count} file(s) in the {where} have changed since '{slot.DisplayName}' was applied:"
                + "\n\n" + names + "\n\n" +
                "Settings files the game rewrites are already left alone. Anything still listed here is " +
                "something else writing over the patch - usually a game update, which makes those files " +
                "newer than the copies kept when it went on." + "\n\n" +
                "Yes - put the old files back, and leave these ones alone from now on." + "\n" +
                "No - leave the changed ones and undo the rest.",
                "Files have changed since", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel) return;
            force = answer == MessageBoxResult.Yes;

            // Remember only the settings files, and only ones still present. A
            // missing file is not "expected to change", and a dll that moved is
            // exactly what the drift check exists to catch.
            if (force)
                service.MarkVolatile(slot.Patch, drifted
                    .Where(d => d.Actual != "missing" && PatchManifest.CanLearnAsVolatile(d.Path))
                    .Select(d => d.Path));
        }
        else if (MessageBox.Show(
                     $"Take '{slot.DisplayName}' back off the {where}?\n\n" +
                     "The files it replaced are restored and the ones it added are removed. " +
                     "The backups are kept either way.",
                     "Revert patch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        RunPatchOperation(() =>
        {
            var result = service.Revert(slot.Patch, slot.Target, force);
            _shell.Report($"Reverted from the {where} - " + result.Summary);
            if (result.Skipped.Count > 0)
                MessageBox.Show(
                    "These were left as they are, so the patch still counts as applied:\n\n" +
                    string.Join("\n", result.Skipped.Select(sk => $"  {sk.Path} - {sk.Reason}")),
                    "Revert patch", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void RemovePatch(PatchItemViewModel? item)
    {
        var service = PatchEngine;
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

    /// <summary>Let a release's packaging name be replaced with something readable.</summary>
    private void RenamePatch(PatchItemViewModel? item)
    {
        var service = PatchEngine;
        if (service is null || item is null) return;

        var entered = Views.TextPrompt.Ask(
            "Name this patch",
            "Shown wherever this patch appears. The folder on disk keeps its own name.",
            item.Name);

        if (entered is null || entered.Trim().Length == 0) return;

        RunPatchOperation(() =>
        {
            service.SetDisplayName(item.FolderName, entered);
            _shell.Report($"Renamed to '{entered.Trim()}'.");
        });
    }

    /// <summary>Copy a bundled patch into the user's own folder, where it behaves like any other.</summary>
    private void AddBundled(BundledItemViewModel? item)
    {
        var service = PatchEngine;
        if (service is null || item is null) return;

        RunPatchOperation(() =>
        {
            Bundled.Install(item.Name, service);
            _shell.Report($"Added '{item.Name}' to your patches. Switch it on below when you want it.");
        });
    }

    /// <summary>
    /// Run the bundled modded-online patcher.
    ///
    /// It edits isaac-ng.exe itself rather than laying files over the folder, so
    /// it is a tool we launch rather than a patch we apply — and because it is
    /// outside the journal, the backup taken here is the only copy this app can
    /// promise. It keeps its own .bak beside the exe as well.
    /// </summary>
    private void RunOnlineTool()
    {
        var tool = Bundled.OnlineToolPath;
        var gameDir = _shell.Config?.GameDir;
        if (tool is null || string.IsNullOrWhiteSpace(gameDir)) return;

        var exe = Path.Combine(gameDir, "isaac-ng.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show($"No isaac-ng.exe at:\n{gameDir}\n\nCheck the paths below.",
                            "Modded online", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                "Open the modded-online patcher?\n\n" +
                "It edits the game executable so mods can be used in online play. It is a separate " +
                "tool that came with this app, not part of it, and it has its own window - this app " +
                "cannot undo what it does from the patch list.\n\n" +
                "Your isaac-ng.exe is copied aside first, so there is a copy to go back to.",
                "Modded online", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RunPatchOperation(() =>
        {
            if (_shell.Process.IsIsaacRunning())
                throw new UnsafePathException("Isaac is running. Close it before patching the executable.");

            var backupDir = Path.Combine(PatchEngine!.BackupRoot,
                                         $"isaac-ng-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);
            File.Copy(exe, Path.Combine(backupDir, "isaac-ng.exe"), overwrite: false);

            Process.Start(new ProcessStartInfo(tool)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(tool),
            });

            _shell.Report($"Backed up isaac-ng.exe to {Path.GetFileName(backupDir)} and opened the patcher.");
        });
    }

    private void OpenPatchesFolder()
    {
        var service = PatchEngine;
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
            OnPropertyChanged(nameof(UsesBuildFolders));
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

    /// <summary>
    /// Build-folder switching is only worth a card on an install that already
    /// uses it. Patches cover the case it was built for, so a plain install —
    /// a real Repentogon\ folder — shows nothing about it rather than an
    /// invitation to set it up.
    /// </summary>
    public bool UsesBuildFolders => Status?.State is BuildLinkState.Linked or BuildLinkState.LinkedElsewhere;

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
