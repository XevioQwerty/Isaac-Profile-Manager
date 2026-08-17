namespace IsaacProfileManager.Core.Models;

/// <summary>
/// A subscribed Workshop item as it exists in Steam's own content store,
/// before Isaac materialises a copy of it into <c>mods\</c>.
/// </summary>
public sealed class WorkshopItem
{
    /// <summary>Steam published file id. The suffix Isaac appends to the folder name.</summary>
    public required string Id { get; init; }

    /// <summary>Display name from metadata.xml. Frequently starts with punctuation to force load order.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The mod's own folder name from metadata.xml. Isaac materialises the mod
    /// as <c>&lt;Directory&gt;_&lt;Id&gt;</c>, so this is the suffix-free name a
    /// local copy should use.
    /// </summary>
    public required string Directory { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Path in <c>steamapps\workshop\content\250900\&lt;id&gt;\</c>. Empty when Steam has not downloaded it.</summary>
    public string ContentPath { get; init; } = string.Empty;

    public bool ContentPresent => ContentPath.Length > 0 && System.IO.Directory.Exists(ContentPath);

    public long SizeBytes { get; init; }

    /// <summary>
    /// A preview image found inside the item, if the author shipped one. The
    /// Workshop store image is server-side metadata rather than a file, so this
    /// is absent for most items.
    /// </summary>
    public string? LocalImagePath { get; init; }

    /// <summary>The folder name Isaac would create for this item under <c>mods\</c>.</summary>
    public string MaterialisedFolderName => $"{Directory}_{Id}";

    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);
}
