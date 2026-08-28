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

        Activated += (_, _) => _viewModel.RefreshStatusBar();
        Closed += (_, _) => _statusTimer.Stop();
    }
}
