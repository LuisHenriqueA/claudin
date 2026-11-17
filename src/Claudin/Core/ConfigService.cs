using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claudin.Core;

public sealed record ConfigLoadResult(AppConfig Config, bool CreatedNew, string? CorruptBackupPath, IReadOnlyList<string> Fixes);

public enum FolderStatus { Existing, Created, ClaudeDefault, Failed }

public sealed record FolderResult(Profile Profile, string? Path, FolderStatus Status, string? Error);

public sealed class ConfigService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ConfigService(string configPath)
    {
        ConfigPath = Path.GetFullPath(configPath);
    }

    public string ConfigPath { get; }

    /// <summary>
    /// Carrega o config. Na primeira execução cria o arquivo com os perfis padrão.
    /// Um arquivo ilegível é renomeado para config.json.corrupt-&lt;data&gt; e recriado.
    /// </summary>
    public ConfigLoadResult Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, CreatedNew: true, CorruptBackupPath: null, Fixes: []);
        }

        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
        }
        catch (JsonException)
        {
            config = null;
        }

        if (config is null)
        {
            var backup = $"{ConfigPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(ConfigPath, backup, overwrite: true);
            var fresh = AppConfig.CreateDefault();
            Save(fresh);
            return new ConfigLoadResult(fresh, CreatedNew: true, CorruptBackupPath: backup, Fixes: []);
        }

        var fixes = config.Normalize();
        if (fixes.Count > 0)
        {
            Save(config);
        }
        return new ConfigLoadResult(config, CreatedNew: false, CorruptBackupPath: null, Fixes: fixes);
    }

    /// <summary>Grava de forma atômica: escreve num .tmp e substitui o arquivo.</summary>
    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Cria as pastas de dados dos perfis. A pasta padrão do Claude (%APPDATA%\Claude) nunca é criada nem tocada:
    /// ela pertence ao Claude.
    /// </summary>
    public static IReadOnlyList<FolderResult> EnsureProfileFolders(AppConfig config)
    {
        var results = new List<FolderResult>();
        foreach (var profile in config.Profiles)
        {
            string? path = null;
            try
            {
                path = profile.ResolveDataDir(config.ProfilesRoot);
                if (profile.UsesClaudeDefaultDataDir(config.ProfilesRoot))
                {
                    results.Add(new FolderResult(profile, path, FolderStatus.ClaudeDefault, null));
                }
                else if (Directory.Exists(path))
                {
                    results.Add(new FolderResult(profile, path, FolderStatus.Existing, null));
                }
                else
                {
                    Directory.CreateDirectory(path);
                    results.Add(new FolderResult(profile, path, FolderStatus.Created, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results.Add(new FolderResult(profile, path, FolderStatus.Failed, ex.Message));
            }
        }
        return results;
    }
}
