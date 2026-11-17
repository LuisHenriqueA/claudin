using System.Text;
using Claudin.Interop;

namespace Claudin.Core;

public sealed record WindowCandidate(IntPtr Handle, int Pid, string ClassName, string Title, bool Visible, bool HasOwner, long Area);

/// <summary>Operações de janela usadas pelo Claudin (substituível nos testes). Nenhum método lança exceção.</summary>
public interface IWindowSystem
{
    IReadOnlyList<WindowCandidate> GetTopLevelWindows(int pid);

    bool IsWindow(IntPtr hwnd);

    bool IsHung(IntPtr hwnd);

    /// <summary>Mostra (restaurando se minimizada) e traz para frente.</summary>
    bool ShowAndFocus(IntPtr hwnd);
}

/// <summary>Acha a janela principal de uma instância do Claude (Fase 0, seção 5).</summary>
public sealed class WindowTracker(IWindowSystem windows)
{
    public const string MainWindowClass = "Chrome_WidgetWin_1";

    public IntPtr FindMainWindow(int pid) => SelectMainWindow(windows.GetTopLevelWindows(pid), pid);

    /// <summary>
    /// Classe Chrome_WidgetWin_1, sem dono e com título. A janela pode estar oculta: fechar o Claude pelo X só a
    /// esconde (o processo continua na bandeja). Entre várias, prefere a visível e a maior.
    /// </summary>
    public static IntPtr SelectMainWindow(IEnumerable<WindowCandidate> candidates, int pid) =>
        candidates
            .Where(w => w.Pid == pid && w.ClassName == MainWindowClass && !w.HasOwner && !string.IsNullOrWhiteSpace(w.Title))
            .OrderByDescending(w => w.Visible)
            .ThenByDescending(w => w.Area)
            .Select(w => w.Handle)
            .FirstOrDefault();
}

public sealed class Win32WindowSystem : IWindowSystem
{
    public IReadOnlyList<WindowCandidate> GetTopLevelWindows(int pid)
    {
        var result = new List<WindowCandidate>();
        var cls = new StringBuilder(256);
        var title = new StringBuilder(512);
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid)
            {
                return true;
            }
            cls.Clear();
            title.Clear();
            NativeMethods.GetClassNameW(hwnd, cls, cls.Capacity);
            NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);
            long area = 0;
            if (NativeMethods.GetWindowRect(hwnd, out var r))
            {
                area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            }
            result.Add(new WindowCandidate(hwnd, owner, cls.ToString(), title.ToString(),
                NativeMethods.IsWindowVisible(hwnd), NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero, area));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);

    public bool IsHung(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsHungAppWindow(hwnd);

    public bool ShowAndFocus(IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }
        NativeMethods.ShowWindow(hwnd, NativeMethods.IsIconic(hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOW);
        return NativeMethods.SetForegroundWindow(hwnd);
    }
}
