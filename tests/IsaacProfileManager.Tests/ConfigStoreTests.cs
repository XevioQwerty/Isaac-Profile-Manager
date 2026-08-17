using System.Text;
using IsaacProfileManager.Core.Models;
using IsaacProfileManager.Core.Storage;
using Xunit;

namespace IsaacProfileManager.Tests;

public class ConfigStoreTests
{
    /// <summary>Exactly what IsaacProfiles.ps1 writes, spacing included.</summary>
    private const string PowerShellWrittenConfig = """
        {
            "ConfigVersion":  3,
            "IsaacExe":  "A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth\\isaac-ng.exe",
            "GameDir":  "A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth",
            "ModsDir":  "A:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth\\mods",
            "SyncRoot":  "D:\\.code\\IsaacSync",
            "Profiles":  [ "Vanilla+_v1.0", "RPTG_v1.0" ],
            "ActiveProfile":  "Vanilla+_v1.0",
            "UseRepentogon":  [ "RPTG_v1.0" ],
            "PerProfileBuild":  true,
            "LauncherExe":  "A:\\path\\REPENTOGONLauncher.exe",
            "OwnsOnSteam":  true,
            "ShortcutDirs":  [ "C:\\Users\\xevio\\Desktop" ],
            "SetupDate":  "2026-08-12T03:19:49.8377930-04:00"
        }
        """;

    [Fact]
    public void Load_ReadsAConfigWrittenByThePowerShellScript()
    {
        using var temp = new TempDir();
        var store = new ConfigStore(temp.File(ConfigStore.FileName, PowerShellWrittenConfig));

        var config = store.Load();

        Assert.Equal(3, config.ConfigVersion);
        Assert.Equal(@"D:\.code\IsaacSync", config.SyncRoot);
        Assert.Equal(new[] { "Vanilla+_v1.0", "RPTG_v1.0" }, config.Profiles);
        Assert.Equal("Vanilla+_v1.0", config.ActiveProfile);
        Assert.True(config.PerProfileBuild);
        Assert.Contains("RPTG_v1.0", config.UseRepentogon);
    }

    [Fact]
    public void Load_StripsAUtf8BomWrittenByPowerShell5()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, ConfigStore.FileName);
        // Set-Content -Encoding UTF8 on PS 5.1 emits a BOM; JSON parsers choke on it.
        File.WriteAllText(path, PowerShellWrittenConfig, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var config = new ConfigStore(path).Load();

        Assert.Equal(3, config.ConfigVersion);
    }

    [Fact]
    public void Load_RefusesAnUnknownSchemaRatherThanFallingBack()
    {
        using var temp = new TempDir();
        var store = new ConfigStore(temp.File(ConfigStore.FileName, """{ "ConfigVersion": 2, "SyncRoot": "X:\\old" }"""));

        var ex = Assert.Throws<ConfigSchemaException>(() => store.Load());

        Assert.Contains("Setup.bat", ex.Message);
    }

    [Fact]
    public void Load_RefusesUnparseableJson()
    {
        using var temp = new TempDir();
        var store = new ConfigStore(temp.File(ConfigStore.FileName, "{ this is not json"));

        Assert.Throws<ConfigSchemaException>(() => store.Load());
    }

    [Fact]
    public void SaveThenLoad_KeepsKeysThisBuildDoesNotKnowAbout()
    {
        using var temp = new TempDir();
        var path = temp.File(ConfigStore.FileName, """
            {
              "ConfigVersion": 3,
              "SyncRoot": "D:\\sync",
              "Profiles": ["a"],
              "SomeFutureKey": { "nested": [1, 2, 3] }
            }
            """);
        var store = new ConfigStore(path);

        var config = store.Load();
        config.ActiveProfile = "a";
        store.Save(config);

        var text = File.ReadAllText(path);
        Assert.Contains("SomeFutureKey", text);
        Assert.Contains("nested", text);
        Assert.Contains("\"ActiveProfile\": \"a\"", text);
    }

    [Fact]
    public void Save_DoesNotEscapeBackslashesIntoUnicodeSequences()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, ConfigStore.FileName);
        var store = new ConfigStore(path);

        store.Save(new AppConfig { SyncRoot = @"D:\.code\IsaacSync", GameDir = @"A:\Steam\The Binding of Isaac Rebirth" });

        // PowerShell reads this file too; \u0027-style escaping of paths would
        // still parse, but makes the file unreadable when someone opens it.
        var text = File.ReadAllText(path);
        Assert.Contains(@"D:\\.code\\IsaacSync", text);
        Assert.DoesNotContain(@"\u005C", text);
    }

    [Fact]
    public void Save_WritesWithoutABomSoPowerShellAndDotnetBothParseIt()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, ConfigStore.FileName);
        new ConfigStore(path).Save(new AppConfig { SyncRoot = @"D:\sync" });

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public void Save_ReplacesTheFileWithoutLeavingATempBehind()
    {
        using var temp = new TempDir();
        var path = temp.File(ConfigStore.FileName, PowerShellWrittenConfig);
        var store = new ConfigStore(path);

        var config = store.Load();
        config.ActiveProfile = "RPTG_v1.0";
        store.Save(config);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("RPTG_v1.0", store.Load().ActiveProfile);
    }
}
