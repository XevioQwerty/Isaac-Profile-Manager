using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class LauncherIniServiceTests
{
    /// <summary>Copied from a real install, including its exact spacing.</summary>
    private const string RealIni = """
        [General]
        IsaacExecutable = A:\SteamLibrary\steamapps\common\The Binding of Isaac Rebirth\isaac-ng.exe
        RanWizard = 1
        HideWindow = 1
        StealthMode = 1
        [Repentogon]
        Console = 0
        Update = 0
        UnstableUpdates = 0
        [Vanilla]
        LuaHeapSize = 1024M
        LuaDebug = 0
        [Shared]
        LaunchMode = 0
        """;

    private static LauncherIniService ServiceFor(TempDir temp, string contents)
    {
        var path = temp.File("repentogon_launcher.ini", contents);
        return new LauncherIniService(path);
    }

    [Fact]
    public void Get_ReadsKeysFromTheCorrectSection()
    {
        using var temp = new TempDir();
        var ini = ServiceFor(temp, RealIni);

        Assert.Equal("1", ini.Get("General", "HideWindow"));
        Assert.Equal("0", ini.Get("Repentogon", "Console"));
        Assert.Equal("1024M", ini.Get("Vanilla", "LuaHeapSize"));
        Assert.Equal(LaunchMode.Vanilla, ini.GetLaunchMode());
    }

    [Fact]
    public void Get_DoesNotFindAKeyFromAnotherSection()
    {
        using var temp = new TempDir();
        var ini = ServiceFor(temp, RealIni);

        // LuaDebug lives in [Vanilla], not [Repentogon].
        Assert.Null(ini.Get("Repentogon", "LuaDebug"));
    }

    [Fact]
    public void SetLaunchMode_ChangesOnlyThatLineAndKeepsEveryOtherKey()
    {
        using var temp = new TempDir();
        var path = temp.File("repentogon_launcher.ini", RealIni);
        var ini = new LauncherIniService(path);

        Assert.True(ini.TrySetLaunchMode(LaunchMode.Repentogon));

        Assert.Equal(LaunchMode.Repentogon, ini.GetLaunchMode());
        var lines = File.ReadAllLines(path);
        Assert.Contains("IsaacExecutable = A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth\\isaac-ng.exe", lines);
        Assert.Contains("UnstableUpdates = 0", lines);
        Assert.Contains("LuaHeapSize = 1024M", lines);
        // Section order preserved: the launcher owns this file.
        Assert.Equal(new[] { "[General]", "[Repentogon]", "[Vanilla]", "[Shared]" },
                     lines.Where(l => l.TrimStart().StartsWith('[')).Select(l => l.Trim()).ToArray());
    }

    [Fact]
    public void Set_PreservesCommentsAndBlankLines()
    {
        using var temp = new TempDir();
        var path = temp.File("repentogon_launcher.ini", """
            ; written by the launcher, do not reorder
            [Shared]

            LaunchMode = 0
            ; trailing note
            """);
        var ini = new LauncherIniService(path);

        ini.TrySetLaunchMode(LaunchMode.Repentogon);

        var text = File.ReadAllText(path);
        Assert.Contains("; written by the launcher, do not reorder", text);
        Assert.Contains("; trailing note", text);
        Assert.Contains("LaunchMode = 1", text);
    }

    [Fact]
    public void Set_AddsTheKeyInsideItsSectionWhenAbsent()
    {
        using var temp = new TempDir();
        var path = temp.File("repentogon_launcher.ini", """
            [Shared]
            SomethingElse = 1
            [General]
            RanWizard = 1
            """);
        var ini = new LauncherIniService(path);

        ini.TrySetLaunchMode(LaunchMode.Repentogon);

        var lines = File.ReadAllLines(path);
        var sharedIndex = Array.FindIndex(lines, l => l.Trim() == "[Shared]");
        var generalIndex = Array.FindIndex(lines, l => l.Trim() == "[General]");
        var modeIndex = Array.FindIndex(lines, l => l.Trim().StartsWith("LaunchMode"));

        Assert.InRange(modeIndex, sharedIndex + 1, generalIndex - 1);
        Assert.Contains("RanWizard = 1", lines);
    }

    [Fact]
    public void Set_AddsTheSectionWhenTheFileHasNone()
    {
        using var temp = new TempDir();
        var path = temp.File("repentogon_launcher.ini", "[General]\nRanWizard = 1\n");
        var ini = new LauncherIniService(path);

        ini.TrySetLaunchMode(LaunchMode.Repentogon);

        var text = File.ReadAllText(path);
        Assert.Contains("[Shared]", text);
        Assert.Contains("LaunchMode = 1", text);
        Assert.Contains("RanWizard = 1", text);
    }

    [Fact]
    public void MissingIniIsReportedNotThrown()
    {
        using var temp = new TempDir();
        var ini = new LauncherIniService(temp.Combine("nope.ini"));

        Assert.False(ini.Exists);
        Assert.Null(ini.GetLaunchMode());
        Assert.False(ini.TrySetLaunchMode(LaunchMode.Repentogon));
    }
}
