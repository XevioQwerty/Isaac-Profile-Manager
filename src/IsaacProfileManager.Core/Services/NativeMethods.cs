using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Win32 entry points needed to create a directory junction.
///
/// .NET has <c>Directory.CreateSymbolicLink</c>, but a symbolic link needs
/// SeCreateSymbolicLinkPrivilege — i.e. administrator, or Developer Mode.
/// Requesting elevation for a folder-linking tool would make this look like
/// malware to a modding audience, so we create junctions (reparse tag
/// IO_REPARSE_TAG_MOUNT_POINT) instead, which any user can do.
/// </summary>
internal static class NativeMethods
{
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
