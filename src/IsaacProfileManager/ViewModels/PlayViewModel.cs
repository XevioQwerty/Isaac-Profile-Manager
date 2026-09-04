using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Views;

namespace IsaacProfileManager.ViewModels;

/// <summary>One row of the pre-flight check, with its fix button when it has one.</summary>
public sealed class GuardFindingViewModel
{
    public required GuardFinding Finding { get; init; }
    public required RelayCommand FixCommand { get; init; }

    public string SeverityText => Finding.SeverityText;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public bool HasFix => Finding.Fix != GuardFix.None;

    public string FixText => Finding.Fix switch
    {
        GuardFix.SwitchProfile => $"Switch to {Finding.FixTarget}",
        GuardFix.SwitchBuild => $"Set launcher to {Finding.FixTarget}",
        _ => string.Empty,
    };

    public Brush SeverityBrush => PlayViewModel.BrushFor(Finding.Severity);
}

/// <summary>
/// The screen that answers the only question the app exists to answer: what
/// happens when I press Launch?
///
/// Profile, save set, build and game version are resolved from disk — never
/// from remembered config — and run through the launch guard. The dangerous
/// half of a mismatch disables the button; the annoying half is shown with the
/// fix beside it. The same screen picks up the run you just played when the
/// game closes, which is what stops progress being stranded in the live folder.
/// </summary>
public sealed class PlayViewModel : ObservableObject
{
    public const string ExitOff = "Off";
    public const string ExitAsk = "Ask";
    public const string ExitAutomatic = "Automatic";

    private readonly MainViewModel _shell;
    private string? _chosenSaveSet;
    private string _lastSessionText = string.Empty;
    private bool _settling;

    public PlayViewModel(MainViewModel shell)
    {
        _shell = shell;

        LaunchCommand = new RelayCommand(Launch, () => _shell.Config is not null && Verdict.CanLaunch && !_shell.IsIsaacRunning);
        PlaySetCommand = new RelayCommand(PlaySet, () => _shell.Config is not null && ChosenSaveSet is not null && !_shell.IsIsaacRunning);
        RefreshCommand = new RelayCommand(() => { Refresh(); _shell.RefreshStatusBar(); });
        CaptureNowCommand = new RelayCommand(() => CaptureAfterSession(ask: true, manual: true),
                                             () => _shell.Config is not null && !_shell.IsIsaacRunning);
    }

    public RelayCommand LaunchCommand { get; }
    public RelayCommand PlaySetCommand { get; }
    public RelayCommand RefreshCommand { get; }

    /// <summary>The exit-capture flow, on demand — for a session that ended while the app was closed.</summary>
    public RelayCommand CaptureNowCommand { get; }

