namespace Claudin.Core;

/// <summary>Estado montado na inicialização do app.</summary>
public sealed class AppState
{
    private AppState(ConfigService configService, ConfigLoadResult load, IReadOnlyList<FolderResult> folders, LocateResult claude)
    {
        ConfigService = configService;
        LoadResult = load;
        Folders = folders;
        Claude = claude;
    }

    public ConfigService ConfigService { get; }

    public ConfigLoadResult LoadResult { get; }

    public AppConfig Config => LoadResult.Config;

    public IReadOnlyList<FolderResult> Folders { get; }

    public LocateResult Claude { get; }

    public static AppState Initialize(string configPath, IClaudeSystem system)
    {
        var service = new ConfigService(configPath);
        var load = service.Load();
        if (load.CreatedNew)
        {
            Log.Info(load.CorruptBackupPath is null
                ? $"Primeira execução: config criado em {service.ConfigPath}"
                : $"Config ilegível movido para {load.CorruptBackupPath}; recriado com os padrões");
        }
        foreach (var fix in load.Fixes)
        {
            Log.Warn($"Config corrigido: {fix}");
        }

        var folders = ConfigService.EnsureProfileFolders(load.Config);
        foreach (var f in folders)
        {
            if (f.Status == FolderStatus.Failed)
            {
                Log.Error($"Perfil '{f.Profile.Id}': não foi possível criar a pasta {f.Path}: {f.Error}");
            }
            else
            {
                Log.Info($"Perfil '{f.Profile.Id}': {f.Status} {f.Path}");
            }
        }

        var claude = new ClaudeInstallLocator(system).Locate(load.Config.ClaudeExecutableOverride);
        foreach (var note in claude.Notes)
        {
            Log.Warn($"Localização do Claude: {note}");
        }
        Log.Info(claude.Install is { } i
            ? $"Claude encontrado ({i.Source}, versão {i.Version ?? "?"}): {i.ExecutablePath}"
            : "Claude Desktop não encontrado");

        return new AppState(service, load, folders, claude);
    }
}
