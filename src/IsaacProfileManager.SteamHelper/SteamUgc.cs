using System.Runtime.InteropServices;
using System.Text;

namespace IsaacProfileManager.SteamHelper;

[Flags]
public enum ItemState : uint
{
    None = 0,
    Subscribed = 1,
    LegacyItem = 2,
    Installed = 4,
    NeedsUpdate = 8,
    Downloading = 16,
    DownloadPending = 32,
}

/// <summary>
/// The slice of ISteamUGC needed to pull a Workshop item without the game.
///
/// Bound against the flat C API in Isaac's own <c>steam_api.dll</c> rather than
/// a shipped copy: the game's is Valve's (verified 2026-08-27, byte-identical to
/// the <c>steam_api_o.dll</c> beside it), it is already on disk, and shipping our
/// own would raise a redistribution question for no gain.
///
/// Two details are load-bearing. The flat API is <c>__cdecl</c>, not stdcall, so
/// every import must say so or x86 stack cleanup goes wrong. And
/// <c>SteamAPI_RestartAppIfNecessary</c> is never called — that is the function
/// that would make Steam launch Isaac out from under us.
/// </summary>
public sealed class SteamUgc : IDisposable
{
    public const uint IsaacAppId = 250900;

    private const string Dll = "steam_api";

    /// <summary>
    /// Accessor names carry the interface version, so they change when the game
    /// updates its SDK. Isaac ships v020 today; the rest are probed so a game
    /// update does not silently break the helper.
    /// </summary>
    private static readonly string[] AccessorNames =
    {
        "SteamAPI_SteamUGC_v020", "SteamAPI_SteamUGC_v021", "SteamAPI_SteamUGC_v022",
        "SteamAPI_SteamUGC_v019", "SteamAPI_SteamUGC_v018", "SteamAPI_SteamUGC_v017",
    };

    private static readonly string[] AppsAccessorNames =
    {
        "SteamAPI_SteamApps_v008", "SteamAPI_SteamApps_v009", "SteamAPI_SteamApps_v007",
    };

