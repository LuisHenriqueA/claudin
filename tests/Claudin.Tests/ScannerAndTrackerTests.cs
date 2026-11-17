using Claudin.Core;

namespace Claudin.Tests;

public sealed class ClaudeProcessScannerTests
{
    private static ClaudeProcessScanner Scanner(FakeInspector inspector, params string[] extra) =>
        new(inspector, FakePaths.DefaultDataDir, extra);

    [Fact]
    public void FindsMainProcesses_WithDataDirFromChildren()
    {
        var inspector = new FakeInspector();
        inspector.AddClaude(100, null, FakePaths.DefaultDataDir);           // aberta pelo Windows, sem argumentos
        inspector.AddClaude(200, @"C:\Claudin\perfis\y", @"C:\Claudin\perfis\y");

        var mains = Scanner(inspector).FindMainProcesses().OrderBy(m => m.Pid).ToList();

        Assert.Equal([100, 200], mains.Select(m => m.Pid));
        Assert.Equal(FakePaths.DefaultDataDir, mains[0].DataDir);
        Assert.Equal(@"C:\Claudin\perfis\y", mains[1].DataDir);
    }

    [Fact]
    public void MainWithoutChildren_UsesOwnArgument_ThenDefault()
    {
        var inspector = new FakeInspector();
        inspector.Add(100, 1, FakePaths.PackageExe, $"\"{FakePaths.PackageExe}\" {CommandLine.BuildStartArguments(@"C:\P\y\")}");
        inspector.Add(300, 1, FakePaths.PackageExe, $"\"{FakePaths.PackageExe}\"");

        var mains = Scanner(inspector).FindMainProcesses().OrderBy(m => m.Pid).ToList();

        Assert.Equal(@"C:\P\y", mains[0].DataDir);
        Assert.Equal(FakePaths.DefaultDataDir, mains[1].DataDir);
    }

    [Fact]
    public void IgnoresClaudeCodeAndOtherProcesses()
    {
        var inspector = new FakeInspector();
        inspector.Add(10, 100, FakePaths.ClaudeCodeExe, $"\"{FakePaths.ClaudeCodeExe}\" --output-format stream-json");
        inspector.Add(11, 1, @"C:\Windows\notepad.exe", "notepad.exe");
        inspector.Add(12, 1, @"C:\Outro\Claude.exe", @"""C:\Outro\Claude.exe""");

        Assert.Empty(Scanner(inspector).FindMainProcesses());
    }

    [Fact]
    public void AcceptsConfiguredExecutable()
    {
        var inspector = new FakeInspector();
        inspector.Add(12, 1, @"C:\Outro\Claude.exe", @"""C:\Outro\Claude.exe"" --user-data-dir=C:\x");

        var main = Assert.Single(Scanner(inspector, @"C:\Outro\Claude.exe").FindMainProcesses());
        Assert.Equal(@"C:\x", main.DataDir);
    }

    [Fact]
    public void FindByDataDir_IsCaseInsensitive()
    {
        var inspector = new FakeInspector();
        inspector.AddClaude(100, null, FakePaths.DefaultDataDir);

        Assert.Equal(100, Scanner(inspector).FindByDataDir(FakePaths.DefaultDataDir.ToUpperInvariant() + @"\")?.Pid);
        Assert.Null(Scanner(inspector).FindByDataDir(@"C:\outra"));
    }

    [Fact]
    public void ProcessThatVanishes_IsSkipped()
    {
        var inspector = new FakeInspector();
        inspector.AddClaude(100, null, FakePaths.DefaultDataDir);
        var vanishing = new VanishingInspector(inspector);

        Assert.Empty(new ClaudeProcessScanner(vanishing, FakePaths.DefaultDataDir).FindMainProcesses());
    }

    private sealed class VanishingInspector(IProcessInspector inner) : IProcessInspector
    {
        public IReadOnlyList<ProcessEntry> Snapshot() => inner.Snapshot();

        public string? GetImagePath(int pid) => null; // sumiu entre o snapshot e a consulta

        public string? GetCommandLine(int pid) => null;
    }
}

public sealed class WindowTrackerTests
{
    private static WindowCandidate W(int h, int pid = 1, string cls = WindowTracker.MainWindowClass, string title = "Claude",
        bool visible = true, bool owner = false, long area = 100) =>
        new(new IntPtr(h), pid, cls, title, visible, owner, area);

    [Fact]
    public void SelectsVisibleTitledUnownedChromeWindow()
    {
        var candidates = new[]
        {
            W(1, cls: "Chrome_WidgetWin_0"),
            W(2, title: ""),
            W(3, owner: true),
            W(4, pid: 2),
            W(5, cls: "Electron_NotifyIconHostWindow"),
            W(6),
        };

        Assert.Equal(new IntPtr(6), WindowTracker.SelectMainWindow(candidates, 1));
    }

    [Fact]
    public void PrefersVisible_ThenLargest()
    {
        Assert.Equal(new IntPtr(2), WindowTracker.SelectMainWindow([W(1, visible: false, area: 999), W(2, area: 10)], 1));
        Assert.Equal(new IntPtr(2), WindowTracker.SelectMainWindow([W(1, area: 10), W(2, area: 20)], 1));
    }

    [Fact]
    public void FindsHiddenWindow_WhenClaudeIsInTray()
    {
        Assert.Equal(new IntPtr(1), WindowTracker.SelectMainWindow([W(1, visible: false)], 1));
    }

    [Fact]
    public void ReturnsZero_WhenNothingMatches()
    {
        Assert.Equal(IntPtr.Zero, WindowTracker.SelectMainWindow([W(1, title: "")], 1));
    }
}
