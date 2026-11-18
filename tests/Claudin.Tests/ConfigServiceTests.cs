using System.Text.Json;
using Claudin.Core;

namespace Claudin.Tests;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private ConfigService NewService() => new(_tmp.Combine("sub", "config.json"));

    [Fact]
    public void Load_FirstRun_CreatesFileWithDefaultProfiles()
    {
        var service = NewService();

        var result = service.Load();

        Assert.True(result.CreatedNew);
        Assert.Null(result.CorruptBackupPath);
        Assert.True(File.Exists(service.ConfigPath));
        Assert.Equal(["x", "y"], result.Config.Profiles.Select(p => p.Id));
        Assert.Equal("x", result.Config.ActiveProfileId);
        Assert.Equal(@"%APPDATA%\Claude", result.Config.Profiles[0].DataDir);
        Assert.Null(result.Config.Profiles[1].DataDir);
        Assert.Equal(AppConfig.DefaultProfilesRoot, result.Config.ProfilesRoot);
    }

    [Fact]
    public void Load_FirstRun_WritesCamelCaseJsonWithoutNulls()
    {
        var service = NewService();
        service.Load();

        var json = File.ReadAllText(service.ConfigPath);

        Assert.Contains("\"profilesRoot\"", json);
        Assert.Contains("\"activeProfileId\"", json);
        Assert.DoesNotContain("claudeExecutableOverride", json);
        Assert.DoesNotContain("activeProfile\"", json); // propriedade calculada não é gravada
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var service = NewService();
        var config = service.Load().Config;
        config.Profiles[1].Name = "Trabalho";
        config.Profiles[1].Color = "#112233";
        config.ActiveProfileId = "y";
        config.ProfilesRoot = @"D:\Perfis";
        config.ClaudeExecutableOverride = @"C:\Claude\claude.exe";

        service.Save(config);
        var reloaded = NewService().Load();

        Assert.False(reloaded.CreatedNew);
        Assert.Empty(reloaded.Fixes);
        Assert.Equal("Trabalho", reloaded.Config.Profiles[1].Name);
        Assert.Equal("#112233", reloaded.Config.Profiles[1].Color);
        Assert.Equal("y", reloaded.Config.ActiveProfileId);
        Assert.Equal(@"D:\Perfis", reloaded.Config.ProfilesRoot);
        Assert.Equal(@"C:\Claude\claude.exe", reloaded.Config.ClaudeExecutableOverride);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        var service = NewService();
        service.Save(AppConfig.CreateDefault());

        Assert.False(File.Exists(service.ConfigPath + ".tmp"));
        Assert.True(File.Exists(service.ConfigPath));
    }

    [Theory]
    [InlineData("{ isto não é json")]
    [InlineData("null")]
    [InlineData("")]
    public void Load_CorruptFile_BacksUpAndRecreatesDefaults(string content)
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.ConfigPath)!);
        File.WriteAllText(service.ConfigPath, content);

        var result = service.Load();

        Assert.True(result.CreatedNew);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.Equal(content, File.ReadAllText(result.CorruptBackupPath!));
        Assert.Equal(["x", "y"], result.Config.Profiles.Select(p => p.Id));
        Assert.Equal("x", JsonDocument.Parse(File.ReadAllText(service.ConfigPath)).RootElement.GetProperty("activeProfileId").GetString());
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.ConfigPath)!);
        File.WriteAllText(service.ConfigPath, """
            {
              // comentário do usuário
              "profilesRoot": "C:\\P",
              "activeProfileId": "a",
              "profiles": [ { "id": "a", "name": "A", "color": "#000000" }, ],
            }
            """);

        var result = service.Load();

        Assert.False(result.CreatedNew);
        Assert.Empty(result.Fixes);
        Assert.Equal("A", result.Config.Profiles.Single().Name);
        // Configurações antigas, sem o campo, passam a esconder os botões do Claude.
        Assert.True(result.Config.HideClaudeCaptionButtons);
        Assert.True(result.Config.HideClaudeTaskbarButtons);
        Assert.True(result.Config.ShowProfileFrame);
    }

    [Fact]
    public void Load_InvalidValues_AreNormalizedAndSaved()
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.ConfigPath)!);
        File.WriteAllText(service.ConfigPath, """
            {
              "profilesRoot": "",
              "activeProfileId": "nao-existe",
              "claudeExecutableOverride": "   ",
              "profiles": [
                { "id": "x", "name": "X", "color": "laranja" },
                { "id": "X", "name": "Duplicado" },
                { "id": "com espaço", "name": "Inválido" },
                { "id": "y", "name": "" }
              ]
            }
            """);

        var result = service.Load();
        var config = result.Config;

        Assert.NotEmpty(result.Fixes);
        Assert.Equal(AppConfig.DefaultProfilesRoot, config.ProfilesRoot);
        Assert.Null(config.ClaudeExecutableOverride);
        Assert.Equal(["x", "y"], config.Profiles.Select(p => p.Id));
        Assert.Equal(AppConfig.FallbackColor, config.Profiles[0].Color);
        Assert.Equal("y", config.Profiles[1].Name);
        Assert.Equal("x", config.ActiveProfileId);

        var reloaded = NewService().Load();
        Assert.Empty(reloaded.Fixes);
    }

    [Fact]
    public void Load_NoProfiles_RecreatesDefaults()
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.ConfigPath)!);
        File.WriteAllText(service.ConfigPath, """{ "profiles": [] }""");

        var config = service.Load().Config;

        Assert.Equal(["x", "y"], config.Profiles.Select(p => p.Id));
        Assert.Equal("x", config.ActiveProfileId);
    }

    [Fact]
    public void EnsureProfileFolders_CreatesMissing_KeepsExisting_SkipsClaudeDefault()
    {
        var root = _tmp.Combine("perfis");
        var existing = _tmp.Combine("ja-existe");
        Directory.CreateDirectory(existing);
        var config = new AppConfig
        {
            ProfilesRoot = root,
            Profiles =
            [
                new Profile { Id = "x", Name = "X", DataDir = @"%APPDATA%\Claude" },
                new Profile { Id = "y", Name = "Y" },
                new Profile { Id = "z", Name = "Z", DataDir = existing },
            ],
        };

        var results = ConfigService.EnsureProfileFolders(config);

        Assert.Equal(FolderStatus.ClaudeDefault, results[0].Status);
        Assert.Equal(FolderStatus.Created, results[1].Status);
        Assert.True(Directory.Exists(Path.Combine(root, "y")));
        Assert.Equal(FolderStatus.Existing, results[2].Status);

        var again = ConfigService.EnsureProfileFolders(config);
        Assert.Equal(FolderStatus.Existing, again[1].Status);
    }

    [Fact]
    public void EnsureProfileFolders_UncreatablePath_ReportsFailureWithoutThrowing()
    {
        var file = _tmp.Combine("sou-um-arquivo.txt");
        File.WriteAllText(file, "x");
        var config = new AppConfig
        {
            ProfilesRoot = _tmp.Path,
            Profiles = [new Profile { Id = "q", Name = "Q", DataDir = Path.Combine(file, "perfil") }],
        };

        var result = Assert.Single(ConfigService.EnsureProfileFolders(config));

        Assert.Equal(FolderStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }
}
