using System.Windows;
using System.Windows.Controls;

namespace IsaacProfileManager.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    /// <summary>
    /// Drop the overflow menu open on a left click.
    ///
    /// A ContextMenu is the right container for it — it inherits the window's
    /// styling and handles placement and dismissal — but it only opens itself on
    /// right click, which nobody would find on a button marked "...".
    /// </summary>
    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.DataContext = button.DataContext;
        button.ContextMenu.IsOpen = true;
    }
}
