using System.IO;
using System.Net.Http;
using System.Windows;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.Views;

/// <summary>
/// Paste a share code or a Steam collection id, see exactly what importing it
/// would do, then do it.
///
/// The plan is shown before anything is fetched deliberately. An import
/// subscribes on the user's Steam account and replaces folders in their library,
/// so "here is what will happen" has to come first.
/// </summary>
public partial class ShareImportWindow : Window
{
    private readonly ModLibraryService _library;
    private readonly IWorkshopPullService _pull;
    private readonly IGameProcessService _process;

    private SharedProfile? _share;
    private SharePlan? _plan;

    public ShareImportWindow(ModLibraryService library, IWorkshopPullService pull, IGameProcessService process)
    {
        InitializeComponent();
        _library = library;
        _pull = pull;
        _process = process;
    }

    /// <summary>Set when an import actually changed something, so the caller can refresh.</summary>
    public bool Changed { get; private set; }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) CodeBox.Text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process had the clipboard open. Not worth a dialog.
        }
    }

    /// <summary>
    /// Load a profile file, the same thing "Export this profile" writes.
    ///
    /// Files made before share codes existed carry no Workshop ids, so nothing
    /// can be downloaded from them — which is exactly the trap the old
    /// Mod-profiles import fell into, silently building an empty profile. That
    /// case is called out rather than left to be discovered.
    /// </summary>
    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a profile someone sent you",
            Filter = "Isaac profile export|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Load(LibraryHashService.ReadExport(dialog.FileName));

            if (_share is { IsFetchable: false } && _plan is { ToFetch.Count: 0 } && _plan.Items.Count > 0)
                MessageBox.Show(
                    "That file lists mods but carries no Workshop ids, so nothing can be downloaded from it. " +
                    "It was made by an older version." + Environment.NewLine + Environment.NewLine +
                    "Ask whoever sent it to send a share code instead, from the Library tab.",
                    "Nothing to download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is ConfigSchemaMismatchException or IOException)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Show a share and what importing it would do.</summary>
    private void Load(SharedProfile share)
    {
        _share = share;

        var runner = new ShareImportRunner(_library, _pull, _process);
        _plan = runner.Plan(share);

        PlanList.ItemsSource = _plan.Items;
        StatusText.Text = _plan.Summary;

        if (ProfileNameBox.Text.Length == 0) ProfileNameBox.Text = share.Name;
        ImportButton.IsEnabled = true;
        SubscribeButton.IsEnabled = _plan.ToFetch.Count > 0;
    }

    private async void OnRead(object sender, RoutedEventArgs e)
    {
        var input = CodeBox.Text?.Trim() ?? string.Empty;
        if (input.Length == 0) return;

        ReadButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        PlanList.ItemsSource = null;
        StatusText.Text = "Reading...";

        try
        {
            Load(input.StartsWith(ShareCodeService.Prefix, StringComparison.OrdinalIgnoreCase)
                ? ShareCodeService.Decode(input)
                : await FromCollectionAsync(input));
        }
        catch (ShareCodeException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Could not reach Steam: {ex.Message}";
        }
        finally
        {
            ReadButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// A collection gives ids and nothing else, so the entry names have to be
    /// invented locally. The id is appended because two mods can share a
    /// display name, and a collision would silently merge them into one folder.
    /// </summary>
    private async Task<SharedProfile> FromCollectionAsync(string input)
    {
        var id = WorkshopCollectionService.ParseId(input);
        if (id is null)
            throw new ShareCodeException(
                $"That is neither a share code (they start with {ShareCodeService.Prefix}) nor a Steam collection id or link.");

        StatusText.Text = "Asking Steam about that collection...";

        var service = new WorkshopCollectionService();
        var children = await service.GetChildIdsAsync(id);

        if (children.Count == 0)
            throw new ShareCodeException("Steam says that collection is empty.");

        var share = new SharedProfile
        {
            Name = $"collection-{id}",
            Notes = $"Imported from Steam collection {id}. No hashes — a collection cannot carry them.",
            ExportedUtc = DateTime.UtcNow.ToString("o"),
        };

        foreach (var child in children)
        {
            var entry = $"workshop_{child}";
            share.Mods.Add(entry);
            share.WorkshopIds[entry] = child;
        }

        return share;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_share is null || _plan is null) return;

        var profileName = BuildProfileBox.IsChecked == true ? ProfileNameBox.Text.Trim() : null;

        if (BuildProfileBox.IsChecked == true && string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show("Give the profile a name, or untick the box to only fill the library.",
                            "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"{_plan.Summary}." + Environment.NewLine + Environment.NewLine +
            (_plan.ToFetch.Count > 0
                ? "The missing mods will be resubscribed on your Steam account, downloaded, then unsubscribed again."
                : "Nothing needs downloading — everything is already in your library.") +
            (profileName is null ? "" : Environment.NewLine + Environment.NewLine + $"A profile called '{profileName}' will be written and built.") +
            Environment.NewLine + Environment.NewLine +
            "Anything replaced is moved to .backup first.",
            "Import a shared mod set", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        ImportButton.IsEnabled = false;
        ReadButton.IsEnabled = false;
        CloseButton.IsEnabled = false;

        try
        {
            var runner = new ShareImportRunner(_library, _pull, _process);
            var progress = new Progress<string>(m => ProgressText.Text = m);
            var report = await runner.RunAsync(_share, _plan, profileName, progress);

            Changed = report.AnythingChanged || report.ProfileWritten is not null;

            var lines = new List<string>
            {
                report.Installed.Count > 0 ? $"Downloaded {report.Installed.Count} mod(s)." : "Nothing needed downloading.",
            };

            if (report.ProfileWritten is not null) lines.Add($"Built the profile '{report.ProfileWritten}'.");
            if (report.Failed.Count > 0) lines.Add($"{report.Failed.Count} failed: {string.Join("; ", report.Failed)}");
            if (report.HashMismatches.Count > 0)
                lines.Add($"These do not match the sender byte for byte: {string.Join(", ", report.HashMismatches)}");
            lines.AddRange(report.Warnings);

            ProgressText.Text = string.Empty;
            StatusText.Text = lines[0];

            MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, lines),
                            "Import finished", MessageBoxButton.OK,
                            report.Failed.Count > 0 || report.HashMismatches.Count > 0
                                ? MessageBoxImage.Warning
                                : MessageBoxImage.Information);

            // Re-plan so the list reflects what is now on disk rather than what
            // was true before the download.
            _plan = runner.Plan(_share);
            PlanList.ItemsSource = _plan.Items;
            StatusText.Text = _plan.Summary;
        }
        catch (Exception ex)
        {
            ProgressText.Text = string.Empty;
            MessageBox.Show(ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ReadButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Subscribe to what is missing and stop there.
    ///
    /// The escape hatch for when the full cycle will not fetch: this leaves the
    /// subscriptions in place so Steam downloads them the ordinary way, and the
    /// Workshop tab can then import them into the library by hand. It is not the
    /// default because a live subscription is exactly what re-lays mods into the
    /// active profile on launch — the state this tool normally keeps you out of.
    /// </summary>
    private async void OnSubscribeOnly(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;

        var ids = _plan.ToFetch.Where(i => i.WorkshopId is not null)
                               .Select(i => i.WorkshopId!)
                               .Distinct(StringComparer.Ordinal)
                               .ToList();

        if (ids.Count == 0)
        {
            MessageBox.Show("Nothing here needs downloading.", "Subscribe",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Subscribe to {ids.Count} mod(s) in Steam and leave them subscribed?" +
            Environment.NewLine + Environment.NewLine +
            "Steam will download them in the background. Nothing is added to your library by this — come back and " +
            "press Import once they have finished, or import them from the Workshop tab." +
            Environment.NewLine + Environment.NewLine +
            "While they stay subscribed, launching the game will copy them into whichever profile is active.",
            "Subscribe in Steam", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        SubscribeButton.IsEnabled = false;
        ImportButton.IsEnabled = false;

        try
        {
            var result = await _pull.SubscribeAsync(ids, new Progress<string>(m => ProgressText.Text = m));

            var lines = new List<string>
            {
                result.Ok
                    ? $"Subscribed to {ids.Count} mod(s). Steam is downloading them now."
                    : "Steam did not accept every subscription.",
                $"Steam now reports {result.SubscribedAfter} subscription(s) for Isaac.",
            };

            if (result.OwnsApp == false)
                lines.Add("This Steam account does not own The Binding of Isaac: Rebirth, which is why Steam will " +
                          "not subscribe. Sign in to the account that owns the game.");

            lines.AddRange(result.Warnings);
            lines.AddRange(result.Errors);

            ProgressText.Text = string.Empty;
            StatusText.Text = lines[0];

            MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, lines), "Subscribe in Steam",
                            MessageBoxButton.OK,
                            result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ProgressText.Text = string.Empty;
            MessageBox.Show(ex.Message, "Subscribe in Steam", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SubscribeButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
