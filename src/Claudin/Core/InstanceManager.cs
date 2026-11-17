namespace Claudin.Core;

public sealed class InstanceManagerOptions
{
    /// <summary>Quanto esperar pela janela do Claude depois de iniciar.</summary>
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Se o processo iniciado morrer e nenhuma instância do perfil aparecer neste prazo, é falha.</summary>
    public TimeSpan EarlyExitGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Inicia, adota e acompanha uma instância do Claude por perfil. Todos os métodos devem ser chamados na mesma thread
/// (a da interface). Refresh() deve ser chamado periodicamente (cerca de 1 s).
/// </summary>
public sealed class InstanceManager : IDisposable
{
    private readonly Func<ClaudeInstall?> _getInstall;
    private readonly IProcessHost _host;
    private readonly ClaudeProcessScanner _scanner;
    private readonly WindowTracker _tracker;
    private readonly IWindowSystem _windows;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly InstanceManagerOptions _options;
    private readonly List<ProfileInstance> _instances;

    public InstanceManager(
        AppConfig config,
        Func<ClaudeInstall?> getInstall,
        IProcessHost host,
        ClaudeProcessScanner scanner,
        IWindowSystem windows,
        InstanceManagerOptions? options = null,
        Func<DateTime>? now = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _getInstall = getInstall;
        _host = host;
        _scanner = scanner;
        _windows = windows;
        _tracker = new WindowTracker(windows);
        _options = options ?? new InstanceManagerOptions();
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? (d => Task.Delay(d));
        _instances = config.Profiles
            .Select(p =>
            {
                var dir = p.ResolveDataDir(config.ProfilesRoot);
                return new ProfileInstance(p, dir, PathUtil.AreSame(dir, scanner.DefaultDataDir));
            })
            .ToList();
    }

    public IReadOnlyList<ProfileInstance> Instances => _instances;

