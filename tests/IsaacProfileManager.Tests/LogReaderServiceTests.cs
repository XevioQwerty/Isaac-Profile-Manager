using IsaacProfileManager.Core.Services;
using Xunit;

namespace IsaacProfileManager.Tests;

public class LogReaderServiceTests
{
    /// <summary>Shaped like the real log, including the two-line Command Line block.</summary>
    private const string RealisticLog = """
        [INFO] - timeBeginPeriod( 1 )
        [INFO] - OpenGL version 4.6.0 NVIDIA 610.47
        [ERROR] - Unkown device type encountered! name = "Guitar Hero3", vendor = 4794
        [INFO] - Command Line:
        [INFO] - 	--repentogonoff
        [INFO] - Game Version: J460
        [INFO] - LOADED MOD a:\steamlibrary\steamapps\common\the binding of isaac rebirth/mods/astro-items_3260980911/content/
        [INFO] - LOADED MOD a:\steamlibrary\steamapps\common\the binding of isaac rebirth/mods/minimapi/content/
        [ASSERT] - something mildly wrong
        [INFO] - Running Lua Script: A:\Games/mods/minimapi/main.lua
        [INFO] - Lua Debug: IPM_JUNCTION_TEST_OK loaded through a per-mod junction
        [INFO] - Checksums:
        [INFO] -  - Player0: Checksum (1000fc7b), Global RNG checksum (f4d2dca3)
        """;

    private static LogReaderService ServiceFor(TempDir temp, string contents = RealisticLog) =>
        new(temp.File("log.txt", contents));

    [Fact]
    public void Read_SplitsSeverityFromTextAndNumbersTheLines()
    {
        using var temp = new TempDir();
        var lines = ServiceFor(temp).Read();

        Assert.Equal(13, lines.Count);
        Assert.Equal(1, lines[0].Number);
        Assert.Equal(LogSeverity.Info, lines[0].Severity);
        Assert.Equal("timeBeginPeriod( 1 )", lines[0].Text);
        Assert.Equal(LogSeverity.Error, lines[2].Severity);
        Assert.Single(lines, l => l.Severity == LogSeverity.Assert);
    }

    [Fact]
    public void Read_CategorisesTheLinesWorthJumpingTo()
    {
        using var temp = new TempDir();
        var lines = ServiceFor(temp).Read();

        Assert.Equal(2, lines.Count(l => l.Category.HasFlag(LogCategory.ModLoaded)));
        Assert.Single(lines, l => l.Category.HasFlag(LogCategory.LuaDebug));
        Assert.Single(lines, l => l.Category.HasFlag(LogCategory.Version));
        Assert.Equal(2, lines.Count(l => l.Category.HasFlag(LogCategory.Checksum)));
    }