    public static Brush BrushFor(GuardSeverity? severity)
    {
        var key = severity switch
        {
            GuardSeverity.Block => "Accent",
            GuardSeverity.Recommend => "WarnBrush",
            GuardSeverity.Warn => "TextDim",
            _ => "Good",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    // --- The pre-flight card --------------------------------------------------

    public LiveIdentity? Identity { get; private set; }
    public LaunchVerdict Verdict { get; private set; } = LaunchVerdict.Clean;
    public ObservableCollection<GuardFindingViewModel> Findings { get; } = new();

    public string? ActiveProfile { get; private set; }
    public string ProfileText { get; private set; } = "—";
    public string ProfileDetail { get; private set; } = string.Empty;
    public string SaveSetText { get; private set; } = "—";
    public string SaveSetDetail { get; private set; } = string.Empty;
    public string BuildText { get; private set; } = "—";
    public string BuildDetail { get; private set; } = string.Empty;
    public string VersionText { get; private set; } = "—";
    public string VersionDetail { get; private set; } = string.Empty;

    public bool HasFindings => Findings.Count > 0;
    public bool CanLaunch => Verdict.CanLaunch;

    public string VerdictText => Verdict.Worst switch
    {
        GuardSeverity.Block => "Launch is blocked until the finding above is fixed.",
        GuardSeverity.Recommend => "Ready, with a recommendation.",
        GuardSeverity.Warn => "Ready.",
        _ => "Ready. Everything matches.",
    };

    public Brush VerdictBrush => BrushFor(Verdict.Worst);

    /// <summary>Sits under the Launch button so it is obvious what pressing it does.</summary>
    public string LaunchHint => _shell.LaunchButtonHint;

    // --- Quick patches, for the active profile's build ----------------------

    public ObservableCollection<PatchSlotViewModel> QuickPatches { get; } = new();
    public bool HasQuickPatches => QuickPatches.Count > 0;
    public string QuickPatchHeader { get; private set; } = string.Empty;
    public RelayCommand TogglePatchCommand => _shell.BuildVariants.TogglePatchCommand;

    // --- Save-set-led launch --------------------------------------------------

    public ObservableCollection<string> SaveSetChoices { get; } = new();

    public string? ChosenSaveSet
    {
        get => _chosenSaveSet;
        set => SetField(ref _chosenSaveSet, value);
    }

    // --- After the game closes -----------------------------------------------

    public IReadOnlyList<string> ExitCaptureChoices { get; } = new[] { ExitOff, ExitAsk, ExitAutomatic };

    public string ExitCaptureMode
    {
        get => Normalise(_shell.Config?.ExitCapture);
        set
        {
            var config = _shell.Config;
            if (config is null) return;
            var mode = Normalise(value);
            if (mode == Normalise(config.ExitCapture)) return;
            config.ExitCapture = mode;
            _shell.SaveConfig();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExitCaptureExplanation));
        }
    }

    public string ExitCaptureExplanation => ExitCaptureMode switch
    {
        ExitOff => "Nothing happens when the game closes. Capture by hand on the Saves screen.",
        ExitAutomatic => "The set that is live is re-captured as soon as the game closes. Saves that match no set are asked about.",
        _ => "When the game closes you are asked whether to capture the run into the set that is live.",
    };

    private static string Normalise(string? value) => value switch
    {
        ExitOff => ExitOff,
        ExitAutomatic => ExitAutomatic,
        _ => ExitAsk,
    };

    public string LastSessionText
    {
        get => _lastSessionText;
        private set => SetField(ref _lastSessionText, value);
    }

    public bool HasLastSession => LastSessionText.Length > 0;

    // --- Sync with your other machine ---------------------------------------
    // The status of the set that is live (or chosen), against the lane store.
    // Checked after every refresh on a background thread; the Play card shows
    // the relation and the button that resolves it. Automatic mode pulls a
    // newer revision by itself before you press Launch and pushes after exit
    // capture, which is the whole "seamless" part.

    private string _syncText = string.Empty;
    private SetSyncStatus? _syncStatus;
    private bool _syncBusy;
    private int _syncGeneration;

    public bool HasSync => _shell.SaveSyncEnabled;

    public string SyncText
    {
        get => _syncText;
        private set => SetField(ref _syncText, value);
    }

