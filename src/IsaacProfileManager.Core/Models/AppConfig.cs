using System.Text.Json.Serialization;

namespace IsaacProfileManager.Core.Models;

/// <summary>
/// The on-disk configuration, shared byte-for-byte with IsaacProfiles.ps1.
///
/// The property names and <see cref="ConfigVersion"/> deliberately match what
/// the PowerShell script writes so both tools can drive the same install. The
/// script round-trips through ConvertFrom-Json / ConvertTo-Json, which preserves
/// properties it does not know about, so keys added here survive a PowerShell
/// write and vice versa (see <see cref="Extra"/>).
///
/// Do not bump ConfigVersion without also updating Assert-Config in the script:
/// it refuses to run against anything below 3, and silently falling back to a
/// default once made the tool launch the wrong build.
/// </summary>
public sealed class AppConfig
{
    /// <summary>The only schema this build understands. Mismatch is a refusal, never a fallback.</summary>
    public const int SupportedConfigVersion = 3;

    [JsonPropertyName("ConfigVersion")]
    public int ConfigVersion { get; set; } = SupportedConfigVersion;

    [JsonPropertyName("IsaacExe")]
    public string? IsaacExe { get; set; }

    [JsonPropertyName("GameDir")]
    public string? GameDir { get; set; }

    [JsonPropertyName("ModsDir")]
    public string? ModsDir { get; set; }

    /// <summary>Folder holding every profile. Synced or versioned; kept outside the game directory.</summary>
    [JsonPropertyName("SyncRoot")]
    public string? SyncRoot { get; set; }

    [JsonPropertyName("Profiles")]
    public List<string> Profiles { get; set; } = new();

    [JsonPropertyName("ActiveProfile")]
    public string? ActiveProfile { get; set; }

    /// <summary>Names of the profiles that should launch on the REPENTOGON build.</summary>
    [JsonPropertyName("UseRepentogon")]
    public List<string> UseRepentogon { get; set; } = new();

    [JsonPropertyName("PerProfileBuild")]
    public bool PerProfileBuild { get; set; }

    [JsonPropertyName("LauncherExe")]
    public string? LauncherExe { get; set; }

    [JsonPropertyName("OwnsOnSteam")]
    public bool OwnsOnSteam { get; set; }

    [JsonPropertyName("ShortcutDirs")]
    public List<string> ShortcutDirs { get; set; } = new();

    [JsonPropertyName("SetupDate")]
    public string? SetupDate { get; set; }

    // --- Keys added by this application -------------------------------------
    // Optional. Absent means "not configured yet", never an error: a config
    // written by the PowerShell script will not contain them.

    /// <summary>
    /// Folder holding the swappable build variants, one subfolder per variant.
    /// Defaults to <c>&lt;GameDir&gt;\~</c>. The game's <c>Repentogon\</c> is a
    /// junction pointing at whichever one is active.
    /// </summary>
    [JsonPropertyName("BuildRoot")]
    public string? BuildRoot { get; set; }

    /// <summary>
    /// The folder inside the game directory that the REPENTOGON launcher loads
    /// the downgraded build from, and which this tool re-points to switch builds.
    ///
    /// Null means the stock <c>Repentogon</c>. It is configurable because the
    /// name is the launcher's convention rather than something we control, and a
    /// reinstall or a non-standard layout can put it elsewhere. Added
    /// additively, so ConfigVersion stays at 3 and the PowerShell side, which
    /// never reads it, is unaffected.
    /// </summary>
    [JsonPropertyName("BuildLinkFolder")]
    public string? BuildLinkFolder { get; set; }

    /// <summary>
    /// The folder the game actually keeps its live saves in, when it is not
    /// where we would work it out to be.
    ///
    /// Steam's userdata folder is right for a copy running against the real
    /// Steam client and wrong for one running a DRM emulator, where the
    /// emulated API writes somewhere else entirely. Null means work it out.
    /// </summary>
    [JsonPropertyName("SaveFolder")]
    public string? SaveFolder { get; set; }

    /// <summary>Human-readable note of the variant last activated. Advisory only — the junction is the truth.</summary>
    [JsonPropertyName("ActiveBuildVariant")]
    public string? ActiveBuildVariant { get; set; }

