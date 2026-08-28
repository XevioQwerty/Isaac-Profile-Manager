using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Storage;

/// <summary>Thrown when the config exists but this build must not act on it.</summary>
public sealed class ConfigSchemaException : Exception
{
    public ConfigSchemaException(string message) : base(message) { }
}

public interface IConfigStore
{
    string? ConfigPath { get; }
    bool Exists { get; }
    AppConfig Load();
    void Save(AppConfig config);
}

/// <summary>
/// Reads and writes <c>isaac-profiles.json</c> — the same file IsaacProfiles.ps1
/// uses, so the GUI and the script stay in agreement about which profile is
/// active.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    public const string FileName = "isaac-profiles.json";

    /// <summary>Remembers where the config was found when it is not beside the exe.</summary>
    private static string PointerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsaacProfileManager",
        "config-location.txt");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string? ConfigPath { get; private set; }

    public ConfigStore(string? explicitPath = null)
    {
        ConfigPath = explicitPath ?? Discover();
    }

    public bool Exists => ConfigPath is not null && File.Exists(ConfigPath);

    /// <summary>
    /// Look beside the executable, then up the directory tree, then at whatever
    /// location the user pointed us to previously. Walking up is what makes a
    /// debug build under <c>bin\Debug\net8.0-windows\</c> find the config at the
    /// repository root without configuration.
    /// </summary>
    public static string? Discover()
    {
        // Beside the executable first. AppContext.BaseDirectory is the bundle's
        // extraction folder for this single-file build, so starting there walks
        // up out of %TEMP% and never sees a config sitting next to the exe —
        // which is why the pointer file below had to exist at all.
        foreach (var start in AppPaths.ProbeRoots())
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (File.Exists(PointerFile))
        {
            var remembered = File.ReadAllText(PointerFile).Trim();
            if (remembered.Length > 0 && File.Exists(remembered)) return remembered;
        }

        return null;
    }

    /// <summary>Adopt a config the user located by hand, and remember it for next launch.</summary>
    public void UseConfigAt(string path)
    {
        ConfigPath = path;
        var dir = Path.GetDirectoryName(PointerFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(PointerFile, path, new UTF8Encoding(false));
    }

    public AppConfig Load()
    {
        if (ConfigPath is null || !File.Exists(ConfigPath))
            throw new FileNotFoundException("No isaac-profiles.json found. Run first-time setup, or locate an existing file.", ConfigPath ?? FileName);

        // ReadAllText strips a UTF-8 BOM; PowerShell 5.1's Set-Content -Encoding UTF8
        // writes one, and JsonDocument would choke on it.
        var json = File.ReadAllText(ConfigPath);

        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigSchemaException($"{ConfigPath} is not readable as JSON: {ex.Message}");
        }

        if (config is null)
            throw new ConfigSchemaException($"{ConfigPath} is empty.");

        // Refuse rather than guess. A wrong default here once meant launching the
        // wrong build, which is a desync in an online session.
        if (config.ConfigVersion != AppConfig.SupportedConfigVersion)
            throw new ConfigSchemaException(
                $"{ConfigPath} has ConfigVersion {config.ConfigVersion}; this build understands {AppConfig.SupportedConfigVersion}. " +
                "Re-run Setup.bat to regenerate it.");

        return config;
    }

    /// <summary>
    /// Write the config atomically. A torn write here would leave the tool
    /// unable to name the active profile while the junction still points at it.
    /// </summary>
    public void Save(AppConfig config)
    {
        if (ConfigPath is null)
            throw new InvalidOperationException("No config path set.");

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        var directory = Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, FileName + ".tmp");
        File.WriteAllText(temp, json, new UTF8Encoding(false));

        if (File.Exists(ConfigPath))
            File.Replace(temp, ConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temp, ConfigPath);
    }
}
