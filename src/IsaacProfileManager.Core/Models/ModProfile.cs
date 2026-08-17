namespace IsaacProfileManager.Core.Models;

/// <summary>A profile as it exists on disk right now, assembled for display.</summary>
public sealed class ModProfile
{
    public required string Name { get; init; }

    /// <summary>Absolute path to the folder the mods junction would point at.</summary>
    public required string Path { get; init; }

    public bool FolderExists { get; init; }

    /// <summary>Subfolders of the profile. Isaac treats every one of them as a candidate mod.</summary>
    public int ModCount { get; init; }

    /// <summary>
    /// Mods carrying a <c>disable.it</c> marker. These are present but silently
    /// off, which looks identical to a failed sync — the reason activation sweeps them.
    /// </summary>
    public int DisabledCount { get; init; }

    public bool IsActive { get; init; }

    /// <summary>Whether activating this profile should select the REPENTOGON build.</summary>
    public bool UseRepentogon { get; init; }

    public string Notes { get; init; } = string.Empty;

    public DateTime? LastModified { get; init; }
}