    private static readonly string[] UserAccessorNames =
    {
        "SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v024", "SteamAPI_SteamUser_v022",
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr UgcAccessor();

    /// <summary>
    /// The exported entry point. <c>SteamAPI_Init</c> is not in this dll's export
    /// table at all — in the current SDK it is a header inline that forwards to
    /// <c>SteamInternal_SteamAPI_Init</c>. <c>SteamAPI_InitFlat</c> is the
    /// supported door for flat-API consumers, and it hands back Steam's own
    /// explanation on failure, which beats a bare false.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int SteamAPI_InitFlat(StringBuilder errorMessage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_Shutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_RunCallbacks();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SteamAPI_ISteamUGC_SubscribeItem(IntPtr self, ulong publishedFileId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SteamAPI_ISteamUGC_UnsubscribeItem(IntPtr self, ulong publishedFileId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUGC_DownloadItem(
        IntPtr self, ulong publishedFileId, [MarshalAs(UnmanagedType.I1)] bool highPriority);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SteamAPI_ISteamUGC_GetItemState(IntPtr self, ulong publishedFileId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUGC_GetItemInstallInfo(
        IntPtr self, ulong publishedFileId, out ulong sizeOnDisk,
        StringBuilder folder, uint folderSize, out uint timestamp);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUGC_GetItemDownloadInfo(
        IntPtr self, ulong publishedFileId, out ulong bytesDownloaded, out ulong bytesTotal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SteamAPI_ISteamUGC_GetNumSubscribedItems(IntPtr self);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr self, uint appId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SteamAPI_ISteamUGC_GetSubscribedItems(
        IntPtr self, [Out] ulong[] publishedFileIds, uint maxEntries);

    private static IntPtr _module = IntPtr.Zero;
    private readonly IntPtr _ugc;
    private readonly IntPtr _apps;
    private readonly IntPtr _user;
    private bool _shutdown;

    private SteamUgc(IntPtr ugc, IntPtr apps, IntPtr user)
    {
        _ugc = ugc;
        _apps = apps;
        _user = user;
    }

    /// <summary>
    /// Bring up the Steam API as Isaac and hand back the UGC interface.
    /// Fails when Steam is not running, no user is signed in, or the account
    /// does not own the app.
    /// </summary>
    public static SteamUgc Connect(string gameDir)
    {
        var dllPath = Path.Combine(gameDir, "steam_api.dll");
        if (!File.Exists(dllPath))
            throw new SteamHelperException($"No steam_api.dll in '{gameDir}'. Point --game-dir at Isaac's install folder.");

        if (_module == IntPtr.Zero)
        {
            _module = NativeLibrary.Load(dllPath);
            NativeLibrary.SetDllImportResolver(typeof(SteamUgc).Assembly,
                (name, _, _) => name == Dll ? _module : IntPtr.Zero);
        }

        // SteamAPI_Init reads these at init; setting them beats writing a
        // steam_appid.txt into a folder we may not own.
        Environment.SetEnvironmentVariable("SteamAppId", IsaacAppId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", IsaacAppId.ToString());

        var error = new StringBuilder(1024);
        var result = SteamAPI_InitFlat(error);
        if (result != 0)
            throw new SteamHelperException(Explain(result, error.ToString()));

        var ugc = Resolve(AccessorNames);
        if (ugc == IntPtr.Zero)
        {
            SteamAPI_Shutdown();
            throw new SteamHelperException(
                "steam_api.dll exposes no ISteamUGC version this build knows. The game's Steamworks SDK has moved on.");
        }

        // Apps and User are best-effort: without them the ownership check is
        // simply unavailable, which is worse than having it but not fatal.
        return new SteamUgc(ugc, Resolve(AppsAccessorNames), Resolve(UserAccessorNames));
    }

    private static IntPtr Resolve(string[] candidates)
    {
        foreach (var name in candidates)
        {
            if (!NativeLibrary.TryGetExport(_module, name, out var address)) continue;

            var pointer = Marshal.GetDelegateForFunctionPointer<UgcAccessor>(address)();
            if (pointer != IntPtr.Zero) return pointer;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Whether the signed-in account owns the app.
    ///
    /// This is the check that explains the otherwise silent failure: Steam only
    /// lets you subscribe to Workshop items for a game you own. Without a
    /// licence, SubscribeItem is accepted and then does nothing, the item sits
    /// in state None forever, and a caller polling for "installed" simply waits
    /// out its timeout with nothing to report.
    ///
    /// Null when ISteamApps could not be resolved, which is not the same as false.
    /// </summary>
    public bool? OwnsApp() =>
        _apps == IntPtr.Zero ? null : SteamAPI_ISteamApps_BIsSubscribedApp(_apps, IsaacAppId);

    public bool? IsLoggedOn() =>
        _user == IntPtr.Zero ? null : SteamAPI_ISteamUser_BLoggedOn(_user);

    /// <summary>ESteamAPIInitResult, turned into something a player can act on.</summary>
    private static string Explain(int result, string detail)
    {
        var reason = result switch
        {
            2 => "Steam is not running, or no user is signed in.",
            3 => "Steam and the game's Steamworks SDK disagree on interface versions.",
            _ => "Steam refused the connection.",
        };

        // Owning the app matters as much as Steam being up: a Workshop
        // subscription is made as the signed-in account, against this app id.
        return detail.Length > 0
            ? $"{reason} Steam said: {detail}"
            : $"{reason} Start Steam, sign in, and make sure the account owns The Binding of Isaac: Rebirth.";
    }

    public void RunCallbacks() => SteamAPI_RunCallbacks();

    public uint SubscribedCount() => SteamAPI_ISteamUGC_GetNumSubscribedItems(_ugc);

    /// <summary>
    /// Everything the signed-in account is subscribed to for this app, asked of
    /// Steam directly. Preferred over reading appworkshop_250900.acf: the acf is
    /// Steam's cache of the same fact and can lag behind the client.
    /// </summary>
    public ulong[] SubscribedItems()
    {
        var count = SteamAPI_ISteamUGC_GetNumSubscribedItems(_ugc);
        if (count == 0) return Array.Empty<ulong>();

        var buffer = new ulong[count];
        var written = SteamAPI_ISteamUGC_GetSubscribedItems(_ugc, buffer, count);
        return written >= count ? buffer : buffer[..(int)written];
    }

    public void Subscribe(ulong id) => SteamAPI_ISteamUGC_SubscribeItem(_ugc, id);

    public void Unsubscribe(ulong id) => SteamAPI_ISteamUGC_UnsubscribeItem(_ugc, id);

    public bool Download(ulong id) => SteamAPI_ISteamUGC_DownloadItem(_ugc, id, highPriority: true);

    public ItemState State(ulong id) => (ItemState)SteamAPI_ISteamUGC_GetItemState(_ugc, id);

    public (ulong Downloaded, ulong Total) DownloadProgress(ulong id) =>
        SteamAPI_ISteamUGC_GetItemDownloadInfo(_ugc, id, out var downloaded, out var total)
            ? (downloaded, total)
            : (0, 0);

    /// <summary>Where Steam put the item, plus its size and content timestamp.</summary>
    public (string Folder, ulong SizeOnDisk, uint Timestamp)? InstallInfo(ulong id)
    {
        var buffer = new StringBuilder(1024);
        return SteamAPI_ISteamUGC_GetItemInstallInfo(_ugc, id, out var size, buffer, (uint)buffer.Capacity, out var stamp)
            ? (buffer.ToString(), size, stamp)
            : null;
    }

    public void Dispose()
    {
        if (_shutdown) return;
        _shutdown = true;
        SteamAPI_Shutdown();
    }
}

public sealed class SteamHelperException : Exception
{
    public SteamHelperException(string message) : base(message) { }
}
