using System.Windows;
using Claudin.Core;

namespace Claudin;

/// <summary>
/// "Claudin.exe --uninstall", chamado pelo desinstalador antes de remover os arquivos:
/// 1. restaura o registro ao estado anterior ao Claudin;
/// 2. pergunta se as pastas de perfil (logins dessas contas) e as configurações do Claudin devem ser apagadas.
///    A pasta padrão do Claude (%APPDATA%\Claude) nunca é apagada.
/// Com "--uninstall --quiet" (desinstalação silenciosa), só restaura o registro.
/// </summary>
internal static class UninstallFlow
{
    public const string Argument = "--uninstall";
    public const string QuietArgument = "--quiet";

    public static int Run(bool quiet)
    {
        try
        {
            var store = new RunKeyStore();
            var restored = new StartupRegistration(store, AppPaths.RegistryBackupFile).RestoreOriginal();
            // Sem backup (ex.: pasta do Claudin apagada à mão), só remove a entrada se ela for claramente do Claudin,
            // para não deixar o Windows tentando abrir um executável removido.
            if (!restored && store.Get("Claudin") is { } value
                && value.Contains("Claudin.exe", StringComparison.OrdinalIgnoreCase)
                && value.Contains(StartupRegistration.BackgroundArgument, StringComparison.OrdinalIgnoreCase))
            {
                store.Delete("Claudin");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Desinstalação: falha ao restaurar o registro", ex);
        }

        if (quiet)
        {
            return 0;
        }

        AppConfig? config = null;
        if (File.Exists(AppPaths.ConfigFile))
        {
            try
            {
                config = new ConfigService(AppPaths.ConfigFile).Load().Config;
            }
            catch (Exception ex)
            {
                Log.Error("Desinstalação: config ilegível", ex);
            }
        }

        var dirs = config is null
            ? []
            : UninstallCleanup.GetDeletableProfileDirs(config, ClaudeDefaults.DefaultDataDir, UninstallCleanup.DefaultProtectedDirs())
                .Where(Directory.Exists).ToList();
        var homeExists = Directory.Exists(AppPaths.Home);
        if (dirs.Count == 0 && !homeExists)
        {
            return 0;
        }

        var list = dirs.Count == 0 ? "" : "\n\nPastas de perfil:\n" + string.Join("\n", dirs.Select(d => "  • " + d));
        var answer = MessageBox.Show(
            "Apagar também os dados do Claudin?" + list +
            $"\n\nConfigurações e logs:\n  • {AppPaths.Home}" +
            "\n\nAs pastas de perfil guardam os logins e os dados do Claude dessas contas. " +
            $"A pasta padrão do Claude ({ClaudeDefaults.DefaultDataDir}) não é tocada." +
            "\n\nSe escolher Não, tudo fica guardado e volta a funcionar se você reinstalar o Claudin.",
            "Desinstalar o Claudin", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            Log.Info("Desinstalação: dados mantidos");
            return 0;
        }

        if (!CloseInstancesUsing(dirs))
        {
            return 0;
        }

        var failures = new List<string>();
        foreach (var dir in dirs)
        {
            TryDelete(dir, failures);
        }
        if (config is not null)
        {
            TryDeleteIfEmpty(Environment.ExpandEnvironmentVariables(config.ProfilesRoot));
        }
        Log.Info("Desinstalação: dados apagados");
        Log.Disable(); // os logs ficam dentro da pasta apagada a seguir
        TryDelete(AppPaths.Home, failures);

        if (failures.Count > 0)
        {
            MessageBox.Show("Algumas pastas não puderam ser apagadas (talvez estejam em uso):\n\n" + string.Join("\n", failures),
                "Desinstalar o Claudin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return 0;
    }

    /// <summary>Instâncias do Claude usando as pastas a apagar precisam fechar antes. False = usuário desistiu de apagar.</summary>
    private static bool CloseInstancesUsing(IReadOnlyList<string> dirs)
    {
        List<ClaudeMainProcess> running;
        try
        {
            running = new ClaudeProcessScanner(new WindowsProcessInspector(), ClaudeDefaults.DefaultDataDir)
                .FindMainProcesses()
                .Where(m => dirs.Any(d => PathUtil.AreSame(d, m.DataDir)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Desinstalação: falha ao listar o Claude", ex);
            return true;
        }
        if (running.Count == 0)
        {
            return true;
        }

        var answer = MessageBox.Show(
            "O Claude está aberto usando estas pastas:\n\n" + string.Join("\n", running.Select(r => "  • " + r.DataDir)) +
            "\n\nEncerrar essas instâncias à força e apagar? (Não = manter as pastas)",
            "Desinstalar o Claudin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }
        var host = new WindowsProcessHost();
        foreach (var r in running)
        {
            using var handle = host.Open(r.Pid);
            if (handle is null)
            {
                continue;
            }
            handle.KillTree();
            for (var i = 0; i < 50 && !handle.HasExited; i++)
            {
                Thread.Sleep(100);
            }
        }
        return true;
    }

    private static void TryDelete(string dir, List<string> failures)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{dir} ({ex.Message})");
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()
                && UninstallCleanup.IsSafeToDelete(dir, ClaudeDefaults.DefaultDataDir, UninstallCleanup.DefaultProtectedDirs()))
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }
}
