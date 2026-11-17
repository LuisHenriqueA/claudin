using Claudin.Core;
using Claudin.Interop;

namespace Claudin.Hosting;

/// <summary>
/// Gancho de eventos de acessibilidade fora de processo (WINEVENT_OUTOFCONTEXT): não injeta nada no Claude.
/// Os eventos chegam pela fila de mensagens da thread que criou o gancho (a da interface).
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback; // mantido vivo enquanto o gancho existir
    private IntPtr _hook;

    public WinEventHook(uint eventMin, uint eventMax, int processId, Action<uint, IntPtr, int, int> handler)
    {
        _callback = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            try
            {
                handler(ev, hwnd, idObject, idChild);
            }
            catch (Exception ex)
            {
                Log.Error($"Erro ao tratar evento de janela 0x{ev:X}", ex);
            }
        };
        _hook = NativeMethods.SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _callback, (uint)processId, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
        {
            Log.Warn($"SetWinEventHook falhou (eventos 0x{eventMin:X}-0x{eventMax:X}, processo {processId})");
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
