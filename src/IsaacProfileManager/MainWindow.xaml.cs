using System.Windows;
using System.Windows.Threading;
using IsaacProfileManager.ViewModels;

namespace IsaacProfileManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // In the title bar because that is the one place a user can read it
        // without knowing where to look. "Which build am I running?" was
        // otherwise only answerable from the file's properties dialog.
        Title = $"Isaac Profile Manager {Core.AppPaths.Version}";

        // The junction and the launcher ini can both change behind our back —
        // the PowerShell script writes the same config, and the launcher rewrites
        // its ini on exit. Poll rather than trust what we last wrote.
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _statusTimer.Tick += (_, _) => _viewModel.RefreshStatusBar();
        _statusTimer.Start();

        WindowGeometry.Restore(this);

        Activated += (_, _) => _viewModel.OnWindowActivated();
        Closing += (_, _) => WindowGeometry.Save(this);
        Closed += (_, _) => { _statusTimer.Stop(); _viewModel.Dispose(); };
    }

    /// <summary>The three Mods segments are a tab strip inside a rail item, and refresh the same way.</summary>
    private void OnModsTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ModsTabs)) return;

        _viewModel.SelectedModsSegment = ModsTabs.SelectedIndex;
        _viewModel.RefreshSelectedTab(MainViewModel.ModsTab);
    }

    /// <summary>
    /// Re-read the tab being switched to.
    ///
    /// Every tab reads state that other tabs change — importing on one adds
    /// library entries another lists, and Steam moves underneath all of them.
    /// Leaving each tab showing whatever it saw when the window opened meant
    /// hunting for a Refresh button to see work you had just done.
    /// </summary>
    private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // TabControl bubbles SelectionChanged from any Selector inside a tab —
        // a list box, a combo box — and those must not trigger a full reload.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;

        _viewModel.SelectedTabIndex = Tabs.SelectedIndex;
        _viewModel.RefreshSelectedTab(Tabs.SelectedIndex);
    }
}
