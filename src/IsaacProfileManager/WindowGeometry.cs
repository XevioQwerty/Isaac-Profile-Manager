using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace IsaacProfileManager;

/// <summary>
/// Remembers the window's size and position between runs.
///
/// Kept beside the config-location pointer in LocalAppData rather than in
/// <c>isaac-profiles.json</c>: that file is shared with the PowerShell script,
/// is synced between machines in some setups, and describes the mod setup. A
/// window rectangle is neither shared nor portable — a position valid on one
/// machine's monitors is nonsense on another's.
/// </summary>
public sealed class WindowGeometry
{
    [JsonPropertyName("Left")] public double Left { get; set; }
    [JsonPropertyName("Top")] public double Top { get; set; }
    [JsonPropertyName("Width")] public double Width { get; set; }
    [JsonPropertyName("Height")] public double Height { get; set; }
    [JsonPropertyName("Maximized")] public bool Maximized { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsaacProfileManager",
        "window.json");

    /// <summary>
    /// Apply a remembered rectangle, if it still lands on a monitor.
    ///
    /// A saved position is checked against the current virtual screen before it
    /// is used: unplugging a second monitor would otherwise reopen the window
    /// somewhere invisible, with no way to get it back short of deleting a file
    /// the user does not know exists.
    /// </summary>
    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(Path)) return;

            var saved = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(Path));
            if (saved is null || saved.Width < 200 || saved.Height < 200) return;

            var fitsOnScreen =
                saved.Left + saved.Width > SystemParameters.VirtualScreenLeft + 80 &&
                saved.Top + saved.Height > SystemParameters.VirtualScreenTop + 80 &&
                saved.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                saved.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

            window.Width = saved.Width;
            window.Height = saved.Height;

            if (fitsOnScreen)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = saved.Left;
                window.Top = saved.Top;
            }

            if (saved.Maximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception)
        {
            // Geometry is a convenience. A corrupt or unreadable file must never
            // be the reason the app does not start.
        }
    }

    public static void Save(Window window)
    {
        try
        {
            // RestoreBounds is the un-maximized rectangle, so maximising and
            // closing does not save a full-screen size as the restored one.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            var geometry = new WindowGeometry
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized,
            };

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(geometry, new JsonSerializerOptions { WriteIndented = true }),
                              new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Same again: failing to remember a size is not worth an error on exit.
        }
    }
}
