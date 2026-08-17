using System.Diagnostics;

namespace IsaacProfileManager.Core.Services;

public interface IGameProcessService
{
    bool IsIsaacRunning();
}

/// <summary>
/// Detects a running game. Both builds use the same process name, so this
/// answers "is it running", not "which build is running" — read
/// <c>log.txt</c>'s <c>Game Version:</c> line for that.
/// </summary>
public sealed class GameProcessService : IGameProcessService
{
    private static readonly string[] ProcessNames = { "isaac-ng" };

    public bool IsIsaacRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (InvalidOperationException)
            {
                // Process exited between enumeration and inspection; treat as not running.
            }
        }
        return false;
    }
}
