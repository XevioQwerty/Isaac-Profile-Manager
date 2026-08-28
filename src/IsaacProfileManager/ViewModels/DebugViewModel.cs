using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

public sealed class LogLineViewModel
{
    public required LogLine Line { get; init; }

    public int Number => Line.Number;
    public string Text => Line.Text;
    public LogSeverity Severity => Line.Severity;
    public bool IsError => Line.Severity == LogSeverity.Error;
    public bool IsAssert => Line.Severity == LogSeverity.Assert;
    public string SeverityText => Line.Severity switch
    {
        LogSeverity.Error => "ERR",
        LogSeverity.Assert => "ASR",
        _ => "",
    };
}

/// <summary>
/// Reads the game's log so a bad modpack can be diagnosed without opening a
/// 200 KB text file. Read-only throughout — nothing here writes to the log.
/// </summary>
public sealed class DebugViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private LogReaderService _reader = new();
    private readonly LogArchiveService _archive = new();
    private ArchivedLog? _selectedArchive;
    private bool _viewingArchive;

    private string _search = string.Empty;
    private bool _showInfo = true;
    private bool _showAsserts = true;
    private bool _showErrors = true;
    private LogCategory _categoryFilter = LogCategory.None;
    private LogSummary? _summary;
    private string _modComparison = string.Empty;

    public DebugViewModel(MainViewModel shell)
    {
        _shell = shell;

        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = o => o is LogLineViewModel l && Matches(l);

        ReloadCommand = new RelayCommand(Reload);
        OpenLogCommand = new RelayCommand(OpenLog, () => _reader.Exists);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _reader.Exists);
        CopyVisibleCommand = new RelayCommand(CopyVisible, () => Lines.Count > 0);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        ShowErrorsOnlyCommand = new RelayCommand(() => { SetSeverity(false, true, true); CategoryFilter = LogCategory.None; });
        ShowModsCommand = new RelayCommand(() => { SetSeverity(true, true, true); CategoryFilter = LogCategory.ModLoaded; });
        ShowLuaDebugCommand = new RelayCommand(() => { SetSeverity(true, true, true); CategoryFilter = LogCategory.LuaDebug; });
        ShowChecksumsCommand = new RelayCommand(() => { SetSeverity(true, true, true); CategoryFilter = LogCategory.Checksum; });

        ArchiveNowCommand = new RelayCommand(ArchiveNow);
        ViewArchiveCommand = new RelayCommand(ViewArchive, () => SelectedArchive is not null);
        ViewCurrentCommand = new RelayCommand(ViewCurrent, () => _viewingArchive);
    }

    // --- Archived sessions --------------------------------------------------
    // The game truncates log.txt on every launch, so without keeping copies you
    // can never compare a run that worked against the one that broke.

    public ObservableCollection<ArchivedLog> Archives { get; } = new();

    public RelayCommand ArchiveNowCommand { get; }
    public RelayCommand ViewArchiveCommand { get; }
    public RelayCommand ViewCurrentCommand { get; }

    public ArchivedLog? SelectedArchive
    {
        get => _selectedArchive;
        set => SetField(ref _selectedArchive, value);
    }

    public bool ViewingArchive
    {
        get => _viewingArchive;
        private set { if (SetField(ref _viewingArchive, value)) OnPropertyChanged(nameof(SourceText)); }
    }

    public string SourceText => ViewingArchive
        ? $"Viewing archived session: {SelectedArchive?.Label}"
        : "Viewing the current log.";

    private void ArchiveNow()
    {
        var archived = _archive.ArchiveCurrent();
        _shell.Report(archived is null
            ? "Nothing new to archive — the current log matches the newest archive."
            : $"Archived {archived.Label} ({archived.SizeKb:N1} KB).");
        LoadArchives();
    }

    private void ViewArchive()
    {
        if (SelectedArchive is null) return;
        _reader = new LogReaderService(SelectedArchive.Path);
        ViewingArchive = true;
        Reload();
    }

    private void ViewCurrent()
    {
        _reader = new LogReaderService();
        ViewingArchive = false;
        Reload();
    }

    private void LoadArchives()
    {
        var previous = SelectedArchive?.Path;
        Archives.Clear();
        foreach (var log in _archive.List()) Archives.Add(log);
        SelectedArchive = Archives.FirstOrDefault(a => a.Path == previous) ?? Archives.FirstOrDefault();
    }

    public ObservableCollection<LogLineViewModel> Lines { get; } = new();
    public ICollectionView View { get; }

    /// <summary>
    /// The per-player checksum table, with the disagreeing rows marked. This is
    /// the manual procedure from the README done for you: whoever's row differs
    /// is the machine to investigate.
    /// </summary>
    public ObservableCollection<LogReaderService.PlayerChecksum> Checksums { get; } = new();

    public bool HasChecksums => Checksums.Count > 0;

    public string ChecksumVerdict
    {
        get
        {
            if (Checksums.Count == 0) return string.Empty;
            var odd = Checksums.Where(c => c.IsOdd).ToList();
            return odd.Count == 0
                ? "Every player agrees — no desync in this table."
                : $"{string.Join(", ", odd.Select(o => o.Player))} disagrees with the rest. That machine is the one to investigate.";
        }
    }

    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyVisibleCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand ShowErrorsOnlyCommand { get; }
    public RelayCommand ShowModsCommand { get; }
    public RelayCommand ShowLuaDebugCommand { get; }
    public RelayCommand ShowChecksumsCommand { get; }

    public bool Exists => _reader.Exists;
    public string LogPath => _reader.LogPath;

    /// <summary>
    /// Where the app is running from and whether it can see the Steam helper.
    /// On the Debug tab because "the helper is missing" is unfalsifiable from
    /// the outside — this makes it a fact the user can read and send back.
    /// </summary>
    public string HelperDiagnosticText
    {
        get
        {
            var pull = new WorkshopPullService(_shell.Config?.GameDir ?? string.Empty);

            var lines = new List<string>
            {
                $"app folder      {Core.AppPaths.ExecutableDirectory}",
                $"bundle folder   {AppContext.BaseDirectory}",
                $"steam helper    {(pull.IsAvailable ? "found" : "NOT FOUND")}",
            };

            if (pull.IsAvailable)
                lines.Add($"                {pull.HelperPath}");
            else
                lines.AddRange(WorkshopPullService.ProbedPaths().Take(4).Select(path => $"  looked in   {path}"));

            return string.Join(Environment.NewLine, lines);
        }
    }

    public LogSummary? Summary
    {
        get => _summary;
        private set
        {
            _summary = value;
            foreach (var name in new[] { nameof(Summary), nameof(GameVersionText), nameof(CommandLineText),
                                         nameof(CountsText), nameof(WrittenText), nameof(HasProblems) })
                OnPropertyChanged(name);
        }
    }

    public string GameVersionText => Summary?.GameVersion ?? "unknown";

    public string CommandLineText => Summary?.CommandLine is { Length: > 0 } c ? c : "(none)";

    public string CountsText => Summary is null
        ? string.Empty
        : $"{Summary.ModsLoaded} mods loaded  ·  {Summary.Errors} errors  ·  {Summary.Asserts} asserts  ·  {Summary.TotalLines} lines";

    public string WrittenText => Summary?.Written?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public bool HasProblems => Summary is { } s && (s.Errors > 0 || s.Asserts > 0);

    /// <summary>
    /// The log's mod count against the active profile's. A mismatch is usually
    /// Steam having re-materialised a subscribed mod, or a disabled mod.
    /// </summary>
    public string ModComparison
    {
        get => _modComparison;
        private set => SetField(ref _modComparison, value);
    }

    public string Search
    {
        get => _search;
        set { if (SetField(ref _search, value)) View.Refresh(); }
    }

    public bool ShowInfo
    {
        get => _showInfo;
        set { if (SetField(ref _showInfo, value)) View.Refresh(); }
    }

    public bool ShowAsserts
    {
        get => _showAsserts;
        set { if (SetField(ref _showAsserts, value)) View.Refresh(); }
    }

    public bool ShowErrors
    {
        get => _showErrors;
        set { if (SetField(ref _showErrors, value)) View.Refresh(); }
    }

    public LogCategory CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            if (!SetField(ref _categoryFilter, value)) return;
            OnPropertyChanged(nameof(CategoryFilterText));
            View.Refresh();
        }
    }

    public string CategoryFilterText => CategoryFilter == LogCategory.None ? "" : $"showing {CategoryFilter} lines only";

    public string VisibleCountText => $"{View.Cast<object>().Count()} of {Lines.Count} lines";

    private bool Matches(LogLineViewModel line)
    {
        var severityOk = line.Severity switch
        {
            LogSeverity.Error => ShowErrors,
            LogSeverity.Assert => ShowAsserts,
            _ => ShowInfo,
        };
        if (!severityOk) return false;

        if (CategoryFilter != LogCategory.None && !line.Line.Category.HasFlag(CategoryFilter)) return false;

        return Search.Length == 0 || line.Text.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private void SetSeverity(bool info, bool asserts, bool errors)
    {
        _showInfo = info; _showAsserts = asserts; _showErrors = errors;
        OnPropertyChanged(nameof(ShowInfo));
        OnPropertyChanged(nameof(ShowAsserts));
        OnPropertyChanged(nameof(ShowErrors));
        View.Refresh();
    }

    private void ClearFilters()
    {
        _search = string.Empty;
        OnPropertyChanged(nameof(Search));
        CategoryFilter = LogCategory.None;
        SetSeverity(true, true, true);
    }

    public void Refresh()
    {
        // Archive on refresh so a run is captured before the next launch wipes it.
        if (!ViewingArchive)
        {
            try { _archive.ArchiveCurrent(); }
            catch (IOException) { /* an unarchived log is not worth an error */ }
        }

        LoadArchives();
        Reload();
    }

    private void Reload()
    {
        Lines.Clear();

        if (!_reader.Exists)
        {
            Summary = null;
            ModComparison = string.Empty;
            OnPropertyChanged(nameof(Exists));
            return;
        }

        IReadOnlyList<LogLine> lines;
        try
        {
            lines = _reader.Read();
        }
        catch (IOException ex)
        {
            _shell.Report($"Could not read the log: {ex.Message}");
            return;
        }

        foreach (var line in lines) Lines.Add(new LogLineViewModel { Line = line });

        Checksums.Clear();
        foreach (var row in LogReaderService.Checksums(lines)) Checksums.Add(row);

        Summary = _reader.Summarise(lines);
        CompareWithActiveProfile(lines);

        View.Refresh();
        OnPropertyChanged(nameof(Exists));
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(HasChecksums));
        OnPropertyChanged(nameof(ChecksumVerdict));
    }

    private void CompareWithActiveProfile(IReadOnlyList<LogLine> lines)
    {
        var config = _shell.Config;
        if (config is null || Summary is null) { ModComparison = string.Empty; return; }

        var active = _shell.ModProfileService.GetActiveProfileFromDisk(config);
        if (active is null) { ModComparison = string.Empty; return; }

        var profile = _shell.ModProfileService.List(config).FirstOrDefault(p => p.Name == active);
        if (profile is null) { ModComparison = string.Empty; return; }

        var difference = Summary.ModsLoaded - profile.ModCount;
        ModComparison = difference switch
        {
            0 => $"Matches '{active}' ({profile.ModCount} mods).",
            > 0 => $"The log loaded {difference} more mod(s) than '{active}' contains — something was added to the folder.",
            _ => $"The log loaded {-difference} fewer mod(s) than '{active}' contains — some are disabled or failed to load.",
        };
    }

    private void CopyVisible()
    {
        var text = string.Join(Environment.NewLine,
            View.Cast<LogLineViewModel>().Select(l => $"{l.Number,6}  {l.SeverityText,-3} {l.Text}"));

        if (text.Length == 0) return;

        try
        {
            Clipboard.SetText(text);
            _shell.Report($"Copied {View.Cast<object>().Count()} line(s) to the clipboard.");
        }
        catch (Exception ex)
        {
            _shell.Report($"Could not copy: {ex.Message}");
        }
    }

    private void OpenLog()
    {
        if (!_reader.Exists) return;
        Process.Start(new ProcessStartInfo(_reader.LogPath) { UseShellExecute = true });
    }

    private void OpenFolder()
    {
        var folder = Path.GetDirectoryName(_reader.LogPath);
        if (folder is null || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
