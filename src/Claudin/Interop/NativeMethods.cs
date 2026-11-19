using System.Runtime.InteropServices;
using System.Text;

namespace Claudin.Interop;

internal static class NativeMethods
{
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const uint TH32CS_SNAPPROCESS = 0x2;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int ProcessCommandLineInformation = 60;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    public const uint GW_OWNER = 4;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    // --- Pacotes MSIX (kernel32, Windows 8+) ---

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetPackagesByPackageFamily(
        string packageFamilyName, ref uint count, IntPtr[]? packageFullNames, ref uint bufferLength, char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, char[]? path);

    // --- DPI (user32, Windows 10 1607+) ---

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    // --- Processos ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref uint size);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern IntPtr LocalFree(IntPtr mem);

    // --- Janelas ---

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    // ---------- Estilo estendido (botão na barra de tarefas) ----------

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const int SW_SHOWNA = 8;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>Em janela de outro processo, envia WM_STYLECHANGING/CHANGED e espera: chamar fora da thread da interface.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    // ---------- Região da janela (recorte dos botões do Claude) e amostra de cor ----------

    public const int RGN_DIFF = 4;
    public const int REGION_ERROR = 0;
    public const uint GA_ROOT = 2;
    public const uint CLR_INVALID = 0xFFFFFFFF;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern bool DeleteObject(IntPtr obj);

    /// <summary>Em caso de sucesso, o sistema passa a ser dono da região: não apagar.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    // --- Fase 3: hospedagem visual ---

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint GW_HWNDPREV = 3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const int OBJID_WINDOW = 0;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;
    public const int VK_LBUTTON = 0x01;

    public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>Versão assíncrona: não bloqueia o Claudin se o Claude estiver travado.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    public const uint WM_CLOSE = 0x0010;

    /// <summary>Não espera a janela de destino processar a mensagem.</summary>
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern short GetAsyncKeyState(int key);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // --- Fase 4 ---

    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint ASFW_ANY = 0xFFFFFFFF;

    /// <summary>Avisa quando a área de transferência muda, sem ler o conteúdo.</summary>
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    // --- Fase 5: posição da janela ---

    public const int SW_SHOWMAXIMIZED = 3;
    public const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    /// <summary>Muda a cada alteração da área de transferência (permite ignorar a própria cópia do Claudin).</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint GetClipboardSequenceNumber();

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
