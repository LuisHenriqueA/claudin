using System.Windows.Interop;
using Claudin.Core;
using Claudin.Interop;

namespace Claudin.Hosting;

/// <summary>
/// Ctrl+Alt+1..9 alternam os perfis. O foco costuma estar dentro do Claude (outro processo), então os atalhos são
/// registrados com RegisterHotKey, mas só enquanto o Claudin ou uma janela hospedada estiver em primeiro plano.
/// Fora disso, as teclas ficam livres para os outros programas.
/// </summary>
internal sealed class ProfileHotkeys : IDisposable
{
    private const int BaseId = 0x4C00;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly int _count;
    private readonly Action<int> _onHotkey;
    private bool _registered;
    private bool _failureLogged;

    public ProfileHotkeys(IntPtr hwnd, int profileCount, Action<int> onHotkey)
    {
        _hwnd = hwnd;
        _count = Math.Clamp(profileCount, 0, 9);
        _onHotkey = onHotkey;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("HwndSource indisponível");
        _source.AddHook(WndProc);
    }

    /// <summary>Liga os atalhos quando o primeiro plano é do Claudin ou de uma janela dele; desliga caso contrário.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _registered)
        {
            return;
        }
        if (enabled)
        {
            for (var i = 1; i <= _count; i++)
            {
                if (!NativeMethods.RegisterHotKey(_hwnd, BaseId + i,
                        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)('0' + i))
                    && !_failureLogged)
                {
                    Log.Warn($"Ctrl+Alt+{i} já está em uso por outro programa (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                    _failureLogged = true;
                }
            }
        }
        else
        {
            for (var i = 1; i <= _count; i++)
            {
                NativeMethods.UnregisterHotKey(_hwnd, BaseId + i);
            }
        }
        _registered = enabled;
    }

    public void Dispose()
    {
        SetEnabled(false);
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var index = wParam.ToInt32() - BaseId;
            if (index >= 1 && index <= _count)
            {
                handled = true;
                _onHotkey(index - 1);
            }
        }
        return IntPtr.Zero;
    }
}
