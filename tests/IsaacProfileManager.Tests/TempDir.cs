namespace IsaacProfileManager.Tests;

/// <summary>
/// A throwaway directory for a single test. Every filesystem test runs against
/// one of these — never a real Isaac install, because these tests exercise the
/// code paths that delete things.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ipm-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Dir(params string[] parts)
    {
        var full = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(full);
        return full;
    }

    public string File(string relativePath, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            // Remove junctions first so the recursive delete below can never
            // follow one into a directory it does not own.
            foreach (var dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Reverse())
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(dir, recursive: false);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
