using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;

namespace IsaacProfileManager.ViewModels;

/// <summary>One subscribed Workshop item, as shown in the browser.</summary>
public sealed class WorkshopItemViewModel : ObservableObject
{
    private bool _selected;

    public required WorkshopItem Item { get; init; }
    public required bool InLibrary { get; init; }
    public required string LibraryEntry { get; init; }
    public string? PreviewPath { get; set; }

    public string Name => Item.Name;
    public string Id => Item.Id;
    public string Description => BbCode.Strip(Item.Description);
    public string SizeText => $"{Item.SizeMb} MB";
    public string MaterialisedFolderName => Item.MaterialisedFolderName;
    public string StatusText => InLibrary ? "in library" : "not imported";

    /// <summary>Ticked items are what the import will copy.</summary>
    public bool Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }
}

/// <summary>
/// Browses subscribed Workshop items and imports them into the shared library.
///
/// The point of this screen is that it runs <em>before</em> you unsubscribe:
/// names, descriptions and previews are captured while Steam still resolves the
/// items, and the mod content is copied out of Steam's store into folders it has
/// no claim on.
/// </summary>
public sealed class WorkshopViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    private WorkshopItemViewModel? _selectedItem;
    private string _progressText = string.Empty;
    private string _summaryText = string.Empty;
    private bool _fetchPreviews = true;

    public WorkshopViewModel(MainViewModel shell)
    {
        _shell = shell;

        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => !_shell.IsBusy && Items.Any(i => i.Selected));
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        SelectMissingCommand = new RelayCommand(() => { foreach (var i in Items) i.Selected = !i.InLibrary; });
        OpenLibraryCommand = new RelayCommand(OpenLibrary, () => Library is not null);
        OpenWorkshopPageCommand = new RelayCommand(OpenWorkshopPage, () => SelectedItem is not null);
        // Opened in the Steam client, where Subscribe and Unsubscribe work.
        BrowseWorkshopCommand = new RelayCommand(() => Open(WorkshopService.InSteamClient(WorkshopService.BrowseUrl)));
        OpenSubscriptionsCommand = new RelayCommand(
            () => Open(WorkshopService.InSteamClient(WorkshopService.SubscribedItemsUrl(AccountId)!)),
            () => WorkshopService.SubscribedItemsUrl(AccountId) is not null);
        OpenContentFolderCommand = new RelayCommand(OpenContentFolder, () => ContentRoot is not null);

        UnsubscribeAllCommand = new RelayCommand(async () => await UnsubscribeAllAsync(), () => !_shell.IsBusy);
        AskSteamCommand = new RelayCommand(async () => await AskSteamAsync(), () => !_shell.IsBusy);
    }

    public RelayCommand UnsubscribeAllCommand { get; }
    public RelayCommand AskSteamCommand { get; }

    /// <summary>
    /// Ask Steam directly how many Workshop items this account is subscribed to.
    ///
    /// This list is otherwise read from appworkshop_250900.acf, which records
    /// items Steam has *installed*. A fresh subscription that has not finished
    /// downloading is not in there yet, so "I subscribed and the tab is empty"
    /// has two very different causes and the acf cannot tell them apart.
    /// </summary>
    private async Task AskSteamAsync()
    {
        var gameDir = _shell.Config?.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir)) return;

        var pull = new WorkshopPullService(gameDir);
        if (!pull.IsAvailable)
        {
            MessageBox.Show(WorkshopPullService.NotFoundMessage(), "Ask Steam",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _shell.IsBusy = true;
        IsWorking = true;
        ProgressText = "Asking Steam...";

        try
        {
            var result = await pull.StatusAsync();

            var root = !string.IsNullOrWhiteSpace(_shell.Config!.WorkshopRoot)
                ? _shell.Config.WorkshopRoot
                : WorkshopService.ResolveWorkshopRoot(gameDir);
            var acf = root is null ? "(workshop folder not found)" : new WorkshopService(root).AcfPath ?? "(none)";

            var lines = new List<string>
            {
                $"Steam says this account is subscribed to {result.SubscribedAfter} Isaac Workshop item(s).",
                $"Owns the game: {Describe(result.OwnsApp)}    Signed in: {Describe(result.LoggedOn)}",
                string.Empty,
                $"This tab reads Steam's installed-items record:{Environment.NewLine}{acf}",
                $"It currently lists {Items.Count} item(s).",
            };

            if (result.SubscribedAfter > 0 && Items.Count == 0)
                lines.Add(Environment.NewLine +
                          "Subscribed but not listed means Steam has not finished downloading them yet. " +
                          "Let it finish, then press Refresh.");

            foreach (var error in result.Errors) lines.Add(error);

            MessageBox.Show(string.Join(Environment.NewLine, lines), "Ask Steam",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ask Steam", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ProgressText = string.Empty;
            IsWorking = false;
            _shell.IsBusy = false;
        }
    }

    /// <summary>
    /// Poll until Steam's record stops listing what we just dropped, or we run
    /// out of patience. Bounded, because a stale list is a cosmetic problem and
    /// blocking the UI on Steam would be a real one.
    /// </summary>
    private async Task WaitForAcfAsync(int expectedRemoved)
    {
        if (expectedRemoved == 0) return;

        var config = _shell.Config;
        var root = !string.IsNullOrWhiteSpace(config?.WorkshopRoot)
            ? config!.WorkshopRoot
            : WorkshopService.ResolveWorkshopRoot(config?.GameDir);
        if (root is null) return;

        var workshop = new WorkshopService(root);
        var before = workshop.GetSubscribedIds().Count;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500);
            if (workshop.GetSubscribedIds().Count < before) return;
        }
    }

    private static string Describe(bool? flag) => flag switch
    {
        true => "yes",
        false => "NO",
        _ => "unknown",
    };

    /// <summary>
    /// Drop every Workshop subscription for Isaac in one go.
    ///
    /// This is the state the library is designed around: with nothing
    /// subscribed, a game update has nothing to re-lay into whichever profile is
    /// junctioned. Doing it one item at a time through the Steam UI is the chore
    /// this replaces.
    ///
    /// It does not remove anything from a profile. Folders Isaac already
    /// materialised are plain copies that Steam no longer has a claim on —
    /// deleting a user's mods as a side effect of a Steam operation would be a
    /// surprise, so that stays a separate, deliberate action.
    /// </summary>
    private bool _isWorking;

    /// <summary>Drives the progress bar; long Steam operations look like a hang without it.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set => SetField(ref _isWorking, value);
    }

    private async Task UnsubscribeAllAsync()
    {
        var gameDir = _shell.Config?.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            MessageBox.Show("No game directory in the config, so Steam's API cannot be reached.",
                            "Unsubscribe from everything", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pull = new WorkshopPullService(gameDir);
        if (!pull.IsAvailable)
        {
            MessageBox.Show(WorkshopPullService.NotFoundMessage(),
                            "Unsubscribe from everything", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "Unsubscribe from every Isaac Workshop item on this Steam account?" +
            Environment.NewLine + Environment.NewLine +
            "Steam will delete its downloaded copies in steamapps\\workshop. Anything already imported into the " +
            "library is a separate copy and is not touched, and neither are your profiles." +
            Environment.NewLine + Environment.NewLine +
            "Import anything you still want first — after unsubscribing, the Workshop's copy is gone.",
            "Unsubscribe from everything", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _shell.IsBusy = true;
        IsWorking = true;
        ProgressText = "Asking Steam what you are subscribed to...";

        try
        {
            var result = await pull.UnsubscribeAllAsync(new Progress<string>(e => ProgressText = e));

            var message = result.Unsubscribed.Count == 0
                ? "Nothing was subscribed."
                : $"Unsubscribed from {result.Unsubscribed.Count} item(s).";

            if (result.SubscribedAfter > 0)
                message += $" Steam still reports {result.SubscribedAfter} — try again in a moment.";

            foreach (var error in result.Errors) message += $" {error}";

            _shell.Report(message);

            // Steam rewrites appworkshop_250900.acf a moment after the
            // unsubscribe lands. Re-reading immediately shows what was just
            // removed, which reads as the button not having worked.
            ProgressText = "Waiting for Steam to update its record...";
            await WaitForAcfAsync(result.Unsubscribed.Count);

            MessageBox.Show(message, "Unsubscribe from everything", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
        }
        catch (Exception ex)
        {
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Unsubscribe from everything", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ProgressText = string.Empty;
            IsWorking = false;
            _shell.IsBusy = false;
        }
    }

    public RelayCommand BrowseWorkshopCommand { get; }
    public RelayCommand OpenSubscriptionsCommand { get; }
    public RelayCommand OpenContentFolderCommand { get; }

    private string? AccountId => new SteamCloudService().GetStatus().AccountId;

    private string? ContentRoot
    {
        get
        {
            var root = !string.IsNullOrWhiteSpace(_shell.Config?.WorkshopRoot)
                ? _shell.Config!.WorkshopRoot
                : WorkshopService.ResolveWorkshopRoot(_shell.Config?.GameDir);
            return root is null ? null : new WorkshopService(root).ContentRoot;
        }
    }

    private void OpenContentFolder()
    {
        var folder = ContentRoot;
        if (folder is null || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.Report($"Could not open the link: {ex.Message}");
        }
    }

    public ObservableCollection<WorkshopItemViewModel> Items { get; } = new();

    public RelayCommand ImportCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectMissingCommand { get; }
    public RelayCommand OpenLibraryCommand { get; }
    public RelayCommand OpenWorkshopPageCommand { get; }

    private ModLibraryService? Library =>
        string.IsNullOrWhiteSpace(_shell.Config?.SyncRoot)
            ? null
            : new ModLibraryService(_shell.Junctions, _shell.Config!.SyncRoot!);

    public WorkshopItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// False once everything is unsubscribed — which is the intended end state,
    /// not a fault, so it gets its own message rather than an empty list.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    /// <summary>
    /// Most items ship no local thumbnail, so previews come from Steam. This
    /// only works while still subscribed — hence capturing them at import.
    /// </summary>
    public bool FetchPreviews
    {
        get => _fetchPreviews;
        set => SetField(ref _fetchPreviews, value);
    }

    public void Refresh()
    {
        var previouslySelected = SelectedItem?.Id;
        var ticked = Items.Where(i => i.Selected).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        Items.Clear();

        var config = _shell.Config;
        var library = Library;
        if (config is null || library is null)
        {
            IsAvailable = false;
            SummaryText = "Load a config first.";
            OnPropertyChanged(nameof(IsAvailable));
            return;
        }

        var root = !string.IsNullOrWhiteSpace(config.WorkshopRoot)
            ? config.WorkshopRoot
            : WorkshopService.ResolveWorkshopRoot(config.GameDir);

        var workshop = new WorkshopService(root);
        IsAvailable = workshop.IsAvailable;
        OnPropertyChanged(nameof(IsAvailable));

        if (!IsAvailable)
        {
            SummaryText = root is null
                ? "Could not find Steam's workshop folder from the game directory."
                : $"No appworkshop_{WorkshopService.IsaacAppId}.acf under {root}.";
            OnPropertyChanged(nameof(HasItems));
            return;
        }

        var entries = library.ListEntries().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = workshop.GetItems();

        // Steam deletes an item's content the moment you unsubscribe, but takes
        // a little longer to rewrite the acf. In that window the acf still lists
        // ids whose folders are gone, and those have no metadata.xml to read a
        // name from — which is why unsubscribing briefly produced a list of bare
        // numbers reading "0 MB, not imported". They are also the state of a
        // subscription Steam has not finished downloading yet. Neither can be
        // imported, so neither belongs in the list.
        var pending = all.Count(i => !i.ContentPresent);

        foreach (var item in all.Where(i => i.ContentPresent)
                                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = library.ResolveEntryName(item);
            var inLibrary = entries.Contains(entry);

            Items.Add(new WorkshopItemViewModel
            {
                Item = item,
                InLibrary = inLibrary,
                LibraryEntry = entry,
                PreviewPath = library.GetCachedImage(entry) ?? item.LocalImagePath,
                Selected = ticked.Contains(item.Id) || (ticked.Count == 0 && !inLibrary),
            });
        }

        var imported = Items.Count(i => i.InLibrary);
        var withPreview = Items.Count(i => i.PreviewPath is not null);
        SummaryText =
            $"{Items.Count} subscribed  ·  {imported} already in the library  ·  " +
            $"{withPreview} with a preview  ·  {Items.Sum(i => i.Item.SizeMb):N0} MB total";

        if (pending > 0)
            SummaryText += $"  ·  {pending} subscribed but not on disk yet (downloading, or just unsubscribed)";

        SelectedItem = Items.FirstOrDefault(i => i.Id == previouslySelected) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
    }

    private void SetAll(bool selected)
    {
        foreach (var item in Items) item.Selected = selected;
    }

    private async Task ImportAsync()
    {
        var library = Library;
        if (library is null) return;

        var chosen = Items.Where(i => i.Selected).ToList();
        if (chosen.Count == 0) return;

        var totalMb = chosen.Sum(i => i.Item.SizeMb);
        if (MessageBox.Show(
                $"Import {chosen.Count} mod(s) into the library?\n\n" +
                $"About {totalMb:N0} MB will be copied to:\n{library.LibraryRoot}\n\n" +
                "Folders are named without the workshop id, so Steam has no claim on them. " +
                "Nothing Steam owns is modified." +
                (FetchPreviews ? "\n\nPreviews and descriptions are captured now, while you are still subscribed." : ""),
                "Import from Workshop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _shell.IsBusy = true;
        var progress = new Progress<string>(m => ProgressText = m);
        var imported = new List<(string Entry, string Id)>();

        try
        {
            await Task.Run(() =>
            {
                foreach (var vm in chosen)
                {
                    var entry = library.Import(vm.Item, overwrite: false, progress: progress);
                    imported.Add((entry, vm.Item.Id));
                }
            });

            var message = $"Imported {imported.Count} mod(s) into the library.";

            if (FetchPreviews)
            {
                ProgressText = "Fetching previews from Steam...";
                var preview = await new WorkshopPreviewService()
                    .CacheAsync(imported, library.MetadataRoot, progress);

                message += preview.Succeeded
                    ? $" Previews: {preview.Fetched} fetched, {preview.AlreadyCached} already cached, {preview.Unavailable} unavailable."
                    : $" Previews could not be fetched ({preview.Error}) — mods imported fine; retry before you unsubscribe.";
            }

            _shell.Report(message);
            ProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            ProgressText = string.Empty;
            _shell.Report(ex.Message);
            MessageBox.Show(ex.Message, "Import from Workshop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _shell.IsBusy = false;
            var message = _shell.StatusMessage;
            _shell.Reload();
            _shell.Report(message);
        }
    }

    private void OpenLibrary()
    {
        var root = Library?.LibraryRoot;
        if (root is null) return;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private void OpenWorkshopPage()
    {
        if (SelectedItem is null) return;
        Open(WorkshopService.InSteamClient(WorkshopService.ItemUrl(SelectedItem.Id)));
    }
}
