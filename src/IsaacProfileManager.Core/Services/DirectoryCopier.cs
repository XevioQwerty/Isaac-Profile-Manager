namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Recursive directory copy that refuses to follow reparse points.
///
/// Copying *through* a junction would silently duplicate whatever it points at
/// — for a build folder that is gigabytes of game resources, and for a profile
/// folder it would be another profile.
/// </summary>
public static class DirectoryCopier
{
    public static void Copy(string sourceDir, string destinationDir, bool overwrite = true, IProgress<string>? progress = null)
    {
        var source = new DirectoryInfo(sourceDir);
        if (!source.Exists)
            throw new DirectoryNotFoundException($"Source folder does not exist: {sourceDir}");

        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnsafePathException($"Refusing to copy from '{sourceDir}' — it is a link, not a real folder.");

        CopyCore(source, destinationDir, overwrite, progress);
    }

    private static void CopyCore(DirectoryInfo source, string destinationDir, bool overwrite, IProgress<string>? progress)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in source.GetFiles())
        {
            var target = Path.Combine(destinationDir, file.Name);
            if (!overwrite && File.Exists(target)) continue;
            file.CopyTo(target, overwrite);
        }

        foreach (var child in source.GetDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                progress?.Report($"Skipped link: {child.FullName}");
                continue;
            }
            progress?.Report(child.Name);
            CopyCore(child, Path.Combine(destinationDir, child.Name), overwrite, progress);
        }
    }
}
