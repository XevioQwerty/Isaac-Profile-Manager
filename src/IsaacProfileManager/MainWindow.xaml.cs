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