    /// <summary>
    /// Steam's workshop folder (<c>&lt;Library&gt;\steamapps\workshop</c>). Absent
    /// means "derive it from GameDir", which is right for a standard install.
    /// </summary>
    [JsonPropertyName("WorkshopRoot")]
    public string? WorkshopRoot { get; set; }

    /// <summary>How the Launch button starts the game: <c>Steam</c> or <c>File</c>.</summary>
    [JsonPropertyName("LaunchMethod")]
    public string? LaunchMethod { get; set; }

    /// <summary>The executable the <c>File</c> method runs. Usually REPENTOGONLauncher.exe.</summary>
    [JsonPropertyName("LaunchTarget")]
    public string? LaunchTarget { get; set; }

    /// <summary>Per-profile free text: "what was I running, who was I playing with".</summary>
    [JsonPropertyName("ProfileNotes")]
    public Dictionary<string, string> ProfileNotes { get; set; } = new();

    // --- Added in 2.0 -------------------------------------------------------
    // Still ConfigVersion 3: every key here is optional, and the PowerShell
    // script preserves what it does not recognise.

    /// <summary>
    /// The save set last activated or captured on this machine. Advisory: the
    /// live files are hashed against every set to find out what is really
    /// loaded, and this only breaks a tie or names a set that has drifted.
    /// </summary>
    [JsonPropertyName("ActiveSaveSet")]
    public string? ActiveSaveSet { get; set; }

    /// <summary>Stable id for this machine, written once. Save sets record which device captured them.</summary>
    [JsonPropertyName("DeviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Friendly name for this machine. Defaults to the machine name.</summary>
    [JsonPropertyName("DeviceName")]
    public string? DeviceName { get; set; }

    /// <summary>The last <c>Game Version:</c> this machine ran, read from log.txt after each session.</summary>
    [JsonPropertyName("LastGameVersion")]
    public string? LastGameVersion { get; set; }

    /// <summary>
    /// The last version each build ran here. A version belongs to a build:
    /// REPENTOGON is pinned to J273 while retail moves, so the number to check
    /// a save against is the one for the build the launcher is about to start.
    /// </summary>
    [JsonPropertyName("LastVanillaVersion")]
    public string? LastVanillaVersion { get; set; }

    [JsonPropertyName("LastRepentogonVersion")]
    public string? LastRepentogonVersion { get; set; }

    /// <summary>
    /// What to do with the live saves when the game exits: <c>Off</c>,
    /// <c>Ask</c> or <c>Automatic</c>. Null means Ask.
    /// </summary>
    [JsonPropertyName("ExitCapture")]
    public string? ExitCapture { get; set; }

    /// <summary>Whether each screen shows its orientation card at the top. Null means yes.</summary>
    [JsonPropertyName("ShowGuides")]
    public bool? ShowGuides { get; set; }

    // --- Save sync between your own machines --------------------------------
    // A lane store: each device writes only its own lane and the app
    // reconciles. Off by default; a Steam-owned copy has Cloud for this.

    /// <summary><c>Off</c>, <c>Folder</c> or <c>Cloud</c>. Null means Off.</summary>
    [JsonPropertyName("SaveSyncMode")]
    public string? SaveSyncMode { get; set; }

    /// <summary>Lane root for the Folder mode. Null means <c>&lt;SyncRoot&gt;\.savesync</c>.</summary>
    [JsonPropertyName("SaveSyncFolder")]
    public string? SaveSyncFolder { get; set; }

    /// <summary>The Worker's address for the Cloud mode, e.g. <c>https://ipm-saves.example.workers.dev</c>.</summary>
    [JsonPropertyName("SaveSyncEndpoint")]
    public string? SaveSyncEndpoint { get; set; }

    /// <summary>Bearer token and namespace for the Cloud mode. Generated on the first device, pasted on the others.</summary>
    [JsonPropertyName("SaveSyncKey")]
    public string? SaveSyncKey { get; set; }

    /// <summary>Pull a newer revision before launch and push after exit capture without asking. Null means ask.</summary>
    [JsonPropertyName("SaveSyncAutomatic")]
    public bool? SaveSyncAutomatic { get; set; }

    /// <summary>Anything written by another version or by the PowerShell script. Preserved verbatim on write.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = new();
}
