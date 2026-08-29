namespace IsaacProfileManager.Core.Services;

/// <summary>Where a live save folder came from, so the UI can say why.</summary>
public enum SaveFolderSource
{
    /// <summary>Nothing could be resolved.</summary>
    None,

    /// <summary>The user pointed at it by hand. Always wins.</summary>
    Configured,

    /// <summary>The game's own savedatapath.txt said so.</summary>
    ReportedByGame,

    /// <summary>Steam's cloud folder for the app, which is right for a normal Steam copy.</summary>
    SteamUserdata,
}

public sealed record SaveFolderResolution(string? Path, SaveFolderSource Source, int SaveFileCount)
{
    public bool Found => Path is not null;

    public string SourceText => Source switch
    {
        SaveFolderSource.Configured => "set by you",
        SaveFolderSource.ReportedByGame => "reported by the game in savedatapath.txt",
        SaveFolderSource.SteamUserdata => "Steam's cloud folder for Isaac",
        _ => "not found",
    };
}

/// <summary>
/// Works out which folder the game actually reads and writes its saves in.
///
/// Steam's <c>userdata\&lt;id&gt;\250900\remote\</c> is right for a copy running
/// against the real Steam client, and was the only thing this app looked at.
/// It is wrong for anything running a Steam DRM emulator: the emulated API
/// never touches Steam's folder, so the app watched a directory the game did
/// not use — clearing it did nothing, a new save set could not be filled, and
/// "start fresh" appeared to load the old save because the real one was
/// somewhere else the whole time.
///
/// The game writes <c>savedatapath.txt</c> in its own folder on every start,
/// naming the path it settled on. That file is documented as informational —
/// changing it has no effect — but <em>reading</em> it is exactly the signal
/// needed here, and it costs nothing to ask the game where it went.
/// </summary>
public sealed class SaveLocationService
{
    public const string PathFileName = "savedatapath.txt";

    /// <summary>The line savedatapath.txt uses. Matched case-insensitively.</summary>
    private const string PathMarker = "Save Data Path:";

    private readonly SteamCloudService _cloud;

    public SaveLocationService(SteamCloudService cloud) => _cloud = cloud;

    /// <summary>
    /// Resolve the live save folder.
    ///
    /// A configured path wins outright. Otherwise whichever of the two
    /// candidates actually holds save files is preferred, because that is the
    /// only evidence that distinguishes them — and on a fresh profile neither
    /// does, in which case the game's own report is the better guess than a
    /// Steam folder that may belong to a copy running without Steam.
    /// </summary>
    public SaveFolderResolution Resolve(string? configuredPath, string? gameDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            return new SaveFolderResolution(configuredPath, SaveFolderSource.Configured, CountSaves(configuredPath));

        var reported = ReadReportedPath(gameDir);
        var steam = _cloud.GetStatus().RemoteDir;

        var candidates = new List<(string Path, SaveFolderSource Source)>();
        if (reported is not null) candidates.Add((reported, SaveFolderSource.ReportedByGame));
        if (steam is not null) candidates.Add((steam, SaveFolderSource.SteamUserdata));

        if (candidates.Count == 0) return new SaveFolderResolution(null, SaveFolderSource.None, 0);

        // Whichever one actually holds saves is the live folder, whatever we
        // would otherwise have guessed.
        foreach (var (path, source) in candidates)
        {
            var count = CountSaves(path);
            if (count > 0) return new SaveFolderResolution(path, source, count);
        }

        var (fallback, fallbackSource) = candidates[0];
        return new SaveFolderResolution(fallback, fallbackSource, 0);
    }

    /// <summary>
    /// The path the game reports in <c>savedatapath.txt</c>, or null.
    ///
    /// The file mixes separators — it is written as
    /// <c>C:\Users\me/Documents/My Games/...</c> — so it is normalised rather
    /// than used as-is.
    /// </summary>
    public static string? ReadReportedPath(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var file = Path.Combine(gameDir, PathFileName);
        if (!File.Exists(file)) return null;

        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var index = line.IndexOf(PathMarker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                var raw = line[(index + PathMarker.Length)..].Trim();
                if (raw.Length == 0) continue;

                var normalised = raw.Replace('/', Path.DirectorySeparatorChar)
                                    .TrimEnd(Path.DirectorySeparatorChar);

                return Directory.Exists(normalised) ? Path.GetFullPath(normalised) : null;
            }
        }
        catch (IOException)
        {
            // The game rewrites this on every start; a read mid-write is not worth failing over.
        }

        return null;
    }

    private static int CountSaves(string folder)
    {
        if (!Directory.Exists(folder)) return 0;

        try
        {
            return Directory.GetFiles(folder)
                .Count(f => SaveSetService.IsSaveFile(Path.GetFileName(f)));
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
