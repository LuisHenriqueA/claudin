using System.Text.Json;
using Claudin.Core;

namespace Claudin.Tests;

public sealed class LogWriterTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private DateTime _now = new(2026, 11, 10, 12, 0, 0);

    public void Dispose() => _tmp.Dispose();

    private LogWriter Writer(long maxBytes = 1000, int retention = 14) => new(_tmp.Path, maxBytes, retention, () => _now);

    [Fact]
    public void WritesDailyFile()
    {
        var w = Writer();
        w.Write("[INFO] olá");

        var file = Path.Combine(_tmp.Path, "claudin-20261110.log");
        Assert.Equal(file, w.CurrentPath);
        Assert.Contains("[INFO] olá", File.ReadAllText(file));
    }

    [Fact]
    public void RollsToNewFile_WhenSizeLimitReached()
    {
        var w = Writer(maxBytes: 200);
        for (var i = 0; i < 10; i++)
        {
            w.Write($"[INFO] linha {i} " + new string('x', 40));
        }

        var files = Directory.GetFiles(_tmp.Path, "claudin-20261110*.log").Select(Path.GetFileName).Order().ToList();
        Assert.True(files.Count >= 3);
        Assert.Contains("claudin-20261110-1.log", files);
        Assert.All(Directory.GetFiles(_tmp.Path), f => Assert.True(new FileInfo(f).Length <= 200));
    }

    [Fact]
    public void RollsToNewFile_WhenDayChanges()
    {
        var w = Writer();
        w.Write("a");
        _now = _now.AddDays(1);
        w.Write("b");

        Assert.Equal(Path.Combine(_tmp.Path, "claudin-20261111.log"), w.CurrentPath);
        Assert.Equal(2, Directory.GetFiles(_tmp.Path).Length);
    }

    [Fact]
    public void DeletesFilesOlderThanRetention_KeepsOthers()
    {
        File.WriteAllText(Path.Combine(_tmp.Path, "claudin-20261020.log"), "velho");
        File.WriteAllText(Path.Combine(_tmp.Path, "claudin-20261020-1.log"), "velho");
        File.WriteAllText(Path.Combine(_tmp.Path, "claudin-20261101.log"), "recente");
        File.WriteAllText(Path.Combine(_tmp.Path, "outro.log"), "não é do Claudin");

        _ = Writer(retention: 14);

        var names = Directory.GetFiles(_tmp.Path).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["claudin-20261101.log", "outro.log"], names);
    }

    [Fact]
    public void ContinuesExistingFileAfterRestart()
    {
        Writer().Write("primeira execução");
        var w2 = Writer();
        w2.Write("segunda execução");

        var text = File.ReadAllText(Path.Combine(_tmp.Path, "claudin-20261110.log"));
        Assert.Contains("primeira", text);
        Assert.Contains("segunda", text);
    }
}

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private sealed class FakeStore : IRegistryValueStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Location => @"HKCU\Fake\Run";

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string value) => Values[name] = value;

        public void Delete(string name) => Values.Remove(name);
    }

    private string BackupFile => _tmp.Combine("registry-backup.json");

    [Fact]
    public void Enable_SavesBackupOfAbsence_ThenRestoreRemovesValue()
    {
        var store = new FakeStore();
        var reg = new StartupRegistration(store, BackupFile);

        reg.Enable(@"C:\Apps\Claudin.exe");

        Assert.Equal(@"""C:\Apps\Claudin.exe"" --background", store.Values["Claudin"]);
        Assert.True(reg.IsEnabled(@"C:\Apps\Claudin.exe"));
        Assert.False(reg.IsEnabled(@"D:\outro\Claudin.exe"));
        var backup = JsonSerializer.Deserialize<RegistryBackupEntry>(File.ReadAllText(BackupFile), ConfigService.JsonOptions)!;
        Assert.False(backup.Existed);

        Assert.True(reg.RestoreOriginal());
        Assert.False(store.Values.ContainsKey("Claudin"));
        Assert.False(reg.HasBackup);
    }

    [Fact]
    public void Backup_KeepsTheValueThatExistedBeforeClaudin()
    {
        var store = new FakeStore();
        store.Values["Claudin"] = @"""C:\Velho\Claudin.exe""";
        var reg = new StartupRegistration(store, BackupFile);

        reg.Enable(@"C:\Novo\Claudin.exe");
        reg.Disable();
        reg.Enable(@"C:\Novo2\Claudin.exe"); // o backup não é sobrescrito

        Assert.True(reg.RestoreOriginal());
        Assert.Equal(@"""C:\Velho\Claudin.exe""", store.Values["Claudin"]);
    }

    [Fact]
    public void Restore_WithoutBackup_DoesNothing()
    {
        var store = new FakeStore();
        store.Values["Outro"] = "x";

        Assert.False(new StartupRegistration(store, BackupFile).RestoreOriginal());
        Assert.Equal("x", store.Values["Outro"]);
    }

    [Fact]
    public void Disable_RemovesValue_AndOtherValuesStayUntouched()
    {
        var store = new FakeStore();
        store.Values["OneDrive"] = "onedrive.exe";
        var reg = new StartupRegistration(store, BackupFile);
        reg.Enable(@"C:\A\Claudin.exe");

        reg.Disable();

        Assert.False(store.Values.ContainsKey("Claudin"));
        Assert.Equal("onedrive.exe", store.Values["OneDrive"]);
    }
}

public sealed class SettingsValidationTests
{
    private static readonly ProfileEdit[] Valid =
    [
        new("x", "Conta X", "#D97757"),
        new("y", "Conta Y", "#4A8FE7"),
    ];

    [Fact]
    public void ValidInput_HasNoErrors()
    {
        Assert.Empty(SettingsValidation.Validate(@"C:\Claudin\perfis", null, Valid, _ => false));
        Assert.Empty(SettingsValidation.Validate(@"%LOCALAPPDATA%\Perfis", @"C:\C\claude.exe", Valid, _ => true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("perfis")]
    [InlineData(@"\sem\unidade")]
    public void Root_MustBeFullPath(string root)
    {
        Assert.NotEmpty(SettingsValidation.Validate(root, null, Valid, _ => true));
    }

    [Fact]
    public void ClaudeOverride_MustBeExistingExe()
    {
        Assert.NotEmpty(SettingsValidation.Validate(@"C:\P", @"C:\nao\existe.exe", Valid, _ => false));
        Assert.NotEmpty(SettingsValidation.Validate(@"C:\P", @"C:\pasta", Valid, _ => true));
    }

    [Fact]
    public void Profiles_NeedNameUniqueAndValidColor()
    {
        var errors = SettingsValidation.Validate(@"C:\P", null,
        [
            new("x", "  ", "#D97757"),
            new("y", "Mesma", "azul"),
            new("z", "mesma", "#123456"),
            new("w", new string('a', 41), "#123456"),
        ], _ => true);

        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void ValidateName_ForRenameFromTab()
    {
        string[] others = ["Trabalho"];

        Assert.Null(SettingsValidation.ValidateName("y", "  Pessoal ", others));
        Assert.NotNull(SettingsValidation.ValidateName("y", " ", others));
        Assert.NotNull(SettingsValidation.ValidateName("y", "trabalho", others));
        Assert.NotNull(SettingsValidation.ValidateName("y", new string('a', SettingsValidation.MaxNameLength + 1), others));
    }
}

public sealed class ConfigPhase5Tests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void NewSettings_HaveDefaults_AndRoundTrip()
    {
        var service = new ConfigService(_tmp.Combine("config.json"));
        var config = service.Load().Config;

        Assert.True(config.OpenActiveProfileOnStart);
        Assert.False(config.CloseToTray);
        Assert.False(config.StartWithWindows);
        Assert.Null(config.Window);

        config.CloseToTray = true;
        config.Window = new WindowBounds { Left = 10, Top = 20, Right = 1210, Bottom = 820, Maximized = true };
        service.Save(config);
        var again = new ConfigService(_tmp.Combine("config.json")).Load().Config;

        Assert.True(again.CloseToTray);
        Assert.Equal(1210, again.Window!.Right);
        Assert.True(again.Window.Maximized);
    }

    [Fact]
    public void OldConfigWithoutNewFields_KeepsDefaults()
    {
        File.WriteAllText(_tmp.Combine("config.json"), """{ "activeProfileId": "x", "profiles": [ { "id": "x", "name": "X", "color": "#000000" } ] }""");

        var config = new ConfigService(_tmp.Combine("config.json")).Load().Config;

        Assert.True(config.OpenActiveProfileOnStart);
        Assert.False(config.CloseToTray);
    }

    [Fact]
    public void ImplausibleWindow_IsDiscarded()
    {
        File.WriteAllText(_tmp.Combine("config.json"),
            """{ "profiles": [ { "id": "x", "name": "X", "color": "#000000" } ], "window": { "left": 0, "top": 0, "right": 10, "bottom": 10 } }""");

        var result = new ConfigService(_tmp.Combine("config.json")).Load();

        Assert.Null(result.Config.Window);
        Assert.Contains(result.Fixes, f => f.Contains("janela"));
    }
}