    [Fact]
    public void Summarise_PullsOutWhatYouWouldOtherwiseScrollToFind()
    {
        using var temp = new TempDir();
        var service = ServiceFor(temp);

        var summary = service.Summarise(service.Read());

        Assert.Equal("J460", summary.GameVersion);
        Assert.Equal("--repentogonoff", summary.CommandLine);
        Assert.True(summary.LooksLikeVanilla);
        Assert.Equal(2, summary.ModsLoaded);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Asserts);
        Assert.True(summary.HasChecksums);
    }

    [Fact]
    public void Summarise_RecognisesARepentogonRunByTheAbsentFlag()
    {
        using var temp = new TempDir();
        var service = ServiceFor(temp, """
            [INFO] - Command Line:
            [INFO] - Game Version: J273
            """);

        var summary = service.Summarise(service.Read());

        Assert.Equal("(none)", summary.CommandLine);
        Assert.False(summary.LooksLikeVanilla);
        Assert.Equal("J273", summary.GameVersion);
    }

    [Fact]
    public void LoadedMods_NamesTheFolderUnderModsInLoadOrder()
    {
        using var temp = new TempDir();
        var service = ServiceFor(temp);

        var mods = service.LoadedMods(service.Read());

        // The folder under mods\ is what identifies the mod — not the content dir.
        Assert.Equal(new[] { "astro-items_3260980911", "minimapi" }, mods);
    }

    [Fact]
    public void Read_WorksWhileAnotherProcessHoldsTheFileOpenForWriting()
    {
        using var temp = new TempDir();
        var path = temp.File("log.txt", RealisticLog);

        // Isaac keeps log.txt open while it runs; a stricter open would throw.
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal(13, new LogReaderService(path).Read().Count);
    }

    [Fact]
    public void Read_HonoursTheLineCap()
    {
        using var temp = new TempDir();
        var service = ServiceFor(temp, string.Join("\n", Enumerable.Repeat("[INFO] - noise", 5000)));

        Assert.Equal(100, service.Read(maxLines: 100).Count);
    }

    [Fact]
    public void MissingLogIsReportedNotThrown()
    {
        using var temp = new TempDir();
        var service = new LogReaderService(temp.Combine("no-log-here.txt"));

        Assert.False(service.Exists);
        Assert.Empty(service.Read());
        Assert.Equal(0, service.Summarise(service.Read()).TotalLines);
    }

    [Fact]
    public void UntaggedLinesSurviveAsInfo()
    {
        using var temp = new TempDir();
        var service = ServiceFor(temp, "a bare line with no tag\n[INFO] - tagged");

        var lines = service.Read();

        Assert.Equal("a bare line with no tag", lines[0].Text);
        Assert.Equal(LogSeverity.Info, lines[0].Severity);
    }

    [Fact]
    public void SaveTransport_ReadsWhereTheGameSaidItSaves()
    {
        using var temp = new TempDir();
        var log = RealisticLog + "\n[INFO] - Loading PersistentGameData from Steam Cloud: rep+persistentgamedata1.dat.\n";
        var lines = ServiceFor(temp, log).Read();

        Assert.Equal("Steam Cloud", LogReaderService.SaveTransport(lines));
        Assert.Null(LogReaderService.SaveTransport(ServiceFor(temp).Read()));
    }

    [Fact]
    public void ReadGameVersion_StopsAtTheVersionLine_AndIsNullWithoutOne()
    {
        using var temp = new TempDir();
        Assert.Equal("J460", ServiceFor(temp).ReadGameVersion());
        Assert.Null(new LogReaderService(temp.File("empty.txt", "[INFO] - nothing here\n")).ReadGameVersion());
        Assert.Null(new LogReaderService(temp.Combine("missing.txt")).ReadGameVersion());
    }

    [Fact]
    public void ReadRun_TellsWhichBuildWroteTheLog()
    {
        using var temp = new TempDir();
        var rgon = temp.File("rgon.txt", """
            [INFO] - Command Line:
            [INFO] - 	A:\Games\The Binding of Isaac Rebirth\Repentogon\isaac-ng.exe
            [INFO] - 	--luaheapsize=1024M
            [INFO] - Game Version: J273
            """);
        var vanilla = temp.File("vanilla.txt", """
            [INFO] - Command Line:
            [INFO] - 	A:\Games\The Binding of Isaac Rebirth\isaac-ng.exe
            [INFO] - 	--repentogonoff
            [INFO] - Game Version: J460
            """);
        var plain = temp.File("plain.txt", """
            [INFO] - Command Line:
            [INFO] - 	C:\Steam\isaac-ng.exe
            [INFO] - Game Version: J460
            """);

        Assert.Equal(new LogReaderService.LogRun("J273", IsaacProfileManager.Core.Models.GameBuild.Repentogon), LogReaderService.ReadRun(rgon));
        Assert.Equal(new LogReaderService.LogRun("J460", IsaacProfileManager.Core.Models.GameBuild.Vanilla), LogReaderService.ReadRun(vanilla));
        Assert.Equal(new LogReaderService.LogRun("J460", IsaacProfileManager.Core.Models.GameBuild.Vanilla), LogReaderService.ReadRun(plain));
        Assert.Equal(LogReaderService.LogRun.None, LogReaderService.ReadRun(temp.Combine("missing.txt")));
    }
}
