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
            _share = input.StartsWith(ShareCodeService.Prefix, StringComparison.OrdinalIgnoreCase)
                ? ShareCodeService.Decode(input)
                : await FromCollectionAsync(input);

            var runner = new ShareImportRunner(_library, _pull, _process);
            _plan = runner.Plan(_share);

            PlanList.ItemsSource = _plan.Items;
            StatusText.Text = _plan.Summary;

            if (ProfileNameBox.Text.Length == 0) ProfileNameBox.Text = _share.Name;
            ImportButton.IsEnabled = true;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
