namespace IsaacProfileManager.Core;

/// <summary>
/// Where this application actually lives on disk.
///
/// <see cref="AppContext.BaseDirectory"/> is the obvious answer and the wrong
/// one here. This app publishes as a single file with
/// <c>IncludeNativeLibrariesForSelfExtract</c>, so the host extracts WPF's
/// native DLLs to <c>%TEMP%\.net\IsaacProfileManager\&lt;hash&gt;\</c> and
/// <c>BaseDirectory</c> reports *that* folder. Anything looking for a file
/// shipped beside the exe — the config, or the Steam helper — then looks in a
/// temp directory and concludes it is missing.
///
/// Observed 2026-08-28: the helper was sitting next to the exe and the app still
/// reported it missing, and the config beside the exe was only found through the
/// <c>config-location.txt</c> pointer written during setup.
///
/// <see cref="Environment.ProcessPath"/> is the single-file-safe answer: it is
/// the path of the running executable, not the bundle's scratch space.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The folder holding the running executable. Falls back to
    /// <see cref="AppContext.BaseDirectory"/>, which is correct for a normal
    /// (non-bundled) build and for the test host.
    /// </summary>
    public static string ExecutableDirectory
    {
        get
        {
            var process = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(process))
            {
                var directory = Path.GetDirectoryName(process);
                if (!string.IsNullOrWhiteSpace(directory)) return directory;
            }

            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// The running build, as three numbers. Read from the assembly rather than a
    /// constant so it cannot drift from what was actually compiled.
    /// </summary>
    public static string Version
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                       ?? typeof(AppPaths).Assembly.GetName().Version;

            return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>
    /// The file version stamped on an executable beside this one, or null when
    /// it is not there. Used to show that the app and the Steam helper are the
    /// same build, since they ship as a pair and break subtly when they are not.
    /// </summary>
    public static string? VersionOf(string fileName)
    {
        foreach (var root in ProbeRoots())
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) continue;

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion;
        }
        return null;
    }

    /// <summary>
    /// Places a file shipped with the app could be, nearest first: beside the
    /// executable, then the bundle's extraction directory when they differ.
    /// </summary>
    public static IEnumerable<string> ProbeRoots()
    {
        var executable = ExecutableDirectory;
        yield return executable;

        var bundle = AppContext.BaseDirectory;
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(bundle),
                Path.TrimEndingDirectorySeparator(executable),
                StringComparison.OrdinalIgnoreCase))
            yield return bundle;
    }
}
