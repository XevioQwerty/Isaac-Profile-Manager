using System.Xml.Linq;
using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

public interface IWorkshopService
{
    string? WorkshopRoot { get; }
    string? AcfPath { get; }
    bool IsAvailable { get; }
    IReadOnlyList<string> GetSubscribedIds();
    IReadOnlyList<WorkshopItem> GetItems();
    IReadOnlyList<WorkshopItem> MissingFromProfile(string profileDir, IReadOnlyList<WorkshopItem> items);
}

/// <summary>
/// Reads Steam's record of subscribed Workshop items and the content store they
/// were downloaded into.
///
/// Steam downloads to <c>steamapps\workshop\content\250900\&lt;id&gt;\</c>; the
/// folders in <c>mods\</c> are copies named <c>&lt;directory&gt;_&lt;id&gt;</c>,
/// laid down again after a game update. Because <c>mods\</c> is a junction, they
/// land in whichever profile is active — which is the whole reason to import
/// from the content store instead of from <c>mods\</c>.
///
/// Read-only. Nothing here writes to anything Steam owns.
/// </summary>
public sealed class WorkshopService : IWorkshopService
{
    public const string IsaacAppId = "250900";

    /// <summary>Steam's 32-bit account id plus this base gives the 64-bit id used in profile URLs.</summary>
    private const ulong SteamId64Base = 76561197960265728;

    /// <summary>The game's Workshop hub, for finding new mods.</summary>
    public static string BrowseUrl => $"https://steamcommunity.com/app/{IsaacAppId}/workshop/";

