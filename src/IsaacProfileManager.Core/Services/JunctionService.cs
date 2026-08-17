using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Thrown when a filesystem path is not what we require it to be — typically a
/// real folder sitting where a junction was expected. Always a refusal to act,
/// never something the caller should retry with more force.
/// </summary>
public sealed class UnsafePathException : Exception
{
    public UnsafePathException(string message) : base(message) { }
}

public interface IJunctionService
{
    bool Exists(string path);
    bool IsJunction(string path);
    string? GetTarget(string path);
    void RemoveLink(string path);
    void Create(string linkPath, string targetPath);
    void Repoint(string linkPath, string targetPath);
}

/// <summary>
/// Create, inspect and remove directory junctions.
///
/// A junction looks like an ordinary folder to most tools, and a recursive
/// delete aimed at one can follow the link and wipe the *target*. Every removal
/// here goes through <see cref="Directory.Delete(string, bool)"/> with
/// recursive:false, which cannot recurse, and refuses outright on anything that
/// is not a reparse point.
/// </summary>
public sealed class JunctionService : IJunctionService
{
    public bool Exists(string path) => Directory.Exists(path);

    public bool IsJunction(string path)
    {
        if (!Directory.Exists(path)) return false;
        var attributes = new DirectoryInfo(path).Attributes;
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>The path a junction points at, or null if <paramref name="path"/> is not a link.</summary>
    public string? GetTarget(string path)
    {
        if (!IsJunction(path)) return null;
        var target = new DirectoryInfo(path).LinkTarget;
        if (string.IsNullOrEmpty(target)) return null;

        // Junction targets are stored in NT namespace form; strip it so the
        // value compares equal to an ordinary path the user typed or picked.
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return target.TrimEnd('\\');
    }

    /// <summary>
    /// Delete a junction, leaving its target untouched.
    /// Refuses if the path is a real directory — that would be the user's data.
    /// </summary>
    public void RemoveLink(string path)
    {
        if (!Directory.Exists(path))
            return;

        if (!IsJunction(path))
            throw new UnsafePathException(
                $"Refusing to delete '{path}' — it is a real folder, not a junction. Move it aside yourself, then try again.");

        Directory.Delete(path, recursive: false);
    }

    /// <summary>Create a junction at <paramref name="linkPath"/> pointing at an existing directory.</summary>
    public void Create(string linkPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath)) throw new ArgumentException("Link path is empty.", nameof(linkPath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path is empty.", nameof(targetPath));

        var fullTarget = Path.GetFullPath(targetPath).TrimEnd('\\');
        var fullLink = Path.GetFullPath(linkPath).TrimEnd('\\');

        if (!Directory.Exists(fullTarget))
            throw new UnsafePathException($"Junction target does not exist: {fullTarget}");

        if (Directory.Exists(fullLink))
        {
            // A junction may only be created on an empty directory. Anything
            // already here is either a stale link or the user's data.
            if (IsJunction(fullLink))
                throw new UnsafePathException($"'{fullLink}' is already a junction. Remove it first.");
            throw new UnsafePathException($"'{fullLink}' already exists as a real folder. Refusing to overwrite it.");
        }

        Directory.CreateDirectory(fullLink);
        try
        {
            SetMountPoint(fullLink, fullTarget);
        }
        catch
        {
            // Leave nothing half-made: the empty directory we just created is
            // ours, so removing it cannot touch anything the user owns.
            try { Directory.Delete(fullLink, recursive: false); } catch { /* report the original failure */ }
            throw;
        }
    }

    /// <summary>Point an existing junction somewhere else, creating it if absent.</summary>
    public void Repoint(string linkPath, string targetPath)
    {
        RemoveLink(linkPath);
        Create(linkPath, targetPath);
    }

    private static void SetMountPoint(string linkPath, string targetPath)
    {
        // REPARSE_DATA_BUFFER, mount-point flavour:
        //   ULONG  ReparseTag              4
        //   USHORT ReparseDataLength       2
        //   USHORT Reserved                2
        //   USHORT SubstituteNameOffset    2  <- ReparseDataLength counts from here
        //   USHORT SubstituteNameLength    2
        //   USHORT PrintNameOffset         2
        //   USHORT PrintNameLength         2
        //   WCHAR  PathBuffer[]
        var substituteName = @"\??\" + targetPath;
        var printName = targetPath;

        var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
        var printBytes = System.Text.Encoding.Unicode.GetBytes(printName);

        // Both names are NUL-terminated inside PathBuffer, but the declared
        // lengths exclude the terminators.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var reparseDataLength = 8 + pathBufferLength;
        var totalLength = 8 + reparseDataLength;

        var buffer = new byte[totalLength];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(NativeMethods.IO_REPARSE_TAG_MOUNT_POINT);
            writer.Write((ushort)reparseDataLength);
            writer.Write((ushort)0);                                  // Reserved
            writer.Write((ushort)0);                                  // SubstituteNameOffset
            writer.Write((ushort)substituteBytes.Length);             // SubstituteNameLength
            writer.Write((ushort)(substituteBytes.Length + 2));       // PrintNameOffset
            writer.Write((ushort)printBytes.Length);                  // PrintNameLength
            writer.Write(substituteBytes);
            writer.Write((ushort)0);                                  // NUL
            writer.Write(printBytes);
            writer.Write((ushort)0);                                  // NUL
        }

        using SafeFileHandle handle = NativeMethods.CreateFile(
            linkPath,
            NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open '{linkPath}' to make it a junction.");

        IntPtr native = Marshal.AllocHGlobal(totalLength);
        try
        {
            Marshal.Copy(buffer, 0, native, totalLength);
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FSCTL_SET_REPARSE_POINT,
                    native,
                    totalLength,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create junction '{linkPath}' -> '{targetPath}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }
}
