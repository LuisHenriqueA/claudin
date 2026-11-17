using Claudin.Core;

namespace Claudin.Tests;

internal static class FakePaths
{
    public const string PackageExe = @"C:\Program Files\WindowsApps\Claude_5.1231.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
    public const string ClaudeCodeExe = @"C:\Users\t\AppData\Roaming\Claude\claude-code\2.1.321\abc\claude.exe";
    public const string DefaultDataDir = @"C:\Users\t\AppData\Roaming\Claude";
}

/// <summary>Processos simulados: o Claude "real" é um MAIN + filhos com --type e --user-data-dir.</summary>
internal sealed class FakeInspector : IProcessInspector
{
    private sealed record Proc(int Pid, int ParentPid, string ExeName, string Image, string CommandLine);

    private readonly List<Proc> _procs = [];

    public void Add(int pid, int parentPid, string image, string commandLine) =>
        _procs.Add(new Proc(pid, parentPid, Path.GetFileName(image), image, commandLine));

    /// <summary>Adiciona uma instância do Claude como o Electron cria: MAIN e dois filhos.</summary>
    public void AddClaude(int mainPid, string? mainDataDirArg, string resolvedDataDir)
    {
        var mainCmd = $"\"{FakePaths.PackageExe}\"" + (mainDataDirArg is null ? "" : $" --user-data-dir=\"{mainDataDirArg}\"");
        Add(mainPid, 1, FakePaths.PackageExe, mainCmd);
        Add(mainPid + 1, mainPid, FakePaths.PackageExe, $"\"{FakePaths.PackageExe}\" --type=gpu-process --user-data-dir=\"{resolvedDataDir}\" --gpu-preferences=x");
        Add(mainPid + 2, mainPid, FakePaths.PackageExe, $"\"{FakePaths.PackageExe}\" --type=crashpad-handler --user-data-dir={resolvedDataDir} /prefetch:4");
    }

    public void RemoveTree(int mainPid) => _procs.RemoveAll(p => p.Pid == mainPid || p.ParentPid == mainPid);

    public IReadOnlyList<ProcessEntry> Snapshot() => _procs.Select(p => new ProcessEntry(p.Pid, p.ParentPid, p.ExeName)).ToList();

    public string? GetImagePath(int pid) => _procs.FirstOrDefault(p => p.Pid == pid)?.Image;

    public string? GetCommandLine(int pid) => _procs.FirstOrDefault(p => p.Pid == pid)?.CommandLine;
}

internal sealed class FakeHandle(int pid) : IProcessHandle
{
    public int Pid { get; } = pid;

    public bool Exited { get; set; }

    public int? Code { get; set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public bool HasExited => Exited;

    public int? ExitCode => Exited ? Code : null;

    public bool KillTree()
    {
        Killed = true;
        Exited = true;
        Code = 1;
        return true;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeHost : IProcessHost
{
    public List<(string Exe, string Args)> Launches { get; } = [];

    public Dictionary<int, FakeHandle> Alive { get; } = [];

    public int NextPid { get; set; } = 1000;

    public Exception? LaunchError { get; set; }

    /// <summary>Executado em cada Launch, com o handle criado (para simular o que o Claude faz).</summary>
    public Action<FakeHandle>? OnLaunch { get; set; }

    public IProcessHandle Launch(string executable, string arguments)
    {
        if (LaunchError is not null)
        {
            throw LaunchError;
        }
        Launches.Add((executable, arguments));
        var handle = new FakeHandle(NextPid);
        NextPid += 100;
        Alive[handle.Pid] = handle;
        OnLaunch?.Invoke(handle);
        return handle;
    }

    public IProcessHandle? Open(int pid) =>
        Alive.TryGetValue(pid, out var h) && !h.Exited ? h : null;

    public FakeHandle Register(int pid)
    {
        var h = new FakeHandle(pid);
        Alive[pid] = h;
        return h;
    }
}

internal sealed class FakeWindows : IWindowSystem
{
    private readonly List<WindowCandidate> _windows = [];

    public HashSet<IntPtr> Hung { get; } = [];

    public List<IntPtr> Shown { get; } = [];

    public IntPtr AddMainWindow(int pid, bool visible = true)
    {
        var h = new IntPtr(pid * 10 + _windows.Count + 1);
        _windows.Add(new WindowCandidate(h, pid, WindowTracker.MainWindowClass, "Claude", visible, false, 1000));
        return h;
    }

    public void RemoveWindowsOf(int pid) => _windows.RemoveAll(w => w.Pid == pid);

    public IReadOnlyList<WindowCandidate> GetTopLevelWindows(int pid) => _windows.Where(w => w.Pid == pid).ToList();

    public bool IsWindow(IntPtr hwnd) => _windows.Any(w => w.Handle == hwnd);

    public bool IsHung(IntPtr hwnd) => Hung.Contains(hwnd);

    public bool ShowAndFocus(IntPtr hwnd)
    {
        Shown.Add(hwnd);
        return IsWindow(hwnd);
    }
}

/// <summary>Relógio falso: cada Delay avança o tempo e executa um gancho opcional.</summary>
internal sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    public int Ticks { get; private set; }

    public Action<int>? OnTick { get; set; }

    public Task Delay(TimeSpan d)
    {
        Now += d;
        Ticks++;
        OnTick?.Invoke(Ticks);
        return Task.CompletedTask;
    }
}
