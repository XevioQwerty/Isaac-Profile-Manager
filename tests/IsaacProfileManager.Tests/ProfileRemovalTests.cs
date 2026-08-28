using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Services;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

/// <summary>
/// Deleting a profile. The folder and the manifest go; the library it links to
/// must not, and neither must a mod that only exists inside that profile.
/// </summary>
public class ProfileRemovalTests
{
    private static (ModProfileService Service, AppConfig Config, ModLibraryService Library) Build(TempDir temp)
    {
        var gameDir = temp.Dir("game");
        var syncRoot = temp.Dir("sync");
        var junctions = new JunctionService();

        var config = new AppConfig
        {
            GameDir = gameDir,
            ModsDir = Path.Combine(gameDir, "mods"),
            SyncRoot = syncRoot,
            Profiles = new List<string> { "other", "doomed" },
            ActiveProfile = "other",
        };

        var ini = new LauncherIniService(temp.File("launcher.ini", "[Shared]\nLaunchMode = 0\n"));
        var store = new ConfigStore(Path.Combine(temp.Path, ConfigStore.FileName));

        return (new ModProfileService(junctions, ini, store), config, new ModLibraryService(junctions, syncRoot));
    }

    private static WorkshopItem Item(TempDir temp, string id, string directory)
    {
        var content = temp.Dir("content", id);
        temp.File($@"content\{id}\main.lua", $"-- {directory}");
        return new WorkshopItem { Id = id, Name = directory, Directory = directory, ContentPath = content };
    }

    [Fact]
    public void Remove_DeletesTheFolderAndTheManifest()
    {
        using var temp = new TempDir();
        var (service, config, library) = Build(temp);

        library.Import(Item(temp, "111", "alpha"));
        library.SaveManifest("doomed", new ProfileManifest { Mods = new List<string> { "alpha" } });
        library.Materialise("doomed", library.LoadManifest("doomed"));

        var profileDir = temp.Combine("sync", "doomed");
        var manifest = temp.Combine("sync", ModLibraryService.ManifestFolderName, "doomed.json");
        Assert.True(Directory.Exists(profileDir));
        Assert.True(File.Exists(manifest));

        var removal = service.Remove(config, "doomed");

        Assert.True(removal.FolderDeleted);
        Assert.True(removal.ManifestDeleted);
        Assert.False(Directory.Exists(profileDir));
        Assert.False(File.Exists(manifest));
        Assert.DoesNotContain("doomed", config.Profiles);
    }

    [Fact]
    public void Remove_UnlinksWithoutTouchingTheLibraryBehindTheJunction()
    {
        // The one that matters. A recursive delete on a folder of junctions
        // follows them and destroys the shared library every profile depends on.
        using var temp = new TempDir();
        var (service, config, library) = Build(temp);

        library.Import(Item(temp, "111", "alpha"));
        library.Import(Item(temp, "222", "beta"));
        library.SaveManifest("doomed", new ProfileManifest { Mods = new List<string> { "alpha", "beta" } });
        library.Materialise("doomed", library.LoadManifest("doomed"));

        var removal = service.Remove(config, "doomed");

        Assert.Equal(2, removal.LinksRemoved);
        Assert.Equal(new[] { "alpha", "beta" }, library.ListEntries());
        Assert.True(File.Exists(Path.Combine(library.LibraryRoot, "alpha", "main.lua")));
        Assert.True(File.Exists(Path.Combine(library.LibraryRoot, "beta", "main.lua")));
    }

    [Fact]
    public void Remove_MovesAHandInstalledModToBackupInsteadOfDeletingIt()
    {
        // A real folder inside a profile can be the only copy in existence.
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);

        temp.File(@"sync\doomed\my hand written mod\main.lua", "-- irreplaceable");

        var removal = service.Remove(config, "doomed");

        Assert.Equal(new[] { "my hand written mod" }, removal.MovedToBackup);
        Assert.NotNull(removal.BackupPath);
        Assert.Equal("-- irreplaceable",
            File.ReadAllText(Path.Combine(removal.BackupPath!, "my hand written mod", "main.lua")));
        Assert.False(Directory.Exists(temp.Combine("sync", "doomed")));
    }

    [Fact]
    public void Remove_RefusesWhenTheProfileIsActive()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);
        config.ActiveProfile = "doomed";
        temp.Dir("sync", "doomed");

        Assert.Throws<InvalidOperationException>(() => service.Remove(config, "doomed"));
        Assert.True(Directory.Exists(temp.Combine("sync", "doomed")));
    }

    [Fact]
    public void Remove_RefusesWhenTheProfileFolderIsItselfALink()
    {
        using var temp = new TempDir();
        var (service, config, _) = Build(temp);

        var real = temp.Dir("somewhere else");
        File.WriteAllText(Path.Combine(real, "main.lua"), "-- not ours to delete");
        new JunctionService().Create(temp.Combine("sync", "doomed"), real);

        Assert.Throws<UnsafePathException>(() => service.Remove(config, "doomed"));
        Assert.True(File.Exists(Path.Combine(real, "main.lua")));
    }
}
