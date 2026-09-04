using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

public sealed record DeviceIdentity(string Id, string Name)
{
    /// <summary>The first eight characters of the id: enough to tell two machines apart in a folder name.</summary>
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
}

/// <summary>
/// Names this machine, so a save set can say which device captured it and a
/// sync lane can be written by exactly one device.
///
/// The id is a random guid written once into the config and never derived from
/// hardware: a hardware-derived id changes when a disk is cloned to a new
/// laptop, which is precisely the case where two "devices" must be told apart.
/// </summary>
public static class DeviceService
{
    /// <summary>
    /// Make sure the config names this machine. Returns true when it had to be
    /// written, so the caller knows to save the config.
    /// </summary>
    public static bool Ensure(AppConfig config, out DeviceIdentity identity)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(config.DeviceId))
        {
            config.DeviceId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(config.DeviceName))
        {
            config.DeviceName = SafeName(Environment.MachineName);
            changed = true;
        }

        identity = new DeviceIdentity(config.DeviceId!, config.DeviceName!);
        return changed;
    }

    /// <summary>A device name that can be used as part of a folder name.</summary>
    public static string SafeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "device";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