    public ProfileInstance Get(string profileId) =>
        _instances.FirstOrDefault(i => string.Equals(i.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Perfil desconhecido: {profileId}", nameof(profileId));

    /// <summary>
    /// Atualiza o estado: detecta instâncias fechadas ou travadas, reencontra janelas e adota instâncias do perfil que
    /// foram abertas fora do Claudin (por exemplo, a padrão aberta pelo Windows).
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<ClaudeMainProcess>? mains = null;
        foreach (var inst in _instances)
        {
            switch (inst.State)
            {
                case InstanceState.Starting:
                    break; // cuidado por StartAsync
                case InstanceState.Running or InstanceState.NotResponding:
                    UpdateAlive(inst);
                    break;
                default:
                    mains ??= Scan();
                    var found = mains.FirstOrDefault(m => PathUtil.AreSame(m.DataDir, inst.DataDir));
                    if (found is not null && _host.Open(found.Pid) is { } handle)
                    {
                        Attach(inst, handle, adopted: true, _tracker.FindMainWindow(found.Pid));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Garante que o perfil tenha uma instância: adota a existente ou inicia uma nova e espera a janela.
    /// Nunca inicia uma segunda instância do mesmo perfil.
    /// </summary>
    public async Task StartAsync(string profileId)
    {
        var inst = Get(profileId);
        if (inst.IsAlive)
        {
            return;
        }

        var install = _getInstall();
        if (install is null)
        {
            Fail(inst, "Claude Desktop não encontrado.");
            return;
        }

        var existing = Scan().FirstOrDefault(m => PathUtil.AreSame(m.DataDir, inst.DataDir));
        if (existing is not null && _host.Open(existing.Pid) is { } existingHandle)
        {
            Attach(inst, existingHandle, adopted: true, _tracker.FindMainWindow(existing.Pid));
            return;
        }

        if (!inst.UsesClaudeDefaultDataDir)
        {
            try
            {
                Directory.CreateDirectory(inst.DataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Fail(inst, $"Não foi possível criar a pasta de dados: {ex.Message}");
                return;
            }
        }

        IProcessHandle launched;
        try
        {
            launched = _host.Launch(install.ExecutablePath, CommandLine.BuildStartArguments(inst.DataDir));
        }
        catch (Exception ex)
        {
            Log.Error($"Perfil '{inst.Profile.Id}': falha ao iniciar o Claude", ex);
            Fail(inst, $"Não foi possível iniciar o Claude: {ex.Message}");
            return;
        }

        Log.Info($"Perfil '{inst.Profile.Id}': Claude iniciado (PID {launched.Pid})");
        inst.Handle?.Dispose();
        inst.Handle = null;
        inst.MainPid = null;
        inst.Window = IntPtr.Zero;
        inst.Adopted = false;
        inst.Message = null;
        inst.State = InstanceState.Starting;

        var started = _now();
        while (true)
        {
            await _delay(_options.PollInterval);
            if (inst.State != InstanceState.Starting)
            {
                launched.Dispose();
                return; // outra ação mudou o estado (ex.: forçar fechamento)
            }

            // O MAIN é procurado pela pasta de dados, não só pelo PID iniciado: se o processo repassar para uma
            // instância que já existia, é ela que vale.
            var main = Scan().FirstOrDefault(m => PathUtil.AreSame(m.DataDir, inst.DataDir));
            int? pid = main?.Pid ?? (launched.HasExited ? null : launched.Pid);
            var elapsed = _now() - started;

            if (pid is int p)
            {
                var hwnd = _tracker.FindMainWindow(p);
                if (hwnd != IntPtr.Zero || elapsed >= _options.WindowTimeout)
                {
                    var handle = p == launched.Pid ? launched : _host.Open(p);
                    if (!ReferenceEquals(handle, launched))
                    {
                        launched.Dispose();
                    }
                    if (handle is null)
                    {
                        continue; // sumiu entre a varredura e a abertura; tenta de novo
                    }
                    Attach(inst, handle, adopted: false, hwnd);
                    if (hwnd == IntPtr.Zero)
                    {
                        inst.Message = $"A janela do Claude não apareceu em {_options.WindowTimeout.TotalSeconds:0} s. O processo continua aberto.";
                        Log.Warn($"Perfil '{inst.Profile.Id}': janela não encontrada após {_options.WindowTimeout.TotalSeconds:0} s");
                    }
                    return;
                }
            }
            else if (elapsed >= _options.EarlyExitGrace)
            {
                var code = launched.ExitCode;
                launched.Dispose();
                Fail(inst, $"O Claude encerrou logo depois de iniciar{(code is null ? "" : $" (código {code})")}.");
                return;
            }

            if (elapsed >= _options.WindowTimeout)
            {
                launched.Dispose();
                Fail(inst, $"O Claude não abriu em {_options.WindowTimeout.TotalSeconds:0} s.");
                return;
            }
        }
    }

    /// <summary>Encerra à força a instância do perfil (pedido explícito do usuário) e espera o processo sair.</summary>
    public async Task<bool> ForceCloseAsync(string profileId)
    {
        var inst = Get(profileId);
        if (inst.Handle is not { } handle)
        {
            return false;
        }
        Log.Warn($"Perfil '{inst.Profile.Id}': encerramento forçado pedido pelo usuário (PID {handle.Pid})");
        handle.KillTree();
        var started = _now();
        while (!handle.HasExited && _now() - started < TimeSpan.FromSeconds(5))
        {
            await _delay(_options.PollInterval);
        }
        var exited = handle.HasExited;
        if (exited)
        {
            MarkExited(inst);
        }
        return exited;
    }

    /// <summary>Mostra e traz para frente a janela do Claude do perfil.</summary>
    public bool ShowWindow(string profileId)
    {
        var inst = Get(profileId);
        if (inst.Window == IntPtr.Zero || !_windows.IsWindow(inst.Window))
        {
            if (inst.MainPid is int pid)
            {
                inst.Window = _tracker.FindMainWindow(pid);
            }
        }
        return _windows.ShowAndFocus(inst.Window);
    }

    public void Dispose()
    {
        foreach (var inst in _instances)
        {
            inst.Handle?.Dispose();
            inst.Handle = null;
        }
    }

    private void UpdateAlive(ProfileInstance inst)
    {
        if (inst.Handle is null || inst.Handle.HasExited)
        {
            MarkExited(inst);
            return;
        }
        if (inst.Window == IntPtr.Zero || !_windows.IsWindow(inst.Window))
        {
            inst.Window = inst.MainPid is int pid ? _tracker.FindMainWindow(pid) : IntPtr.Zero;
        }
        var hung = inst.Window != IntPtr.Zero && _windows.IsHung(inst.Window);
        if (hung && inst.State != InstanceState.NotResponding)
        {
            Log.Warn($"Perfil '{inst.Profile.Id}': janela não está respondendo");
        }
        inst.State = hung ? InstanceState.NotResponding : InstanceState.Running;
        if (inst.Window != IntPtr.Zero && inst.Message is not null && !hung)
        {
            inst.Message = null; // a janela apareceu depois do prazo
        }
    }

    private void Attach(ProfileInstance inst, IProcessHandle handle, bool adopted, IntPtr hwnd)
    {
        if (!ReferenceEquals(inst.Handle, handle))
        {
            inst.Handle?.Dispose();
        }
        inst.Handle = handle;
        inst.MainPid = handle.Pid;
        inst.Window = hwnd;
        inst.Adopted = adopted;
        inst.Message = null;
        inst.State = InstanceState.Running;
        Log.Info($"Perfil '{inst.Profile.Id}': {(adopted ? "instância existente adotada" : "instância aberta")} (PID {handle.Pid}, janela 0x{hwnd.ToInt64():X})");
    }

    private void MarkExited(ProfileInstance inst)
    {
        var code = inst.Handle?.ExitCode;
        inst.Handle?.Dispose();
        inst.Handle = null;
        inst.MainPid = null;
        inst.Window = IntPtr.Zero;
        inst.Message = code is null or 0 ? null : $"Código de saída {code}.";
        inst.State = InstanceState.Exited;
        Log.Info($"Perfil '{inst.Profile.Id}': instância fechada{(code is null ? "" : $" (código {code})")}");
    }

    private void Fail(ProfileInstance inst, string message)
    {
        inst.Handle?.Dispose();
        inst.Handle = null;
        inst.MainPid = null;
        inst.Window = IntPtr.Zero;
        inst.Message = message;
        inst.State = InstanceState.Failed;
        Log.Warn($"Perfil '{inst.Profile.Id}': {message}");
    }

    private IReadOnlyList<ClaudeMainProcess> Scan()
    {
        try
        {
            return _scanner.FindMainProcesses();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar as instâncias do Claude", ex);
            return [];
        }
    }
}
