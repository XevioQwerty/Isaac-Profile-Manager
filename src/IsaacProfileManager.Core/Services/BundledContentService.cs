using IsaacProfileManager.Core.Models;

namespace IsaacProfileManager.Core.Services;

/// <summary>Something shipped with the app that the user would otherwise have to find.</summary>
public sealed record BundledPatch(string Name, string Path, string Description, bool AlreadyInstalled);

/// <summary>
/// The patches and tools shipped beside the executable.
///
/// These exist because the pieces needed to play modded online are scattered
/// across forum posts and are the part people get stuck on. Shipping them means
/// the app can say "here is the thing" rather than "go and find the thing".
///
/// Bundled content is never applied on its own. A patch is copied into the
/// user's own patches folder and from then on is an ordinary patch, applied and
/// reverted through the same journal as anything they added themselves — there
/// is no second, weaker path for the ones we happened to ship.
/// </summary>
public sealed class BundledContentService
{
    public const string FolderName = "bundled";
    public const string PatchesFolderName = "patches";

    /// <summary>The modded-online patcher, which edits isaac-ng.exe itself.</summary>
    public const string OnlineToolFileName = "IsaacOnlineModded.exe";

    private readonly string _appDirectory;

    public BundledContentService(string? appDirectory = null) =>
        _appDirectory = appDirectory ?? AppPaths.ExecutableDirectory;

    public string Root => Path.Combine(_appDirectory, FolderName);
    public string PatchesRoot => Path.Combine(Root, PatchesFolderName);

    /// <summary>
    /// The bundled online patcher, or null when it is not beside the app — a
    /// plain <c>dotnet run</c> from the repository has no staged bundle.
    /// </summary>
    public string? OnlineToolPath
    {
        get
        {
            var path = Path.Combine(Root, OnlineToolFileName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// What we ship, and whether the user already has a patch by that name.
    ///
    /// <paramref name="installedNames"/> comes from the user's patch folder;
    /// an existing name is reported rather than overwritten, because theirs may
    /// be a newer copy than the one this build shipped with.
    /// </summary>
    public IReadOnlyList<BundledPatch> ListPatches(IEnumerable<string> installedNames)
    {
        if (!Directory.Exists(PatchesRoot)) return Array.Empty<BundledPatch>();

        var installed = installedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.GetDirectories(PatchesRoot)
            .Select(dir =>
            {
                var name = Path.GetFileName(dir);
                return new BundledPatch(
                    Name: name,
                    Path: dir,
                    Description: DescriptionOf(dir),
                    AlreadyInstalled: installed.Contains(name));
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DescriptionOf(string patchDir)
    {
        var manifest = Path.Combine(patchDir, PatchManifest.FileName);
        if (!File.Exists(manifest)) return string.Empty;

        try
        {
            // Target is an enum written as a string, so without the converter
            // this throws and every bundled patch renders with no description.
            var options = new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };

            var parsed = System.Text.Json.JsonSerializer.Deserialize<PatchManifest>(
                File.ReadAllText(manifest), options);
            return parsed?.Description ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            // A description is decoration; a malformed one is not worth failing over.
            return string.Empty;
        }
    }

    /// <summary>
    /// Copy a bundled patch into the user's patches folder, where it becomes an
    /// ordinary one. Refuses to overwrite: a patch of that name may be applied,
    /// and replacing its files under a live journal would leave the record
    /// describing bytes that are no longer there.
    /// </summary>
    public void Install(string name, PatchService patches)
    {
        var source = Path.Combine(PatchesRoot, name);
        if (!Directory.Exists(source))
            throw new UnsafePathException($"'{name}' is not bundled with this build.");

        patches.Install(source, name, PatchTarget.GameRoot);
    }
}
