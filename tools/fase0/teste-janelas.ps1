# Fase 0 / item 5: testa ShowWindow e SetWindowPos na janela principal de UMA instancia de teste.
# Uso: .\teste-janelas.ps1 -TestPid <pid do MAIN da instancia de teste>
# NUNCA passe o PID da instancia padrao (%APPDATA%\Claude).
param([Parameter(Mandatory)][uint32]$TestPid, [string]$UserDataPrefix = 'C:\Claudin\teste')

. "$PSScriptRoot\cdhelpers.ps1"
if (-not ('CD.Win2' -as [type])) {
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
namespace CD { public static class Win2 {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out R r, int size);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int val);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}}
'@
}
[void][CD.Win2]::SetThreadDpiAwarenessContext([IntPtr]-4)   # Per-Monitor V2: coordenadas fisicas

# Guarda de seguranca: o PID precisa usar um user-data-dir de teste
$cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$TestPid").CommandLine
if (-not $cl -or $cl.IndexOf($UserDataPrefix, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "PID $TestPid nao e instancia de teste" }

$h = [CD.Native]::TopWindows() | Where-Object { [CD.Native]::Pid($_) -eq $TestPid -and [CD.Native]::Cls($_) -eq 'Chrome_WidgetWin_1' -and [CD.Native]::Title($_) -and [CD.Native]::GetWindow($_,4) -eq [IntPtr]::Zero -and [CD.Native]::IsWindowVisible($_) } | Select-Object -First 1
if (-not $h) { throw "janela principal nao encontrada" }

function State($label) {
  $r = New-Object CD.Native+RECT; [void][CD.Native]::GetWindowRect($h, [ref]$r)
  $f = New-Object CD.Win2+R; [void][CD.Win2]::DwmGetWindowAttribute($h, 9, [ref]$f, 16)
  "{0,-38} vis={1,-5} min={2,-5} max={3,-5} rect={4},{5} {6}x{7}  dwm={8},{9} {10}x{11}  dpi={12}" -f $label,
    [CD.Native]::IsWindowVisible($h), [CD.Native]::IsIconic($h), [CD.Native]::IsZoomed($h),
    $r.L,$r.T,($r.R-$r.L),($r.B-$r.T), $f.L,$f.T,($f.Ri-$f.L),($f.B-$f.T), [CD.Native]::GetDpiForWindow($h)
}
$SWP_NOZORDER=0x4; $SWP_NOACTIVATE=0x10; $SWP_SHOWWINDOW=0x40; $SWP_FRAMECHANGED=0x20

"hwnd = 0x{0:X}" -f $h.ToInt64()
State 'inicial'

# 1. SetWindowPos
$ok = [CD.Native]::SetWindowPos($h, [IntPtr]::Zero, 100, 150, 1400, 900, $SWP_NOZORDER -bor $SWP_NOACTIVATE)
State "SetWindowPos(100,150,1400x900) ok=$ok"
Start-Sleep 2; State '  +2s (app reposicionou?)'

# 2. Hide / persistencia do hide
[void][CD.Native]::ShowWindow($h, 0); State 'SW_HIDE'
Start-Sleep 3; State '  +3s (app re-exibiu?)'

# 3. SetWindowPos enquanto oculta
[void][CD.Native]::SetWindowPos($h, [IntPtr]::Zero, 120, 170, 1300, 850, $SWP_NOZORDER -bor $SWP_NOACTIVATE)
State 'SetWindowPos oculta (120,170,1300x850)'

# 4. Show
[void][CD.Native]::ShowWindow($h, 5); State 'SW_SHOW'
$fg = [CD.Win2]::SetForegroundWindow($h); State "SetForegroundWindow ok=$fg"

# 5. Minimize / restore
[void][CD.Native]::ShowWindow($h, 6); Start-Sleep -Milliseconds 500; State 'SW_MINIMIZE'
[void][CD.Native]::ShowWindow($h, 9); Start-Sleep -Milliseconds 500; State 'SW_RESTORE'

# 6. Maximize e SetWindowPos com maximizada
[void][CD.Native]::ShowWindow($h, 3); Start-Sleep -Milliseconds 500; State 'SW_MAXIMIZE'
[void][CD.Native]::SetWindowPos($h, [IntPtr]::Zero, 100, 150, 1400, 900, $SWP_NOZORDER -bor $SWP_NOACTIVATE); Start-Sleep -Milliseconds 300
State 'SetWindowPos com maximizada'
[void][CD.Native]::ShowWindow($h, 9); Start-Sleep -Milliseconds 500; State 'SW_RESTORE'
[void][CD.Native]::SetWindowPos($h, [IntPtr]::Zero, 100, 150, 1400, 900, $SWP_NOZORDER -bor $SWP_NOACTIVATE); State 'SetWindowPos apos restore'

# 7. Oculta + segunda instancia (como no repasse de claude://)
[void][CD.Native]::ShowWindow($h, 0); State 'SW_HIDE (antes da 2a instancia)'
$exe = (Get-Process -Id $TestPid).Path
$udd = if ($cl -match '--user-data-dir="?([^"]+?)"?(\s|$)') { $Matches[1] } else { throw 'udd?' }
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.Arguments = "--user-data-dir=`"$udd`""; $psi.UseShellExecute = $false; $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true
$p2 = [System.Diagnostics.Process]::Start($psi); [void]$p2.WaitForExit(15000)
Start-Sleep -Milliseconds 800; State "apos 2a instancia (exit=$($p2.ExitCode))"

# 8. WS_EX_TOOLWINDOW para tirar da barra de tarefas (opcional da Fase 3)
$ex = [CD.Native]::GetWindowLong($h, -20)
[void][CD.Native]::ShowWindow($h, 0)
[void][CD.Win2]::SetWindowLong($h, -20, ($ex -bor 0x80) -band (-bnot 0x40000))
[void][CD.Native]::ShowWindow($h, 5)
Start-Sleep 1; State ("ExStyle TOOLWINDOW 0x{0:X}" -f [CD.Native]::GetWindowLong($h,-20))
Start-Sleep 2; State ("  +2s ExStyle 0x{0:X} (app reverteu?)" -f [CD.Native]::GetWindowLong($h,-20))
[void][CD.Native]::ShowWindow($h, 0); [void][CD.Win2]::SetWindowLong($h, -20, $ex); [void][CD.Native]::ShowWindow($h, 5)
State ("ExStyle restaurado 0x{0:X}" -f [CD.Native]::GetWindowLong($h,-20))
