using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Claudin.Core;
using Claudin.Hosting;

namespace Claudin;

public partial class App : Application
{
    /// <summary>Restaura o registro ao estado anterior ao Claudin e sai (usado pelo desinstalador).</summary>
    public const string RestoreRegistryArgument = "--restore-registry";

    private InstanceManager? _instances;
    private SingleInstance? _single;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private Mutex? _appMutex;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init(AppPaths.LogsDir);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Log.Info($"Claudin {version} iniciando");

        if (e.Args.Contains(RestoreRegistryArgument, StringComparer.OrdinalIgnoreCase))
        {
            var restored = new StartupRegistration(new RunKeyStore(), AppPaths.RegistryBackupFile).RestoreOriginal();
            Log.Info(restored ? "Registro restaurado (linha de comando)" : "Nada a restaurar no registro (linha de comando)");
            Shutdown(0);
            return;
        }

        if (e.Args.Contains(UninstallFlow.Argument, StringComparer.OrdinalIgnoreCase))
        {
            Log.Info("Desinstalação iniciada");
            Shutdown(UninstallFlow.Run(e.Args.Contains(UninstallFlow.QuietArgument, StringComparer.OrdinalIgnoreCase)));
            return;
        }

        // Marca "Claudin em execução" para o instalador/desinstalador (AppMutex do Inno Setup).
        _appMutex = new Mutex(false, "Claudin.AppMutex");

        var instanceName = SingleInstance.NameFor(AppPaths.Home);
        _single = SingleInstance.TryAcquire(instanceName);
        if (_single is null)
        {
            // Já existe um Claudin: ele vem para a frente e esta execução termina.
            Interop.NativeMethods.AllowSetForegroundWindow(Interop.NativeMethods.ASFW_ANY);
            SingleInstance.SendToExisting(instanceName, SingleInstance.ActivateMessage, TimeSpan.FromSeconds(3));
            Log.Info("Claudin já está aberto; a instância existente foi ativada");
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Exceção fatal não tratada", args.ExceptionObject as Exception);

        AppState state;
        try
        {
            state = AppState.Initialize(AppPaths.ConfigFile, new WindowsClaudeSystem());
        }
        catch (Exception ex)
        {
            Log.Error("Falha na inicialização", ex);
            MessageBox.Show(
                $"Não foi possível iniciar o Claudin.\n\n{ex.Message}\n\nDetalhes em {AppPaths.LogsDir}",
                "Claudin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        SyncStartupRegistration(state);

        var overridePath = state.Claude.Install?.Source == ClaudeSource.Override ? state.Claude.Install.ExecutablePath : null;
        _instances = new InstanceManager(
            state.Config,
            () => state.Claude.Install,
            new WindowsProcessHost(),
            new ClaudeProcessScanner(new WindowsProcessInspector(), ClaudeDefaults.DefaultDataDir,
                overridePath is null ? null : [overridePath]),
            new Win32WindowSystem());

        // Fechar a janela só encerra o app quando pedido explicitamente (pode ir para a bandeja).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _window = new MainWindow(state, _instances);
        MainWindow = _window;
        _window.ExitRequested += () => ExitApp();

        try
        {
            _tray = new TrayIcon(_instances.Instances, () => _window.ActiveInstance);
            _tray.ShowRequested += () => _window.BringToFront();
            _tray.ProfileRequested += inst =>
            {
                _window.BringToFront();
                _window.SelectFromOutside(inst);
            };
            _tray.ExitRequested += () => ExitApp();
            _tray.ExitAndCloseClaudeRequested += () => _ = ExitClosingClaudeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao criar o ícone da bandeja", ex);
        }

        var background = e.Args.Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase)
            && _tray is not null;
        _window.Start(background);

        _single.StartServer(message =>
        {
            if (message == SingleInstance.ActivateMessage)
            {
                Dispatcher.BeginInvoke(_window.BringToFront);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // As instâncias do Claude continuam abertas: só os handles do Claudin são liberados.
        _tray?.Dispose();
        _instances?.Dispose();
        _single?.Dispose();
        _appMutex?.Dispose();
        Log.Info($"Claudin encerrando (código {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    /// <summary>Sai do Claudin. As instâncias do Claude continuam abertas.</summary>
    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _window?.CloseForExit();
        Shutdown(0);
    }

    /// <summary>
    /// Sai do Claudin e encerra as instâncias do Claude. O Claude não oferece um jeito de sair pedido por outro programa
    /// (o X só o manda para a bandeja), então os processos são encerrados à força, sempre com confirmação.
    /// </summary>
    private async Task ExitClosingClaudeAsync()
    {
        if (_instances is null || _window is null)
        {
            ExitApp();
            return;
        }
        var alive = _instances.Instances.Where(i => i.IsAlive && i.Handle is not null).ToList();
        if (alive.Count > 0)
        {
            _window.BringToFront();
            var names = string.Join(", ", alive.Select(i => i.Profile.Name));
            var answer = MessageBox.Show(_window,
                $"Encerrar o Claude de: {names}?\n\n" +
                "O Claude não tem um jeito de sair pedido por outro programa, então o Claudin encerra os processos à força. " +
                "Conversas em andamento, tarefas do Claude Code e o que não tiver sido salvo podem ser perdidos.",
                "Claudin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var inst in alive)
            {
                await _instances.ForceCloseAsync(inst.Profile.Id);
            }
        }
        ExitApp();
    }

    /// <summary>Se o início com o Windows está ligado mas aponta para outro executável (app movido), corrige.</summary>
    private static void SyncStartupRegistration(AppState state)
    {
        if (!state.Config.StartWithWindows || Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            var startup = new StartupRegistration(new RunKeyStore(), AppPaths.RegistryBackupFile);
            if (!startup.IsEnabled(exe))
            {
                startup.Enable(exe);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao conferir o início com o Windows", ex);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Exceção não tratada na interface", e.Exception);
        MessageBox.Show($"Ocorreu um erro inesperado:\n\n{e.Exception.Message}", "Claudin",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