    /// <summary>
    /// The subscribed-items view for an account, which is where bulk
    /// unsubscribing is done — going item by item through store pages is
    /// unworkable at 39 subscriptions.
    /// </summary>
    public static string? SubscribedItemsUrl(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !ulong.TryParse(accountId, out var account)) return null;
        return $"https://steamcommunity.com/profiles/{SteamId64Base + account}" +
               $"/myworkshopfiles/?appid={IsaacAppId}&browsefilter=mysubscriptions";
    }

    public static string ItemUrl(string id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    /// <summary>
    /// Wrap a Community URL so it opens inside the Steam client rather than a
    /// system browser. Subscribing needs a logged-in Steam session, so the
    /// in-client browser is where these links are actually useful.
    /// </summary>
    public static string InSteamClient(string httpUrl) => $"steam://openurl/{httpUrl}";

    private static readonly string[] ImageNames = { "thumb.png", "thumbnail.png", "icon.png", "preview.png", "cover.png" };

    public string? WorkshopRoot { get; }

    public WorkshopService(string? workshopRoot) => WorkshopRoot = workshopRoot;

    /// <summary>
    /// Derive the workshop folder from the game directory:
    /// <c>&lt;Library&gt;\steamapps\common\&lt;game&gt;</c> sits three levels below
    /// the library root, and the workshop lives at
    /// <c>&lt;Library&gt;\steamapps\workshop</c>.
    /// </summary>
    public static string? ResolveWorkshopRoot(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var steamapps = System.IO.Directory.GetParent(gameDir)?.Parent;   // <game> -> common -> steamapps
        if (steamapps is null) return null;
        if (!string.Equals(steamapps.Name, "steamapps", StringComparison.OrdinalIgnoreCase)) return null;

        var workshop = Path.Combine(steamapps.FullName, "workshop");
        return System.IO.Directory.Exists(workshop) ? workshop : null;
    }

    public string? AcfPath =>
        WorkshopRoot is null ? null : Path.Combine(WorkshopRoot, $"appworkshop_{IsaacAppId}.acf");

    public string? ContentRoot =>
        WorkshopRoot is null ? null : Path.Combine(WorkshopRoot, "content", IsaacAppId);

    public bool IsAvailable => AcfPath is not null && File.Exists(AcfPath);

    /// <summary>
    /// Subscribed item ids, read from <c>WorkshopItemsInstalled</c> only. The
    /// same ids appear again under <c>WorkshopItemDetails</c>; reading both
    /// double-counts.
    /// </summary>
    public IReadOnlyList<string> GetSubscribedIds()
    {
        if (AcfPath is null || !File.Exists(AcfPath)) return Array.Empty<string>();

        VdfNode root;
        try
        {
            root = VdfParser.ParseFile(AcfPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Array.Empty<string>();
        }

        var installed = root.Find("WorkshopItemsInstalled");
        if (installed is null) return Array.Empty<string>();

        return installed.Children
            .Where(pair => pair.Value.IsSection && pair.Key.All(char.IsDigit))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<WorkshopItem> GetItems()
    {
        var items = new List<WorkshopItem>();
        var contentRoot = ContentRoot;

        foreach (var id in GetSubscribedIds())
        {
            var contentPath = contentRoot is null ? string.Empty : Path.Combine(contentRoot, id);
            items.Add(Describe(id, contentPath));
        }

        return items;
    }

    private static WorkshopItem Describe(string id, string contentPath)
    {
        string name = id, directory = id, description = string.Empty;

        var metadataPath = Path.Combine(contentPath, "metadata.xml");
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadata = XDocument.Load(metadataPath).Root;
                name = Trimmed(metadata?.Element("name")?.Value) ?? id;
                directory = Trimmed(metadata?.Element("directory")?.Value) ?? name;
                description = Trimmed(metadata?.Element("description")?.Value) ?? string.Empty;
            }
            catch (System.Xml.XmlException)
            {
                // Hand-edited metadata is common; fall back to the id rather than
                // dropping the item out of the list entirely.
            }
        }

        long size = 0;
        string? image = null;
        if (System.IO.Directory.Exists(contentPath))
        {
            var info = new DirectoryInfo(contentPath);
            try { size = info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch (IOException) { }
            image = FindImage(contentPath);
        }

        return new WorkshopItem
        {
            Id = id,
            Name = name,
            Directory = directory,
            Description = description,
            ContentPath = contentPath,
            SizeBytes = size,
            LocalImagePath = image,
        };
    }

    /// <summary>
    /// Best-effort preview. Authors ship these under several names or not at
    /// all — the Workshop store image is server-side metadata, not a file in the
    /// item — so most items legitimately have none.
    /// </summary>
    private static string? FindImage(string contentPath)
    {
        foreach (var candidate in ImageNames)
        {
            var path = Path.Combine(contentPath, candidate);
            if (File.Exists(path)) return path;
        }

        try
        {
            return new DirectoryInfo(contentPath)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Where(f => f.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("icon", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("cover", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("preview", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Subscribed items with no matching <c>&lt;directory&gt;_&lt;id&gt;</c> folder
    /// in the profile. These are exactly what Steam re-materialises into that
    /// profile the next time it is active — verified happening on launch.
    /// </summary>
    public IReadOnlyList<WorkshopItem> MissingFromProfile(string profileDir, IReadOnlyList<WorkshopItem> items)
    {
        if (!System.IO.Directory.Exists(profileDir)) return items;

        var present = System.IO.Directory.GetDirectories(profileDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        return items.Where(item => !present.Contains(item.MaterialisedFolderName)).ToList();
    }

    /// <summary>
    /// Folders that exist both suffix-free and with a workshop suffix — the
    /// state that means a local copy was made but the unsubscribe never
    /// happened, so the mod loads twice.
    /// </summary>
    public static IReadOnlyList<string> FindDuplicatePairs(string profileDir)
    {
        if (!System.IO.Directory.Exists(profileDir)) return Array.Empty<string>();

        var names = System.IO.Directory.GetDirectories(profileDir).Select(Path.GetFileName).OfType<string>().ToList();
        var bare = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var duplicates = new List<string>();
        foreach (var name in names)
        {
            var underscore = name.LastIndexOf('_');
            if (underscore <= 0) continue;

            var suffix = name[(underscore + 1)..];
            if (suffix.Length < 6 || !suffix.All(char.IsDigit)) continue;

            var stripped = name[..underscore];
            if (bare.Contains(stripped)) duplicates.Add($"{stripped}  +  {name}");
        }
        return duplicates;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
