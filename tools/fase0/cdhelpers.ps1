if (-not ('CD.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace CD {
public static class Native {
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern int GetPackageFullName(IntPtr hProcess, ref uint len, StringBuilder sb);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] public static extern int RegOpenKeyEx(UIntPtr hKey, string sub, uint opt, int sam, out IntPtr res);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] public static extern int RegQueryInfoKey(IntPtr hKey, IntPtr c, IntPtr cl, IntPtr r, IntPtr sk, IntPtr msk, IntPtr mcl, IntPtr v, IntPtr mvn, IntPtr mvl, IntPtr sd, out long ft);
  [DllImport("advapi32.dll")] public static extern int RegCloseKey(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  public static List<IntPtr> TopWindows() {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static string Pkg(uint pid) {
    IntPtr hp = OpenProcess(0x1000, false, pid); if (hp == IntPtr.Zero) return "<openprocess failed>";
    try { uint len = 0; int rc = GetPackageFullName(hp, ref len, null);
      if (rc == 15700) return "<sem identidade de pacote>";
      var sb = new StringBuilder((int)len); rc = GetPackageFullName(hp, ref len, sb);
      return rc == 0 ? sb.ToString() : "<erro " + rc + ">"; }
    finally { CloseHandle(hp); }
  }
  public static DateTime KeyTime(string sub) {
    IntPtr hk; int rc = RegOpenKeyEx(new UIntPtr(0x80000001u), sub, 0, 0x20019, out hk);
    if (rc != 0) return DateTime.MinValue;
    long ft; RegQueryInfoKey(hk, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out ft);
    RegCloseKey(hk); return DateTime.FromFileTime(ft);
  }
}}
'@
}

function Get-ClaudeProcs {
  Get-CimInstance Win32_Process -Filter "Name='claude.exe'" | ForEach-Object {
    $cl = $_.CommandLine
    $udd = if ($cl -match '--user-data-dir="?([^"]+?)"?(\s--|$)') { $Matches[1] } else { '' }
    $type = if ($cl -match '--type=(\S+)') { $Matches[1] } else { 'MAIN' }
    [pscustomobject]@{ Pid=$_.ProcessId; Ppid=$_.ParentProcessId; Type=$type; UserData=$udd; Exe=$_.ExecutablePath }
  }
}

# Janelas top-level de um conjunto de PIDs
function Get-WindowsOf([uint32[]]$pids, [switch]$All) {
  foreach ($h in [CD.Native]::TopWindows()) {
    $p = [CD.Native]::Pid($h)
    if ($pids -contains $p) {
      $vis = [CD.Native]::IsWindowVisible($h)
      if (-not $All -and -not $vis) { continue }
      $r = New-Object CD.Native+RECT; [void][CD.Native]::GetWindowRect($h, [ref]$r)
      [pscustomobject]@{ Hwnd=('0x{0:X}' -f $h.ToInt64()); Pid=$p; Visible=$vis; Class=[CD.Native]::Cls($h); Title=[CD.Native]::Title($h);
        Owner=('0x{0:X}' -f [CD.Native]::GetWindow($h,4).ToInt64()); Rect="$($r.L),$($r.T) $($r.R-$r.L)x$($r.B-$r.T)"; Dpi=[CD.Native]::GetDpiForWindow($h);
        ExStyle=('0x{0:X}' -f [CD.Native]::GetWindowLong($h,-20)) }
    }
  }
}

# Encerra SOMENTE processos cujo user-data-dir esta sob o prefixo de teste
function Stop-TestInstances([string]$prefix) {
  Get-CimInstance Win32_Process -Filter "Name='claude.exe'" | Where-Object { $_.ExecutablePath -like '*\WindowsApps\Claude_*' -and $_.CommandLine -and $_.CommandLine.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object {
    "  encerrando PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