    public SetSyncStatus? SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (!SetField(ref _syncStatus, value)) return;
            OnPropertyChanged(nameof(SyncCanPull));
            OnPropertyChanged(nameof(SyncNeedsPush));
            OnPropertyChanged(nameof(SyncBrush));
        }
    }

    public bool SyncBusy
    {
        get => _syncBusy;
        private set => SetField(ref _syncBusy, value);
    }

    public bool SyncCanPull => SyncStatus?.CanPull == true && !SyncBusy;
    public bool SyncNeedsPush => SyncStatus?.NeedsPush == true && !SyncBusy;

    public Brush SyncBrush => SyncStatus?.Relation switch
    {
        SyncRelation.Equal => BrushFor(null),
        SyncRelation.Fork => BrushFor(GuardSeverity.Block),
        null => BrushFor(GuardSeverity.Warn),
        _ => BrushFor(GuardSeverity.Recommend),
    };

    public RelayCommand PullCommand => new(() => _ = PullAsync(), () => SyncCanPull && !_shell.IsIsaacRunning);
    public RelayCommand PushCommand => new(() => _ = PushAsync(), () => SyncNeedsPush && !_shell.IsIsaacRunning);

    private string? SyncTargetSet => Identity?.Set?.Name ?? ChosenSaveSet;

    private async Task RefreshSyncAsync()
    {
        var generation = ++_syncGeneration;
        SyncStatus = null;

        if (!HasSync) { SyncText = string.Empty; OnPropertyChanged(nameof(HasSync)); return; }
        OnPropertyChanged(nameof(HasSync));

        var name = SyncTargetSet;

        SaveSyncService? service;
        try { service = _shell.CreateSaveSyncService(); }
        catch (SaveSyncException ex) { SyncText = ex.Message; return; }
        if (service is null) return;

        SyncText = $"checking {service.Store.Description}…";
        try
        {
            var statuses = await Task.Run(() => service.StatusAsync());
            if (generation != _syncGeneration) return;   // a newer refresh superseded this one

            // The live set's row when it needs anything; otherwise whichever set
            // does — on a fresh machine that is the set that only exists on the
            // other one, which is exactly the row a person needs to see.
            var mine = name is null ? null : statuses.FirstOrDefault(s => string.Equals(s.SetName, name, StringComparison.OrdinalIgnoreCase));
            var attention = statuses.FirstOrDefault(s => s.CanPull) ?? statuses.FirstOrDefault(s => s.NeedsPush);
            var pick = mine is { CanPull: true } or { NeedsPush: true } ? mine : attention ?? mine;

            SyncStatus = pick;
            SyncText = pick is not null ? $"'{pick.SetName}': {pick.Text}"
                     : name is null ? $"nothing on {service.Store.Description} yet"
                     : $"'{name}' is not on {service.Store.Description}";

            if (pick is { CanPull: true, Relation: not SyncRelation.Fork } && _shell.SaveSyncAutomatic && !_shell.IsIsaacRunning)
                await PullAsync(pick, silent: true);
        }
        catch (SaveSyncException ex)
        {
            if (generation == _syncGeneration) SyncText = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == _syncGeneration) SyncText = ex.Message;
        }
    }

    private Task PullAsync() => SyncStatus is null ? Task.CompletedTask : PullAsync(SyncStatus, silent: false);

    /// <summary>
    /// Take the newer revision, then — if that set is the one live — load it
    /// through the usual gates so the live folder matches before Launch.
    /// </summary>
    private async Task PullAsync(SetSyncStatus status, bool silent)
    {
        var config = _shell.Config;
        var service = _shell.CreateSaveSyncService();
        if (config is null || service is null || status.Newest is null || SyncBusy) return;

        var asCopy = status.Relation == SyncRelation.Fork;
        if (asCopy && !silent && MessageBox.Show(
                $"'{status.SetName}' was played here and on {status.Newest.DeviceName} from the same point, so neither revision can replace the other.\n\n" +
                $"Bring the {status.Newest.DeviceName} revision in as a separate set, so you can compare and pick?",
                "Forked save set", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (asCopy && silent) return;

        if (!silent && !asCopy && MessageBox.Show(
                $"Pull '{status.SetName}' revision {status.RemoteRevision} from {status.Newest.DeviceName}?\n\n" +
                "What this machine has is filed into the set's history first." +
                (IsLive(status.SetName) || Identity?.State is LiveSaveState.NoSaves ? " The live saves are then loaded from it, with every check." : ""),
                "Pull from sync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SyncBusy = true;
        try
        {
            var pulled = await Task.Run(() => asCopy
                ? service.PullAsCopyAsync(status.SetName, status.Newest)
                : service.PullAsync(status.SetName, status.Newest));

            var message = $"Pulled '{pulled.Name}' (rev {VectorClock.Revision(pulled.Clock)}) from {status.Newest.DeviceName}.";

            // Load it when it is the live set, or when nothing is live at all —
            // a fresh machine with an empty save folder has nothing to lose.
            var nothingLive = Identity?.State is LiveSaveState.NoSaves;
            if (!asCopy && (IsLive(status.SetName) || nothingLive))
            {
                var sets = _shell.CreateSaveSetService()!;
                var build = pulled.Build == GameBuild.Both ? GameBuild.Unknown : pulled.Build;
                var checks = sets.Check(pulled, build, _shell.Saves.CloudAcknowledged);
                if (checks.CanActivate)
                {
                    sets.ActivateSet(pulled, build, _shell.Saves.CloudAcknowledged);
                    config.ActiveSaveSet = pulled.Name;
                    _shell.SaveConfig();
                    message += " Loaded it as the live saves.";
                }
                else
                {
                    message += " Not loaded: " + string.Join(" ", checks.Blockers);
                }
            }

            _shell.Report(message);
        }
        catch (Exception ex) when (ex is SaveSyncException or UnsafePathException or ConfigSchemaMismatchException or IOException)
        {
            _shell.Report(ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "Pull from sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SyncBusy = false;
        }

        var keep = _shell.StatusMessage;
        _shell.Reload();
        _shell.Report(keep);
    }

    private bool IsLive(string setName) =>
        Identity?.Set is not null && string.Equals(Identity.Set.Name, setName, StringComparison.OrdinalIgnoreCase);

    private async Task PushAsync()
    {
        var name = SyncTargetSet;
        if (name is null) return;
        await PushAsync(name, silent: false);
    }

    private async Task PushAsync(string setName, bool silent)
    {
        SaveSyncService? service;
        try { service = _shell.CreateSaveSyncService(); }
        catch (SaveSyncException ex) { _shell.Report(ex.Message); return; }
        if (service is null || SyncBusy) return;

        SyncBusy = true;
        try
        {
            var manifest = await Task.Run(() => service.PushAsync(setName));
            _shell.Report($"Pushed '{setName}' revision {manifest.Revision} to {service.Store.Description}.");
        }
        catch (Exception ex) when (ex is SaveSyncException or UnsafePathException or IOException)
        {
            _shell.Report(ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "Push to sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SyncBusy = false;
        }

        _ = RefreshSyncAsync();
    }

    /// <summary>After exit capture: send the revision on, so the other machine can pick it up.</summary>
    private void PushAfterCapture(string setName)
    {
        if (!HasSync) return;

        if (!_shell.SaveSyncAutomatic && MessageBox.Show(
                $"Push '{setName}' to your other machine now?", "Push to sync",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _ = PushAsync(setName, silent: _shell.SaveSyncAutomatic);
    }

    // --- Refresh ---------------------------------------------------------------

    public void Refresh()
    {
        Findings.Clear();
        QuickPatches.Clear();
        SaveSetChoices.Clear();

        var config = _shell.Config;
        if (config is null)
        {
            Identity = null;
            Verdict = LaunchVerdict.Clean;
            ProfileText = SaveSetText = BuildText = VersionText = "—";
            ProfileDetail = SaveSetDetail = BuildDetail = VersionDetail = string.Empty;
            RaiseAll();
            return;
        }

        // Profile: the junction, not the config.
        ActiveProfile = _shell.ModProfileService.GetActiveProfileFromDisk(config);
        var profile = ActiveProfile is null ? null
            : _shell.ModProfileService.List(config).FirstOrDefault(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase));
        ProfileText = ActiveProfile ?? "(not linked)";
        ProfileDetail = profile is null ? "mods\\ is not pointing at a profile"
            : $"{profile.ModCount} mods" + (profile.DisabledCount > 0 ? $", {profile.DisabledCount} disabled" : string.Empty);

        // Build: what the launcher will start, and which folder that is.
        var launcherBuild = _shell.LauncherIni.GetLaunchMode() switch
        {
            LaunchMode.Repentogon => GameBuild.Repentogon,
            LaunchMode.Vanilla => GameBuild.Vanilla,
            _ => GameBuild.Unknown,
        };
        BuildText = _shell.LaunchModeText;
        BuildDetail = $"build folder: {_shell.ActiveBuildText}";

        // Version: what the build being launched last ran here. The log's
        // version belongs to whichever build wrote it, so it is filed per build
        // and the launcher's choice picks which one to compare against.
        if (RecordRunVersion(config)) _shell.SaveConfig();
        var launchingName = launcherBuild == GameBuild.Repentogon ? "REPENTOGON" : "vanilla";
        var machineVersion = launcherBuild switch
        {
            GameBuild.Repentogon => config.LastRepentogonVersion,
            GameBuild.Vanilla => config.LastVanillaVersion,
            _ => null,
        };
        VersionText = machineVersion ?? "unknown";
        VersionDetail = launcherBuild == GameBuild.Unknown
            ? "no build selected in the launcher"
            : machineVersion is null
                ? $"run {launchingName} once so log.txt says"
                : $"last {launchingName} run here";

        // Save set: hashed, with the config as a hint.
        var service = _shell.CreateSaveSetService();
        var anySets = false;
        if (service is not null)
        {
            Identity = new SaveIdentityService(service).Identify(config.ActiveSaveSet);
            var sets = service.ListSets();
            anySets = sets.Count > 0;
            foreach (var name in sets) SaveSetChoices.Add(name);
            if (ChosenSaveSet is null || !SaveSetChoices.Contains(ChosenSaveSet))
                ChosenSaveSet = Identity.Set?.Name ?? SaveSetChoices.FirstOrDefault();
        }
        else
        {
            Identity = new LiveIdentity(LiveSaveState.NoSaveFolder, null, Array.Empty<string>(), 0);
        }

        SaveSetText = Identity.HasSet ? Identity.Set!.Name : Identity.Text;
        SaveSetDetail = Identity.State switch
        {
            LiveSaveState.Exact => $"{Identity.Set!.BuildText}, captured {When(Identity.Set.CapturedUtc)}",
            LiveSaveState.Drifted => Identity.Drift.Count > 0
                ? $"played since capture — {Identity.Drift.Count} file(s) changed"
                : "played since capture",
            _ => string.Empty,
        };

        Verdict = LaunchGuardService.Evaluate(new LaunchContext(
            Identity, ActiveProfile, launcherBuild, machineVersion, _shell.IsIsaacRunning, anySets));

        foreach (var finding in Verdict.Findings)
        {
            var captured = finding;
            Findings.Add(new GuardFindingViewModel
            {
                Finding = captured,
                FixCommand = new RelayCommand(() => ApplyFix(captured), () => captured.Fix != GuardFix.None && !_shell.IsIsaacRunning),
            });
        }

        RefreshQuickPatches(config, profile);
        RaiseAll();
        _ = RefreshSyncAsync();
    }

    private static string When(string utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : utc;

    private static LogReaderService.LogRun TryReadRun()
    {
        try { return new LogReaderService().ReadRun(); }
        catch (IOException) { return LogReaderService.LogRun.None; }
        catch (UnauthorizedAccessException) { return LogReaderService.LogRun.None; }
    }

    /// <summary>File the log's version under the build that wrote it. Returns whether the config changed.</summary>
    private static bool RecordRunVersion(AppConfig config)
    {
        var run = TryReadRun();
        if (run.GameVersion is not { Length: > 0 } version) return false;

        var changed = false;
        if (!string.Equals(config.LastGameVersion, version, StringComparison.Ordinal)) { config.LastGameVersion = version; changed = true; }

        switch (run.Build)
        {
            case GameBuild.Repentogon when !string.Equals(config.LastRepentogonVersion, version, StringComparison.Ordinal):
                config.LastRepentogonVersion = version; changed = true; break;
            case GameBuild.Vanilla when !string.Equals(config.LastVanillaVersion, version, StringComparison.Ordinal):
                config.LastVanillaVersion = version; changed = true; break;
        }

        return changed;
    }

    private void RefreshQuickPatches(AppConfig config, ModProfile? profile)
    {
        QuickPatchHeader = string.Empty;
        if (profile is null || string.IsNullOrWhiteSpace(config.SyncRoot)) return;

        var wanted = profile.UseRepentogon && config.PerProfileBuild ? PatchTarget.Repentogon : PatchTarget.GameRoot;
        QuickPatchHeader = wanted == PatchTarget.Repentogon ? "REPENTOGON" : "Retail";

        var engine = new PatchService(_shell.Process, config.SyncRoot);
        foreach (var info in engine.DescribeAll())
        {
            var state = info.States.FirstOrDefault(t => t.Target == wanted);
            if (state is null) continue;
            QuickPatches.Add(new PatchSlotViewModel
            {
                Patch = info.Name,
                DisplayName = info.DisplayName,
                ShortName = info.ShortName,
                State = state,
            });
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Identity), nameof(Verdict), nameof(ProfileText), nameof(ProfileDetail),
                     nameof(SaveSetText), nameof(SaveSetDetail), nameof(BuildText), nameof(BuildDetail),
                     nameof(VersionText), nameof(VersionDetail), nameof(HasFindings), nameof(CanLaunch),
                     nameof(VerdictText), nameof(VerdictBrush), nameof(LaunchHint), nameof(HasQuickPatches),
                     nameof(QuickPatchHeader), nameof(ExitCaptureMode), nameof(ExitCaptureExplanation),
                     nameof(HasLastSession),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // --- Actions ---------------------------------------------------------------

    private void ApplyFix(GuardFinding finding)
    {
        var config = _shell.Config;
        if (config is null || finding.FixTarget is null) return;

        try
        {
            switch (finding.Fix)
            {
                case GuardFix.SwitchProfile:
                    var result = _shell.ModProfileService.Activate(config, finding.FixTarget);
                    _shell.Report($"Active profile: {result.ProfileName} ({result.ModCount} mods)");
                    break;

                case GuardFix.SwitchBuild:
                    var mode = finding.FixTarget.Equals("REPENTOGON", StringComparison.OrdinalIgnoreCase)
                        ? LaunchMode.Repentogon
                        : LaunchMode.Vanilla;
                    if (!_shell.LauncherIni.TrySetLaunchMode(mode))
                        throw new InvalidOperationException("The launcher's ini could not be written. Is REPENTOGON's launcher installed?");
                    _shell.Report($"Launcher set to start {finding.FixTarget}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Fix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _shell.Reload();
    }

    /// <summary>
    /// The Launch button. Blocks are enforced by the command's CanExecute; a
    /// recommendation asks once, with the fix as the default answer.
    /// </summary>
    public void Launch()
    {
        Refresh();
        if (!Verdict.CanLaunch)
        {
            MessageBox.Show(string.Join("\n\n", Verdict.Findings.Where(f => f.Severity == GuardSeverity.Block).Select(f => f.Title + "\n" + f.Detail)),
                            "Launch is blocked", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var recommended = Verdict.Findings.FirstOrDefault(f => f.Severity == GuardSeverity.Recommend);
        if (recommended is not null)
        {
            var fix = Findings.First(f => ReferenceEquals(f.Finding, recommended));
            var answer = MessageBox.Show(
                $"{recommended.Title}\n\n{recommended.Detail}\n\n" +
                (fix.HasFix ? $"Yes: {fix.FixText}, then launch.\nNo: launch anyway.\nCancel: do nothing." : "Launch anyway?"),
                "Before you launch", fix.HasFix ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (fix.HasFix && answer == MessageBoxResult.Yes)
            {
                ApplyFix(recommended);
                Refresh();
                if (!Verdict.CanLaunch) return;
            }
            else if (!fix.HasFix && answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _shell.LaunchUnguarded();
    }

    /// <summary>
    /// Pick a save set and press one thing: the set is loaded, the mod profile
    /// it was made with is activated, the launcher is pointed at its build, and
    /// the game starts. Every gate still runs — they just almost never fire,
    /// because the set chose everything.
    /// </summary>
    private void PlaySet()
    {
        var config = _shell.Config;
        var service = _shell.CreateSaveSetService();
        if (config is null || service is null || ChosenSaveSet is null) return;

        SaveSet? set;
        try { set = service.LoadSet(ChosenSaveSet); }
        catch (ConfigSchemaMismatchException ex) { Fail(ex.Message); return; }
        if (set is null) { Fail($"No save set called '{ChosenSaveSet}'."); return; }

        if (set.Files.Count == 0)
        {
            Fail($"'{set.Name}' has no save in it yet. Launch the game so it writes one, then capture it into the set on the Saves screen.");
            return;
        }

        try
        {
            // 1. Profile.
            if (set.ModProfile.Length > 0 &&
                !string.Equals(set.ModProfile, ActiveProfile, StringComparison.OrdinalIgnoreCase))
            {
                if (!config.Profiles.Contains(set.ModProfile, StringComparer.OrdinalIgnoreCase))
                {
                    if (MessageBox.Show(
                            $"'{set.Name}' was made with the mod profile '{set.ModProfile}', which does not exist here.\n\n" +
                            $"Keep '{ActiveProfile ?? "(none)"}' active and continue?",
                            "Profile missing", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
                else
                {
                    _shell.ModProfileService.Activate(config, set.ModProfile);
                }
            }

            // 2. Build.
            if (set.Build is GameBuild.Repentogon or GameBuild.Vanilla)
            {
                var mode = set.Build == GameBuild.Repentogon ? LaunchMode.Repentogon : LaunchMode.Vanilla;
                if (_shell.LauncherIni.GetLaunchMode() != mode && !_shell.LauncherIni.TrySetLaunchMode(mode))
                    throw new InvalidOperationException("The launcher's ini could not be written, so the build cannot be selected.");
            }

            // 3. Saves — unless they are already exactly this set.
            var identity = new SaveIdentityService(service).Identify(config.ActiveSaveSet);
            var alreadyLive = identity.State == LiveSaveState.Exact &&
                              string.Equals(identity.Set?.Name, set.Name, StringComparison.OrdinalIgnoreCase);
            if (!alreadyLive)
            {
                var checks = service.Check(set, set.Build == GameBuild.Both ? GameBuild.Unknown : set.Build, _shell.Saves.CloudAcknowledged);
                if (!checks.CanActivate)
                {
                    Fail("The save set cannot be loaded:\n\n" + string.Join("\n\n", checks.Blockers));
                    return;
                }

                var drift = identity.State == LiveSaveState.Drifted && identity.Set is not null
                    ? $"\n\nThe live saves are '{identity.Set.Name}', played since it was last captured. " +
                      "That progress is backed up but not captured into its set — capture it first if you want to keep it."
                    : string.Empty;

                if (MessageBox.Show(
                        $"Load '{set.Name}' ({set.BuildText}), switch to '{set.ModProfile}' and launch?\n\n" +
                        "Your current saves are backed up first." + drift,
                        "Play this save set", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                service.ActivateSet(set, set.Build == GameBuild.Both ? GameBuild.Unknown : set.Build, _shell.Saves.CloudAcknowledged);
            }

            config.ActiveSaveSet = set.Name;
            _shell.SaveConfig();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            _shell.Reload();
            return;
        }

        _shell.Reload();
        Refresh();
        if (Verdict.CanLaunch) _shell.LaunchUnguarded();
        else Fail("Loaded, but launch is blocked:\n\n" + string.Join("\n\n", Verdict.Findings.Where(f => f.Severity == GuardSeverity.Block).Select(f => f.Title)));
    }

    private void Fail(string message)
    {
        _shell.Report(message);
        MessageBox.Show(message, "Play", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // --- The session watcher -------------------------------------------------

    public void OnGameStarted()
    {
        _shell.Report("Isaac started.");
        _shell.RefreshStatusBar();
    }

    /// <summary>
    /// The game has just exited. Its save is written on exit, so wait for the
    /// files to settle before reading them — then capture the run you played.
    /// </summary>
    public void OnGameExited()
    {
        if (_settling) return;
        _settling = true;
        _shell.Report("Isaac closed — waiting for the save to settle.");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _settling = false;
            try { CaptureAfterSession(ask: ExitCaptureMode != ExitAutomatic, manual: false); }
            catch (Exception ex) { _shell.Report(ex.Message); }
        };
        timer.Start();
    }

    private void CaptureAfterSession(bool ask, bool manual)
    {
        var config = _shell.Config;
        var service = _shell.CreateSaveSetService();
        if (config is null || service is null) return;

        var reader = new LogReaderService();
        LastSessionText = SummariseSession(reader);
        RecordRunVersion(config);

        var mode = ExitCaptureMode;
        if (mode == ExitOff && !manual)
        {
            _shell.SaveConfig();
            _shell.Reload();
            return;
        }

        var activeProfile = _shell.ModProfileService.GetActiveProfileFromDisk(config) ?? config.ActiveProfile ?? string.Empty;
        var identity = new SaveIdentityService(service).Identify(config.ActiveSaveSet);

        try
        {
            switch (identity.State)
            {
                case LiveSaveState.Exact:
                    _shell.Report(manual ? $"The live saves already match '{identity.Set!.Name}'. Nothing to capture." : "Session over; the saves are unchanged.");
                    break;

                case LiveSaveState.Drifted:
                    var set = identity.Set!;
                    if (ask && MessageBox.Show(
                            $"You played on '{set.Name}'. Capture the run into the set?\n\n" +
                            $"{identity.Drift.Count} file(s) changed. The previous revision is kept in the set's history.",
                            "Capture this session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        break;

                    var filled = service.CaptureInto(set);
                    config.ActiveSaveSet = filled.Name;
                    _shell.Report($"Captured the session into '{filled.Name}' (revision {VectorClock.Revision(filled.Clock)}).");
                    PushAfterCapture(filled.Name);
                    break;

                case LiveSaveState.Unrecognised:
                    if (MessageBox.Show(
                            "The live saves match no save set, so nothing was updated.\n\n" +
                            "Capture them as a new set now?",
                            "Unrecognised saves", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        break;

                    var name = TextPrompt.Ask("New save set", "Name for the set these saves become.",
                                              activeProfile.Length > 0 ? $"{activeProfile} save" : "My save");
                    if (string.IsNullOrWhiteSpace(name)) break;

                    var created = service.Capture(name.Trim(), activeProfile);
                    config.ActiveSaveSet = created.Name;
                    _shell.Report($"Captured '{created.Name}' — {created.BuildText}, {created.Files.Count} files.");
                    PushAfterCapture(created.Name);
                    break;

                default:
                    _shell.Report(identity.Text);
                    break;
            }
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Capture this session", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _shell.SaveConfig();
        var message = _shell.StatusMessage;
        _shell.Reload();
        _shell.Report(message);
    }

    private static string SummariseSession(LogReaderService reader)
    {
        try
        {
            if (!reader.Exists) return string.Empty;
            var lines = reader.Read();
            var summary = reader.Summarise(lines);
            var parts = new List<string>
            {
                $"Game version {summary.GameVersion ?? "unknown"}",
                $"{summary.ModsLoaded} mods loaded",
                $"{summary.Errors} errors, {summary.Asserts} asserts",
            };
            if (LogReaderService.SaveTransport(lines) is { } transport) parts.Add($"saves via {transport}");
            if (summary.HasChecksums) parts.Add("a desync table was written — see Diagnose");
            var when = summary.Written?.ToString("HH:mm") ?? "?";
            return $"Last session ({when}): " + string.Join(" · ", parts) + ".";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
