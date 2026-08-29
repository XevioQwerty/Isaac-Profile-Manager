using System.Windows;
using System.Windows.Input;

namespace IsaacProfileManager.Views;

/// <summary>
/// A one-line text prompt.
///
/// WPF has no InputBox, and the alternative was a MessageBox that could only
/// ask yes or no — which is why renaming anything meant editing a file by hand.
/// Kept deliberately small: a heading, a sentence, a box, and two buttons.
/// </summary>
public partial class TextPrompt : Window
{
    private TextPrompt() => InitializeComponent();

    /// <summary>Returns the entered text, or null if the prompt was dismissed.</summary>
    public static string? Ask(string heading, string explanation, string initial = "")
    {
        var prompt = new TextPrompt
        {
            Owner = Application.Current?.MainWindow,
        };

        prompt.Heading.Text = heading;
        prompt.Explanation.Text = explanation;
        prompt.Entry.Text = initial;

        // Selected, so typing replaces rather than appends — a rename usually
        // means "call it something else", not "add to what is there".
        prompt.Entry.SelectAll();
        prompt.Loaded += (_, _) => prompt.Entry.Focus();

        return prompt.ShowDialog() == true ? prompt.Entry.Text : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
