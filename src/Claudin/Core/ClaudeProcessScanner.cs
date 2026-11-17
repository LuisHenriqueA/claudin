namespace Claudin.Core;

/// <summary>Processo principal (MAIN do Electron) de uma instância do Claude Desktop e a pasta de dados que ele usa.</summary>
public sealed record ClaudeMainProcess(int Pid, string DataDir);

/// <summary>
/// Encontra as instâncias do Claude Desktop em execução e a pasta de dados de cada uma (Fase 0, seção 1):
/// - só conta processos claude.exe da instalação do Claude Desktop (exclui o Claude Code, que também se chama claude.exe);
/// - o MAIN é o processo sem --type=;
/// - a pasta vem do --user-data-dir dos filhos (valor já resolvido pelo Electron), depois do próprio MAIN;
///   sem nenhum dos dois, é a pasta padrão (%APPDATA%\Claude), caso da instância aberta pelo Windows.
/// </summary>
public sealed class ClaudeProcessScanner(IProcessInspector inspector, string defaultDataDir, IEnumerable<string>? extraExecutables = null)
{
    private readonly string[] _extraExecutables = (extraExecutables ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

    /// <summary>Pasta que o Claude usa quando iniciado sem --user-data-dir.</summary>
    public string DefaultDataDir { get; } = defaultDataDir;

    public IReadOnlyList<ClaudeMainProcess> FindMainProcesses()
    {
        var mains = new List<int>();
        var childDataDirs = new Dictionary<int, string>();
        var ownDataDirs = new Dictionary<int, string>();

        foreach (var entry in inspector.Snapshot())
        {
            if (!string.Equals(entry.ExeName, "claude.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var image = inspector.GetImagePath(entry.Pid);
            if (image is null || !IsClaudeDesktopExecutable(image))
            {
                continue;
            }
            var args = CommandLine.Split(inspector.GetCommandLine(entry.Pid));
            var dataDir = CommandLine.GetSwitchValue(args, CommandLine.UserDataDirSwitch);
            if (CommandLine.HasSwitch(args, CommandLine.TypeSwitch))
            {
                if (!string.IsNullOrWhiteSpace(dataDir))
                {
                    childDataDirs.TryAdd(entry.ParentPid, dataDir);
                }
            }
            else
            {
                mains.Add(entry.Pid);
                if (!string.IsNullOrWhiteSpace(dataDir))
                {
                    ownDataDirs[entry.Pid] = dataDir;
                }
            }
        }

        var result = new List<ClaudeMainProcess>();
        foreach (var pid in mains)
        {
            var raw = childDataDirs.GetValueOrDefault(pid) ?? ownDataDirs.GetValueOrDefault(pid) ?? DefaultDataDir;
            try
            {
                result.Add(new ClaudeMainProcess(pid, PathUtil.Normalize(raw)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Caminho ilegível: ignora este processo.
            }
        }
        return result;
    }

    public ClaudeMainProcess? FindByDataDir(string dataDir) =>
        FindMainProcesses().FirstOrDefault(m => PathUtil.AreSame(m.DataDir, dataDir));

    public bool IsClaudeDesktopExecutable(string imagePath)
    {
        if (!string.Equals(Path.GetFileName(imagePath), "claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (imagePath.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
            || imagePath.Contains(@"\AnthropicClaude\app-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return _extraExecutables.Any(p => string.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase));
    }
}
