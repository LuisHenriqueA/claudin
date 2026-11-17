using Claudin.Core;

namespace Claudin.Tests;

public sealed class InstanceManagerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeInspector _inspector = new();
    private readonly FakeHost _host = new();
    private readonly FakeWindows _windows = new();
    private readonly FakeClock _clock = new();
    private ClaudeInstall? _install = new(FakePaths.PackageExe, ClaudeSource.Alias, null, null);

    public void Dispose() => _tmp.Dispose();

    private string DirY => _tmp.Combine("perfis", "y");

    /// <summary>x = pasta padrão (fake), y = pasta própria sob a pasta temporária.</summary>
    private InstanceManager CreateManager()
    {
        var config = new AppConfig
        {
            ProfilesRoot = _tmp.Combine("perfis"),
            ActiveProfileId = "x",
            Profiles =
            [
                new Profile { Id = "x", Name = "Conta X", DataDir = FakePaths.DefaultDataDir },
                new Profile { Id = "y", Name = "Conta Y" },
            ],
        };
        return new InstanceManager(config, () => _install, _host,
            new ClaudeProcessScanner(_inspector, FakePaths.DefaultDataDir), _windows,
            new InstanceManagerOptions(), () => _clock.Now, _clock.Delay);
    }

    /// <summary>Simula o Claude: ao iniciar, o processo aparece e, depois de alguns ticks, a janela.</summary>
    private void SimulateClaudeStartup(int windowAfterTicks = 3)
    {
        FakeHandle? launched = null;
        _host.OnLaunch = h =>
        {
            launched = h;
            var dir = CommandLine.GetSwitchValue(CommandLine.Split("x " + _host.Launches[^1].Args), "user-data-dir")!;
            _inspector.AddClaude(h.Pid, dir, dir);
        };
        _clock.OnTick = tick =>
        {
            if (tick == windowAfterTicks && launched is not null)
            {
                _windows.AddMainWindow(launched.Pid);
            }
        };
    }

    [Fact]
    public async Task Start_LaunchesWithUserDataDir_AndWaitsForWindow()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();

        await manager.StartAsync("y");
        var y = manager.Get("y");

        var launch = Assert.Single(_host.Launches);
        Assert.Equal(FakePaths.PackageExe, launch.Exe);
        Assert.Equal(CommandLine.BuildStartArguments(DirY), launch.Args);
        Assert.True(Directory.Exists(DirY));
        Assert.Equal(InstanceState.Running, y.State);
        Assert.Equal(1000, y.MainPid);
        Assert.NotEqual(IntPtr.Zero, y.Window);
        Assert.False(y.Adopted);
        Assert.Null(y.Message);
    }

    [Fact]
    public async Task Start_Twice_DoesNotLaunchSecondInstance()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();

        await manager.StartAsync("y");
        await manager.StartAsync("y");
        manager.Refresh();
        await manager.StartAsync("y");

        Assert.Single(_host.Launches);
    }

    [Fact]
    public async Task Start_WhenProfileAlreadyRunning_AdoptsWithoutLaunching()
    {
        _inspector.AddClaude(500, null, FakePaths.DefaultDataDir);
        _host.Register(500);
        var hwnd = _windows.AddMainWindow(500);
        var manager = CreateManager();

        await manager.StartAsync("x");
        var x = manager.Get("x");

        Assert.Empty(_host.Launches);
        Assert.Equal(InstanceState.Running, x.State);
        Assert.True(x.Adopted);
        Assert.Equal(500, x.MainPid);
        Assert.Equal(hwnd, x.Window);
    }

    [Fact]
    public void Refresh_AdoptsInstancesOpenedOutsideClaudin_OnlyForMatchingProfile()
    {
        var manager = CreateManager();
        manager.Refresh();
        Assert.All(manager.Instances, i => Assert.Equal(InstanceState.NotRunning, i.State));

        _inspector.AddClaude(500, null, FakePaths.DefaultDataDir); // Windows abriu a padrão (link claude://)
        _host.Register(500);
        _windows.AddMainWindow(500, visible: false);                // na bandeja
        manager.Refresh();

        Assert.Equal(InstanceState.Running, manager.Get("x").State);
        Assert.True(manager.Get("x").Adopted);
        Assert.NotEqual(IntPtr.Zero, manager.Get("x").Window);
        Assert.Equal(InstanceState.NotRunning, manager.Get("y").State);
    }

    [Fact]
    public async Task Refresh_DetectsExit_AndReopenLaunchesAgain()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();
        await manager.StartAsync("y");
        var pid = manager.Get("y").MainPid!.Value;

        _host.Alive[pid].Exited = true;     // morto pelo Gerenciador de Tarefas
        _inspector.RemoveTree(pid);
        _windows.RemoveWindowsOf(pid);
        manager.Refresh();

        var y = manager.Get("y");
        Assert.Equal(InstanceState.Exited, y.State);
        Assert.Null(y.MainPid);
        Assert.Equal(IntPtr.Zero, y.Window);

        _clock.OnTick = null;
        SimulateClaudeStartup();
        await manager.StartAsync("y");
        Assert.Equal(2, _host.Launches.Count);
        Assert.Equal(InstanceState.Running, manager.Get("y").State);
    }

    [Fact]
    public async Task Refresh_DetectsHungWindow_AndRecovery()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();
        await manager.StartAsync("y");
        var y = manager.Get("y");

        _windows.Hung.Add(y.Window);
        manager.Refresh();
        Assert.Equal(InstanceState.NotResponding, y.State);
        Assert.True(y.IsAlive);

        _windows.Hung.Clear();
        manager.Refresh();
        Assert.Equal(InstanceState.Running, y.State);
    }

    [Fact]
    public async Task Refresh_FindsWindowAgain_WhenHandleChanges()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();
        await manager.StartAsync("y");
        var y = manager.Get("y");
        var pid = y.MainPid!.Value;

        _windows.RemoveWindowsOf(pid);
        var newHwnd = _windows.AddMainWindow(pid);
        manager.Refresh();

        Assert.Equal(newHwnd, y.Window);
        Assert.Equal(InstanceState.Running, y.State);
    }

    [Fact]
    public async Task Start_LaunchedProcessForwardsToExistingInstance_TracksTheExistingOne()
    {
        // Corrida: a instância do perfil apareceu entre a varredura e o início.
        _host.OnLaunch = h =>
        {
            h.Exited = true;
            h.Code = 0;
            _inspector.AddClaude(700, null, FakePaths.DefaultDataDir);
            _host.Register(700);
            _windows.AddMainWindow(700);
        };
        var manager = CreateManager();

        await manager.StartAsync("x");

        Assert.Equal(InstanceState.Running, manager.Get("x").State);
        Assert.Equal(700, manager.Get("x").MainPid);
    }

    [Fact]
    public async Task Start_ProcessDiesImmediately_Fails()
    {
        _host.OnLaunch = h =>
        {
            h.Exited = true;
            h.Code = 3;
        };
        var manager = CreateManager();

        await manager.StartAsync("y");
        var y = manager.Get("y");

        Assert.Equal(InstanceState.Failed, y.State);
        Assert.Contains("encerrou logo depois de iniciar (código 3)", y.Message);
        Assert.True(_clock.Now - new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc) <= TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Start_WindowNeverAppears_AttachesAfterTimeoutWithMessage_ThenClearsWhenWindowShows()
    {
        _host.OnLaunch = h => _inspector.AddClaude(h.Pid, DirY, DirY); // processo vivo, sem janela
        var manager = CreateManager();

        await manager.StartAsync("y");
        var y = manager.Get("y");

        Assert.Equal(InstanceState.Running, y.State);
        Assert.Equal(IntPtr.Zero, y.Window);
        Assert.Contains("30 s", y.Message);

        _windows.AddMainWindow(y.MainPid!.Value);
        manager.Refresh();
        Assert.NotEqual(IntPtr.Zero, y.Window);
        Assert.Null(y.Message);
    }

    [Fact]
    public async Task Start_WithoutClaudeInstalled_Fails()
    {
        _install = null;
        var manager = CreateManager();

        await manager.StartAsync("y");

        Assert.Equal(InstanceState.Failed, manager.Get("y").State);
        Assert.Contains("não encontrado", manager.Get("y").Message);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Start_LaunchThrows_Fails()
    {
        _host.LaunchError = new System.ComponentModel.Win32Exception(5, "Acesso negado");
        var manager = CreateManager();

        await manager.StartAsync("y");

        Assert.Equal(InstanceState.Failed, manager.Get("y").State);
        Assert.Contains("Acesso negado", manager.Get("y").Message);
    }

    [Fact]
    public async Task Start_DefaultProfile_DoesNotCreateFolder()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();

        await manager.StartAsync("x");

        Assert.False(Directory.Exists(FakePaths.DefaultDataDir));
        Assert.Equal(CommandLine.BuildStartArguments(FakePaths.DefaultDataDir), _host.Launches.Single().Args);
    }

    [Fact]
    public async Task ForceClose_KillsTree_AndMarksExited()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();
        await manager.StartAsync("y");
        var handle = _host.Alive[manager.Get("y").MainPid!.Value];

        var closed = await manager.ForceCloseAsync("y");

        Assert.True(closed);
        Assert.True(handle.Killed);
        Assert.True(handle.Disposed);
        Assert.Equal(InstanceState.Exited, manager.Get("y").State);
    }

    [Fact]
    public async Task ShowWindow_UsesTrackedWindow()
    {
        SimulateClaudeStartup();
        var manager = CreateManager();
        await manager.StartAsync("y");

        Assert.True(manager.ShowWindow("y"));
        Assert.Equal(manager.Get("y").Window, Assert.Single(_windows.Shown));
    }

    [Fact]
    public void Dispose_ReleasesHandles_WithoutKilling()
    {
        _inspector.AddClaude(500, null, FakePaths.DefaultDataDir);
        var handle = _host.Register(500);
        var manager = CreateManager();
        manager.Refresh();

        manager.Dispose();

        Assert.True(handle.Disposed);
        Assert.False(handle.Killed);
    }
}
