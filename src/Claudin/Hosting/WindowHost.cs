using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Claudin.Core;
using Claudin.Interop;

namespace Claudin.Hosting;

/// <summary>
/// "Embed" visual (sem SetParent): mantém a janela do Claude do perfil ativo exatamente sobre a área host do Claudin,
/// logo acima dele na ordem Z, e esconde as janelas dos outros perfis.
///
/// Regras vindas da Fase 0 (docs/ACHADOS.md, seção 5):
/// - toda chamada que afeta a janela do Claude é assíncrona (ShowWindowAsync, SWP_ASYNCWINDOWPOS), para o Claudin
///   não travar junto se o Claude travar;
/// - SW_RESTORE antes de posicionar (maximizada ignora SetWindowPos);
/// - compensa as bordas invisíveis medidas pelo DWM;
/// - a janela pode reaparecer sozinha (link, notificação, ícone da bandeja): isso é tratado como pedido de atenção;
/// - na troca de perfil, a janela nova entra por cima da atual e só depois a anterior some. Enquanto a nova não se
///   desenha, ela é transparente e a anterior continua aparecendo: o fundo do Claudin não aparece no meio da troca.
///   (Colocar a nova por baixo não funciona: coberta, o Claude não se desenha.)
/// - opcionalmente, recorta os botões de janela do Claude (SetWindowRgn) e cobre o canto com a CaptionCover, na cor do
///   cabeçalho do Claude. A região é desfeita ao soltar a janela.
/// </summary>
public sealed class WindowHost : IDisposable
{
    private static readonly TimeSpan SuppressFor = TimeSpan.FromMilliseconds(600);

    /// <summary>Tempo para a janela nova se desenhar por cima da anterior antes de esconder a anterior.</summary>
    private static readonly TimeSpan SwitchOverlap = TimeSpan.FromMilliseconds(300);

    private readonly Window _claudin;
    private readonly FrameworkElement _hostArea;
    private readonly CaptionCover _cover = new();
    private readonly DispatcherTimer _switchTimer;
    private readonly DispatcherTimer _captionTimer;
    private readonly DispatcherTimer _colorTimer;
    private bool _pendingFocus;
    private bool _hideCaptionButtons;
    private bool _hideTaskbarButtons;
    private readonly DispatcherTimer _verifyTimer;
    private int _verifyAttempts;
    private uint _patchColor;
    private readonly IntPtr _claudinHwnd;
    private readonly Dictionary<IntPtr, ManagedWindow> _managed = [];
    private readonly Dictionary<int, WinEventHook[]> _processHooks = [];
    private readonly WinEventHook _foregroundHook;
    private readonly DispatcherTimer _snapTimer;
    private readonly DispatcherTimer _remeasureTimer;
    private readonly SnapLimiter _snapLimiter = new(4, TimeSpan.FromSeconds(3), () => DateTime.UtcNow);
    private ManagedWindow? _snapPending;
    private ProfileInstance? _active;
    private bool _syncQueued;
    private bool _disposed;

    public WindowHost(Window claudin, FrameworkElement hostArea, bool hideCaptionButtons, bool hideTaskbarButtons)
    {
        _claudin = claudin;
        _hostArea = hostArea;
        _hideCaptionButtons = hideCaptionButtons;
        _hideTaskbarButtons = hideTaskbarButtons;
        _ = _cover.Handle; // cria a janela agora, oculta
        _claudinHwnd = new WindowInteropHelper(claudin).EnsureHandle();
        _switchTimer = new DispatcherTimer(SwitchOverlap, DispatcherPriority.Normal, (_, _) => OnSwitchTimer(), claudin.Dispatcher);
        _switchTimer.Stop();
        _verifyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => VerifyOnTop(),
            claudin.Dispatcher);
        _verifyTimer.Stop();
        _captionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => OnCaptionTimer(),
            claudin.Dispatcher);
        _captionTimer.Stop();
        _colorTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            SampleCaptionColor();
            HideTaskbarTabs(); // o Explorer pode ter devolvido o botão (janela reexibida, por exemplo)
        }, claudin.Dispatcher);
        _colorTimer.Start();
        _foregroundHook = new WinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, 0,
            (_, hwnd, _, _) => OnForeground(hwnd));
        _snapTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => OnSnapTimer(),
            claudin.Dispatcher);
        _snapTimer.Stop();
        _remeasureTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => OnRemeasure(),
            claudin.Dispatcher);
        _remeasureTimer.Stop();
    }

    /// <summary>Uma janela de perfil inativo apareceu sozinha (link claude://, notificação, bandeja).</summary>
    public event Action<ProfileInstance>? AttentionRequested;

    /// <summary>O usuário escondeu a janela ativa (X do Claude, que a manda para a bandeja) ou ela voltou.</summary>
    public event Action<ProfileInstance>? HiddenByUserChanged;

    /// <summary>A janela em primeiro plano mudou (qualquer processo).</summary>
    public event Action<IntPtr>? ForegroundChanged;

    public ProfileInstance? Active => _active;

    public bool IsManaged(IntPtr hwnd) => _managed.ContainsKey(hwnd);

    public bool IsHiddenByUser(ProfileInstance inst) => Find(inst)?.HiddenByUser == true;

    /// <summary>A janela do perfil está hospedada e visível.</summary>
    public bool IsShowing(ProfileInstance inst) => Find(inst) is { } mw && NativeMethods.IsWindowVisible(mw.Hwnd);

    /// <summary>Cor do cabeçalho do Claude ativo, lida da tela (para o fundo do Claudin combinar com ele).</summary>
    public event Action<System.Windows.Media.Color>? ClaudeColorChanged;

    /// <summary>Passa a gerenciar (ou solta) a janela de uma instância. Chamar sempre que Window/State mudarem.</summary>
    public void Track(ProfileInstance inst)
    {
        if (_disposed)
        {
            return;
        }
        foreach (var stale in _managed.Values.Where(m => ReferenceEquals(m.Instance, inst)
                     && (m.Hwnd != inst.Window || !inst.IsAlive)).ToList())
        {
            Unmanage(stale, restore: false);
        }
        var hwnd = inst.Window;
        if (hwnd == IntPtr.Zero || !inst.IsAlive || inst.MainPid is not int pid || _managed.ContainsKey(hwnd))
        {
            return;
        }

        var mw = new ManagedWindow(inst, hwnd, pid);
        _managed[hwnd] = mw;
        EnsureHooks(pid);
        Log.Info($"Janela 0x{hwnd.ToInt64():X} do perfil '{inst.Profile.Id}' passou a ser hospedada");
        SyncToolWindow(mw);
        if (ReferenceEquals(inst, _active))
        {
            Place(mw, show: true, focus: false);
        }
        else
        {
            Hide(mw);
        }
        UpdateCover();
    }

    /// <summary>Tirar o botão do Claude da barra de tarefas, ficando só o do Claudin (configuração do usuário).</summary>
    public bool HideTaskbarButtons
    {
        get => _hideTaskbarButtons;
        set
        {
            if (_hideTaskbarButtons == value || _disposed)
            {
                return;
            }
            _hideTaskbarButtons = value;
            if (value)
            {
                HideTaskbarTabs();
            }
            else
            {
                foreach (var mw in _managed.Values)
                {
                    RestoreToolWindow(mw);
                }
            }
        }
    }

    /// <summary>Recortar os botões de janela do Claude (configuração do usuário).</summary>
    public bool HideCaptionButtons
    {
        get => _hideCaptionButtons;
        set
        {
            if (_hideCaptionButtons == value || _disposed)
            {
                return;
            }
            _hideCaptionButtons = value;
            if (value)
            {
                ScheduleCaption();
            }
            else
            {
                foreach (var mw in _managed.Values)
                {
                    ClearCaption(mw);
                }
            }
            UpdateCover();
        }
    }

    /// <summary>
    /// Troca o perfil ativo. A janela nova entra por cima da que está visível e só depois a anterior é escondida, para o
    /// fundo do Claudin não aparecer no meio da troca. O foco vai para a nova só depois, para o Claudin não subir para
    /// logo abaixo dela (o que deixaria o fundo dele à mostra enquanto ela ainda não se desenhou).
    /// </summary>
    public void Activate(ProfileInstance? inst, bool focus)
    {
        if (_disposed)
        {
            return;
        }
        _active = inst;
        _switchTimer.Stop();
        _pendingFocus = false;
        if (inst is null || Find(inst) is not { } active)
        {
            HideInactive();
            UpdateCover();
            return;
        }

        SetHiddenByUser(active, false);
        _snapLimiter.Reset();
        var previous = _managed.Values.FirstOrDefault(m => !ReferenceEquals(m, active) && NativeMethods.IsWindowVisible(m.Hwnd));
        Place(active, show: true, focus: focus && previous is null, above: previous?.Hwnd ?? IntPtr.Zero);
        if (previous is not null)
        {
            _pendingFocus = focus;
            _switchTimer.Start();
        }
    }

    private void OnSwitchTimer()
    {
        _switchTimer.Stop();
        HideInactive();
        if (_pendingFocus && _active is not null && Find(_active) is { } mw && !mw.HiddenByUser
            && NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            NativeMethods.SetForegroundWindow(mw.Hwnd);
        }
        _pendingFocus = false;
        UpdateCover();
    }

    private void HideInactive()
    {
        foreach (var mw in _managed.Values.Where(m => !ReferenceEquals(m.Instance, _active)))
        {
            Hide(mw);
        }
    }

    /// <summary>Reposiciona a janela ativa (Claudin movido, redimensionado, minimizado, restaurado, DPI mudou).</summary>
    public void RequestSync()
    {
        if (_syncQueued || _disposed)
        {
            return;
        }
        _syncQueued = true;
        _claudin.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _syncQueued = false;
            Sync();
        });
    }

    /// <summary>
    /// O Claudin foi ativado: coloca o Claude logo acima dele. Se a ativação não veio de um clique na barra
    /// (Alt+Tab, barra de tarefas), devolve o foco ao Claude.
    /// </summary>
    public void OnClaudinActivated()
    {
        if (_active is null || Find(_active) is not { } mw || mw.HiddenByUser || !NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            return;
        }
        NativeMethods.SetWindowPos(mw.Hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE
            | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_ASYNCWINDOWPOS);
        var mouseDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (!mouseDown)
        {
            NativeMethods.SetForegroundWindow(mw.Hwnd);
        }
        ScheduleCaption(); // a capa volta para cima do Claude depois que ele subir
        ScheduleVerify();
    }

    // ---------- Conferência: o Claude está mesmo à vista? ----------

    /// <summary>Confere, logo depois, se o Claude ativo cobre a área do Claudin (até 3 tentativas por pedido).</summary>
    private void ScheduleVerify()
    {
        if (_disposed)
        {
            return;
        }
        _verifyAttempts = 0;
        _verifyTimer.Stop();
        _verifyTimer.Start();
    }

    /// <summary>
    /// Bug relatado: ao voltar ao Claudin pela barra de tarefas, a área ficava vazia (o Claude "aberto", mas não à vista).
    /// Se o Claudin ou o Claude estão em primeiro plano e o centro da área não é do Claude, reposiciona o Claude por cima
    /// e registra o que estava no lugar (classe da janela, nunca o título).
    /// </summary>
    private void VerifyOnTop()
    {
        _verifyTimer.Stop();
        if (_disposed || _active is null || Find(_active) is not { HiddenByUser: false } mw
            || _claudin.WindowState == WindowState.Minimized || !_claudin.IsVisible || GetHostRect() is not { } host)
        {
            return;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _claudinHwnd && foreground != mw.Hwnd)
        {
            return; // o usuário está em outro aplicativo: não é para o Claude ficar por cima
        }
        var center = new NativeMethods.POINT { X = host.X + host.Width / 2, Y = host.Y + host.Height / 2 };
        var root = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(center), NativeMethods.GA_ROOT);
        if (root == mw.Hwnd || (root != IntPtr.Zero && NativeMethods.GetWindowThreadProcessId(root, out var rootPid) != 0
                                 && rootPid == mw.Pid))
        {
            return; // o Claude, ou uma janela dele (menu, diálogo), está à vista
        }
        _ = NativeMethods.DwmGetWindowAttributeInt(mw.Hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        var what = root == _claudinHwnd ? "o próprio Claudin" : root == IntPtr.Zero ? "nada" : $"uma janela {ClassOf(root)}";
        Log.Warn($"O Claude do perfil '{mw.Instance.Profile.Id}' não estava à vista no Claudin (no lugar: {what}; " +
                 $"visível={NativeMethods.IsWindowVisible(mw.Hwnd)}, minimizada={NativeMethods.IsIconic(mw.Hwnd)}, " +
                 $"oculta pelo DWM={cloaked != 0}). Reposicionando.");
        if (NativeMethods.IsIconic(mw.Hwnd) || !NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            Suppress(mw);
            NativeMethods.ShowWindowAsync(mw.Hwnd, NativeMethods.IsIconic(mw.Hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOWNA);
        }
        Suppress(mw);
        mw.Expected = WindowGeometry.Expand(host, mw.Margins);
        NativeMethods.SetWindowPos(mw.Hwnd, NativeMethods.HWND_TOP, mw.Expected.X, mw.Expected.Y, mw.Expected.Width, mw.Expected.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_ASYNCWINDOWPOS);
        if (foreground == _claudinHwnd)
        {
            NativeMethods.SetForegroundWindow(mw.Hwnd);
        }
        ScheduleCaption();
        if (++_verifyAttempts < 3)
        {
            _verifyTimer.Start(); // confere de novo
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(128);
        return NativeMethods.GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "?";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _snapTimer.Stop();
        _remeasureTimer.Stop();
        _switchTimer.Stop();
        _verifyTimer.Stop();
        _captionTimer.Stop();
        _colorTimer.Stop();
        _foregroundHook.Dispose();
        foreach (var mw in _managed.Values.ToList())
        {
            Unmanage(mw, restore: true);
        }
        _cover.Dispose();
    }

    // ---------- Posicionamento ----------

    private void Sync()
    {
        if (_disposed || _active is null || Find(_active) is not { } mw)
        {
            return;
        }
        if (_claudin.WindowState == WindowState.Minimized || !_claudin.IsVisible)
        {
            Hide(mw);
            UpdateCover();
            return;
        }
        var wasHidden = !NativeMethods.IsWindowVisible(mw.Hwnd);
        Place(mw, show: !mw.HiddenByUser, focus: false);
        if (wasHidden && !mw.HiddenByUser)
        {
            ScheduleVerify(); // voltando de minimizado ou da bandeja
        }
    }

    /// <param name="above">Na troca de perfil: a janela anterior, que a nova deve cobrir. Zero = logo acima do Claudin.</param>
    private void Place(ManagedWindow mw, bool show, bool focus, IntPtr above = default)
    {
        if (!NativeMethods.IsWindow(mw.Hwnd))
        {
            return;
        }
        if (_claudin.WindowState == WindowState.Minimized || !_claudin.IsVisible)
        {
            Hide(mw); // Claudin minimizado ou na bandeja
            return;
        }
        if (GetHostRect() is not { } host)
        {
            return;
        }
        if (mw.HiddenByUser)
        {
            show = false;
        }

        if (NativeMethods.IsIconic(mw.Hwnd) || NativeMethods.IsZoomed(mw.Hwnd))
        {
            Suppress(mw);
            NativeMethods.ShowWindowAsync(mw.Hwnd, NativeMethods.SW_RESTORE);
        }

        var wasVisible = NativeMethods.IsWindowVisible(mw.Hwnd);
        if (wasVisible)
        {
            MeasureMargins(mw);
        }
        var target = WindowGeometry.Expand(host, mw.Margins);

        // Ordem Z: logo acima do Claudin (abaixo da janela que estiver acima dele), ou logo acima da janela anterior
        // na troca de perfil. Nunca acima de outros apps.
        var reference = above != IntPtr.Zero ? above : _claudinHwnd;
        var prev = NativeMethods.GetWindow(reference, NativeMethods.GW_HWNDPREV);
        var insertAfter = prev == IntPtr.Zero ? NativeMethods.HWND_TOP : prev;
        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_ASYNCWINDOWPOS;
        if (show)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }
        if (prev == mw.Hwnd || (!show && !wasVisible))
        {
            flags |= NativeMethods.SWP_NOZORDER; // já está no lugar, ou continua oculta
        }

        Suppress(mw);
        mw.Expected = target;
        if (!NativeMethods.SetWindowPos(mw.Hwnd, insertAfter, target.X, target.Y, target.Width, target.Height, flags))
        {
            Log.Warn($"SetWindowPos falhou para 0x{mw.Hwnd.ToInt64():X} (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
        if (focus && show)
        {
            NativeMethods.SetForegroundWindow(mw.Hwnd);
        }
        if (show && !wasVisible)
        {
            // As bordas só podem ser medidas com a janela visível: mede logo depois e corrige se precisar.
            _remeasureTimer.Stop();
            _remeasureTimer.Start();
        }
        if (show || wasVisible)
        {
            if (ReferenceEquals(mw.Instance, _active))
            {
                UpdateCover(target); // acompanha o Claudin sendo arrastado, sem esperar o timer
            }
            ScheduleCaption();
        }
    }

    private void Hide(ManagedWindow mw)
    {
        if (!NativeMethods.IsWindow(mw.Hwnd) || !NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            return;
        }
        Suppress(mw);
        NativeMethods.ShowWindowAsync(mw.Hwnd, NativeMethods.SW_HIDE);
    }

    private PixelRect? GetHostRect()
    {
        if (!_hostArea.IsLoaded || _hostArea.ActualWidth < 1 || _hostArea.ActualHeight < 1
            || PresentationSource.FromVisual(_hostArea) is null)
        {
            return null;
        }
        // Em Per-Monitor V2, PointToScreen devolve pixels físicos. A moldura na cor da conta (borda da área host) fica
        // de fora: o Claude ocupa só o miolo.
        var frame = (_hostArea as System.Windows.Controls.Border)?.BorderThickness ?? default;
        var topLeft = _hostArea.PointToScreen(new Point(frame.Left, frame.Top));
        var bottomRight = _hostArea.PointToScreen(new Point(Math.Max(frame.Left, _hostArea.ActualWidth - frame.Right),
            Math.Max(frame.Top, _hostArea.ActualHeight - frame.Bottom)));
        return PixelRect.FromLtrb((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
    }

    private static void MeasureMargins(ManagedWindow mw)
    {
        if (NativeMethods.IsIconic(mw.Hwnd) || NativeMethods.IsZoomed(mw.Hwnd)
            || !NativeMethods.GetWindowRect(mw.Hwnd, out var w)
            || NativeMethods.DwmGetWindowAttribute(mw.Hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var f,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return;
        }
        mw.Margins = FrameMargins.FromRects(
            PixelRect.FromLtrb(w.Left, w.Top, w.Right, w.Bottom),
            PixelRect.FromLtrb(f.Left, f.Top, f.Right, f.Bottom));
    }

    private void OnRemeasure()
    {
        _remeasureTimer.Stop();
        if (_active is null || Find(_active) is not { } mw || !NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            return;
        }
        var before = mw.Margins;
        MeasureMargins(mw);
        if (mw.Margins != before)
        {
            Place(mw, show: false, focus: false);
        }
        ScheduleCaption();
    }

    // ---------- Botões de janela do Claude ----------

    /// <summary>Logo depois de posicionar ou mostrar o Claude: recorte, capa e botão da barra de tarefas.</summary>
    private void ScheduleCaption()
    {
        if ((!_hideCaptionButtons && !_hideTaskbarButtons) || _disposed)
        {
            return;
        }
        _captionTimer.Stop();
        _captionTimer.Start();
    }

    private void OnCaptionTimer()
    {
        _captionTimer.Stop();
        if (_active is not null && Find(_active) is { } mw)
        {
            ApplyCaption(mw);
            SampleCaptionColor();
        }
        UpdateCover();
        HideTaskbarTabs();
    }

    // ---------- Botão do Claude na barra de tarefas ----------

    // Pedir ao Explorer para tirar o botão (ITaskbarList::DeleteTab) não funcionou neste Windows para janelas de outro
    // processo. O que funciona é a janela ser "de ferramenta" (WS_EX_TOOLWINDOW): o Explorer não dá botão a ela.
    // O Explorer só reavalia quando a janela reaparece, por isso a janela visível é escondida e mostrada uma vez.
    // A janela principal do Claude nunca tem esse estilo sozinha: se já tiver, foi o Claudin (inclusive um anterior,
    // que fechou sem desfazer), e é desfeito ao soltar a janela ou desligar a opção.

    /// <summary>Garante o estilo de cada janela hospedada conforme a opção. Repetir não tem efeito colateral.</summary>
    private void HideTaskbarTabs()
    {
        if (!_hideTaskbarButtons || _disposed)
        {
            return;
        }
        foreach (var mw in _managed.Values)
        {
            ApplyToolWindow(mw);
        }
    }

    private void SyncToolWindow(ManagedWindow mw)
    {
        if (_hideTaskbarButtons)
        {
            ApplyToolWindow(mw);
        }
        else if (HasToolWindowStyle(mw.Hwnd))
        {
            mw.ToolWindowApplied = true; // sobrou de um Claudin que fechou sem desfazer
            RestoreToolWindow(mw);
        }
    }

    private void ApplyToolWindow(ManagedWindow mw)
    {
        if (mw.ToolWindowPending || !NativeMethods.IsWindow(mw.Hwnd))
        {
            return;
        }
        var ex = NativeMethods.GetWindowLongPtr(mw.Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            mw.ToolWindowApplied = true;
            return;
        }
        if (NativeMethods.IsHungAppWindow(mw.Hwnd))
        {
            return;
        }
        mw.ToolWindowApplied = true;
        mw.ToolWindowPending = true;
        mw.HadAppWindow = (ex & NativeMethods.WS_EX_APPWINDOW) != 0;
        var hwnd = mw.Hwnd;
        var visible = NativeMethods.IsWindowVisible(hwnd);
        var wasForeground = NativeMethods.GetForegroundWindow() == hwnd;
        if (visible)
        {
            Suppress(mw); // o esconder/mostrar abaixo não é o usuário fechando a janela
        }
        var newEx = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        _ = Task.Run(() =>
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newEx));
            if (visible)
            {
                RefreshTaskbarEntry(hwnd, wasForeground);
            }
        }).ContinueWith(_ => mw.ToolWindowPending = false, TaskScheduler.FromCurrentSynchronizationContext());
        Log.Info($"Janela 0x{hwnd.ToInt64():X} do perfil '{mw.Instance.Profile.Id}' fora da barra de tarefas");
    }

    /// <summary>Desfaz o estilo, só se foi o Claudin que o aplicou. A janela visível ganha o botão de volta.</summary>
    private void RestoreToolWindow(ManagedWindow mw)
    {
        if (!mw.ToolWindowApplied)
        {
            return;
        }
        mw.ToolWindowApplied = false;
        if (!NativeMethods.IsWindow(mw.Hwnd) || NativeMethods.IsHungAppWindow(mw.Hwnd))
        {
            return;
        }
        var ex = NativeMethods.GetWindowLongPtr(mw.Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) == 0)
        {
            return;
        }
        var newEx = ex & ~NativeMethods.WS_EX_TOOLWINDOW;
        if (mw.HadAppWindow)
        {
            newEx |= NativeMethods.WS_EX_APPWINDOW;
        }
        NativeMethods.SetWindowLongPtr(mw.Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newEx));
        if (NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            Suppress(mw);
            RefreshTaskbarEntry(mw.Hwnd, NativeMethods.GetForegroundWindow() == mw.Hwnd);
        }
    }

    private static bool HasToolWindowStyle(IntPtr hwnd) =>
        NativeMethods.IsWindow(hwnd)
        && (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    /// <summary>Esconde e mostra de novo, no mesmo lugar e na mesma ordem Z, para o Explorer reavaliar o botão.</summary>
    private static void RefreshTaskbarEntry(IntPtr hwnd, bool wasForeground)
    {
        NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_HIDE);
        NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_SHOWNA);
        if (wasForeground)
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Recorta o canto dos botões, a partir do tamanho real da janela (o Claude pode recusar ficar menor).</summary>
    private void ApplyCaption(ManagedWindow mw)
    {
        if (!_hideCaptionButtons || !NativeMethods.IsWindow(mw.Hwnd) || !NativeMethods.IsWindowVisible(mw.Hwnd)
            || NativeMethods.IsIconic(mw.Hwnd) || NativeMethods.IsZoomed(mw.Hwnd) || NativeMethods.IsHungAppWindow(mw.Hwnd)
            || CurrentRect(mw) is not { } rect)
        {
            return;
        }
        MeasureMargins(mw);
        var hole = CaptionButtons.Hole(rect.Width, mw.Margins, NativeMethods.GetDpiForWindow(mw.Hwnd));
        if (hole.Width <= 0 || (mw.Hole == hole && HasRegion(mw.Hwnd)))
        {
            return;
        }
        mw.Hole = hole;
        var hwnd = mw.Hwnd;
        var (width, height) = (rect.Width, rect.Height);
        // Fora da thread da interface: SetWindowRgn conversa com a janela do Claude e não pode travar o Claudin.
        _ = Task.Run(() =>
        {
            var full = NativeMethods.CreateRectRgn(0, 0, width, height);
            var cut = NativeMethods.CreateRectRgn(hole.X, hole.Y, hole.Right, hole.Bottom);
            NativeMethods.CombineRgn(full, full, cut, NativeMethods.RGN_DIFF);
            NativeMethods.DeleteObject(cut);
            if (NativeMethods.SetWindowRgn(hwnd, full, true) == 0)
            {
                NativeMethods.DeleteObject(full);
                Log.Warn($"SetWindowRgn falhou para 0x{hwnd.ToInt64():X} (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
        });
    }

    /// <summary>Desfaz o recorte, só se foi o Claudin que o fez.</summary>
    private static void ClearCaption(ManagedWindow mw)
    {
        if (mw.Hole is null)
        {
            return;
        }
        mw.Hole = null;
        if (NativeMethods.IsWindow(mw.Hwnd) && !NativeMethods.IsHungAppWindow(mw.Hwnd)
            && NativeMethods.SetWindowRgn(mw.Hwnd, IntPtr.Zero, true) == 0)
        {
            Log.Warn($"Não foi possível desfazer o recorte da janela 0x{mw.Hwnd.ToInt64():X}");
        }
    }

    private static bool HasRegion(IntPtr hwnd)
    {
        var probe = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        try
        {
            return NativeMethods.GetWindowRgn(hwnd, probe) != NativeMethods.REGION_ERROR;
        }
        finally
        {
            NativeMethods.DeleteObject(probe);
        }
    }

    /// <summary>
    /// Mostra a capa logo acima do Claude ativo, sobre o recorte, ou a esconde. "Logo acima" = abaixo da janela que
    /// estiver acima do Claude (os menus e avisos do próprio Claude continuam por cima da capa).
    /// </summary>
    /// <param name="windowRect">Retângulo da janela do Claude, quando já se sabe (acabou de ser posicionada).</param>
    private void UpdateCover(PixelRect? windowRect = null)
    {
        if (_disposed)
        {
            return;
        }
        if (!_hideCaptionButtons || _active is null || Find(_active) is not { HiddenByUser: false, Hole: { } hole } mw
            || _claudin.WindowState == WindowState.Minimized || !_claudin.IsVisible
            || !NativeMethods.IsWindowVisible(mw.Hwnd) || NativeMethods.IsIconic(mw.Hwnd)
            || (windowRect ?? CurrentRect(mw)) is not { } rect)
        {
            HideCover();
            return;
        }
        var coverHwnd = _cover.Handle;
        var prev = NativeMethods.GetWindow(mw.Hwnd, NativeMethods.GW_HWNDPREV);
        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW;
        var insertAfter = prev == IntPtr.Zero ? NativeMethods.HWND_TOP : prev;
        if (prev == coverHwnd)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }
        NativeMethods.SetWindowPos(coverHwnd, insertAfter, rect.X + hole.X, rect.Y + hole.Y, hole.Width, hole.Height, flags);
    }

    private void HideCover()
    {
        if (_cover.IsHandleCreated && NativeMethods.IsWindowVisible(_cover.Handle))
        {
            NativeMethods.ShowWindow(_cover.Handle, NativeMethods.SW_HIDE);
        }
    }

    /// <summary>
    /// Copia a cor do cabeçalho do Claude (um pixel logo à esquerda do recorte) para a capa, para o canto ficar contínuo
    /// nos temas claro e escuro. Só lê o pixel quando ele é mesmo da janela do Claude ativo.
    /// </summary>
    private void SampleCaptionColor()
    {
        if (!_hideCaptionButtons || _disposed || _active is null || Find(_active) is not { Hole: { } hole } mw
            || !NativeMethods.IsWindowVisible(mw.Hwnd) || CurrentRect(mw) is not { } rect)
        {
            return;
        }
        var scale = NativeMethods.GetDpiForWindow(mw.Hwnd) / 96.0;
        var offset = Math.Max(2, (int)Math.Round(3 * scale));
        var pt = new NativeMethods.POINT { X = rect.X + hole.X - offset, Y = rect.Y + hole.Y + offset };
        if (NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(pt), NativeMethods.GA_ROOT) != mw.Hwnd)
        {
            return; // outra janela por cima desse ponto
        }
        var dc = NativeMethods.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return;
        }
        uint color;
        try
        {
            color = NativeMethods.GetPixel(dc, pt.X, pt.Y);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, dc);
        }
        if (color == NativeMethods.CLR_INVALID || color == _patchColor)
        {
            return;
        }
        _patchColor = color;
        var (r, g, b) = ((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF));
        _cover.BackColor = System.Drawing.Color.FromArgb(r, g, b);
        ClaudeColorChanged?.Invoke(System.Windows.Media.Color.FromRgb(r, g, b));
    }

    private static PixelRect? CurrentRect(ManagedWindow mw) =>
        NativeMethods.GetWindowRect(mw.Hwnd, out var r) ? PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom) : null;

    // ---------- Eventos das janelas do Claude ----------

    private void EnsureHooks(int pid)
    {
        if (_processHooks.ContainsKey(pid))
        {
            return;
        }
        _processHooks[pid] =
        [
            new WinEventHook(NativeMethods.EVENT_SYSTEM_MOVESIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, pid, OnWinEvent),
            new WinEventHook(NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_HIDE, pid, OnWinEvent),
            new WinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, pid, OnWinEvent),
        ];
    }

    private void OnWinEvent(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (_disposed || idObject != NativeMethods.OBJID_WINDOW || idChild != 0 || !_managed.TryGetValue(hwnd, out var mw))
        {
            return;
        }
        var suppressed = DateTime.UtcNow < mw.SuppressUntil;
        var isActive = ReferenceEquals(mw.Instance, _active);
        if (ev == NativeMethods.EVENT_OBJECT_SHOW)
        {
            ScheduleCaption(); // ao reaparecer, o Explorer devolve o botão da barra de tarefas
        }

        switch (ev)
        {
            case NativeMethods.EVENT_OBJECT_SHOW when !suppressed:
                if (isActive)
                {
                    SetHiddenByUser(mw, false);
                    Place(mw, show: true, focus: false);
                }
                else
                {
                    Log.Info($"Janela do perfil '{mw.Instance.Profile.Id}' apareceu sozinha; pedindo atenção");
                    AttentionRequested?.Invoke(mw.Instance);
                }
                break;

            case NativeMethods.EVENT_OBJECT_HIDE when !suppressed && isActive:
                if (_claudin.WindowState != WindowState.Minimized)
                {
                    SetHiddenByUser(mw, true); // X do Claude: ele vai para a bandeja
                }
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART when !suppressed && isActive:
                _claudin.WindowState = WindowState.Minimized; // o minimizar do Claude minimiza o conjunto
                break;

            case NativeMethods.EVENT_SYSTEM_MOVESIZESTART:
                mw.InMoveSize = true;
                break;

            case NativeMethods.EVENT_SYSTEM_MOVESIZEEND:
                mw.InMoveSize = false;
                if (isActive)
                {
                    ScheduleSnap(mw);
                }
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE when isActive && !mw.InMoveSize:
                if (NativeMethods.IsZoomed(hwnd) && !suppressed)
                {
                    // Maximizar o Claude = maximizar o Claudin, com o Claude encaixado.
                    Suppress(mw);
                    NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                    if (_claudin.WindowState != WindowState.Maximized)
                    {
                        _claudin.WindowState = WindowState.Maximized;
                    }
                    RequestSync();
                }
                else if (CurrentRect(mw) is { } rect && rect != mw.Expected && !NativeMethods.IsIconic(hwnd))
                {
                    ScheduleSnap(mw);
                }
                break;
        }
    }

    /// <summary>Decisão da Fase 3: janela movida ou redimensionada por fora volta para o lugar (com debounce).</summary>
    private void ScheduleSnap(ManagedWindow mw)
    {
        _snapPending = mw;
        _snapTimer.Stop();
        _snapTimer.Start();
    }

    private void OnSnapTimer()
    {
        _snapTimer.Stop();
        var mw = _snapPending;
        _snapPending = null;
        if (mw is null || !ReferenceEquals(mw.Instance, _active) || mw.InMoveSize || !_managed.ContainsKey(mw.Hwnd)
            || CurrentRect(mw) is not { } rect || rect == mw.Expected)
        {
            return;
        }
        if (!_snapLimiter.TryAcquire())
        {
            Log.Warn($"Janela 0x{mw.Hwnd.ToInt64():X} insiste em {rect} (esperado {mw.Expected}); parando de corrigir por ora");
            return;
        }
        Place(mw, show: false, focus: false);
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }
        // O Claude ativo foi ativado (clique nele, por exemplo): o Claudin vai para logo abaixo, para a barra ficar visível.
        if (_managed.TryGetValue(hwnd, out var mw) && ReferenceEquals(mw.Instance, _active))
        {
            NativeMethods.SetWindowPos(_claudinHwnd, hwnd, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            UpdateCover(); // o Claude subiu por cima da capa: ela volta para cima dele
        }
        ForegroundChanged?.Invoke(hwnd);
    }

    // ---------- Infra ----------

    private ManagedWindow? Find(ProfileInstance inst) => _managed.Values.FirstOrDefault(m => ReferenceEquals(m.Instance, inst));

    private static void Suppress(ManagedWindow mw) => mw.SuppressUntil = DateTime.UtcNow + SuppressFor;

    private void SetHiddenByUser(ManagedWindow mw, bool value)
    {
        if (mw.HiddenByUser == value)
        {
            return;
        }
        mw.HiddenByUser = value;
        UpdateCover();
        HiddenByUserChanged?.Invoke(mw.Instance);
    }

    private void Unmanage(ManagedWindow mw, bool restore)
    {
        _managed.Remove(mw.Hwnd);
        ClearCaption(mw); // a janela do Claude volta ao normal, mesmo se o Claudin só deixou de hospedá-la
        RestoreToolWindow(mw); // antes de reexibir as ocultas abaixo, para elas voltarem com o botão
        UpdateCover();
        if (restore && !mw.HiddenByUser && NativeMethods.IsWindow(mw.Hwnd) && !NativeMethods.IsWindowVisible(mw.Hwnd))
        {
            // Ao sair, janelas escondidas pelo Claudin voltam a aparecer.
            NativeMethods.ShowWindowAsync(mw.Hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
        if (_managed.Values.All(m => m.Pid != mw.Pid) && _processHooks.Remove(mw.Pid, out var hooks))
        {
            foreach (var h in hooks)
            {
                h.Dispose();
            }
        }
        Log.Info($"Janela 0x{mw.Hwnd.ToInt64():X} do perfil '{mw.Instance.Profile.Id}' deixou de ser hospedada");
    }

    private sealed class ManagedWindow(ProfileInstance instance, IntPtr hwnd, int pid)
    {
        public ProfileInstance Instance { get; } = instance;

        public IntPtr Hwnd { get; } = hwnd;

        public int Pid { get; } = pid;

        public FrameMargins Margins { get; set; }

        public PixelRect Expected { get; set; }

        public DateTime SuppressUntil { get; set; }

        public bool InMoveSize { get; set; }

        public bool HiddenByUser { get; set; }

        /// <summary>Recorte aplicado pelo Claudin (relativo à janela), ou nulo.</summary>
        public PixelRect? Hole { get; set; }

        /// <summary>O Claudin marcou esta janela como "de ferramenta" (sem botão na barra de tarefas).</summary>
        public bool ToolWindowApplied { get; set; }

        /// <summary>A mudança de estilo está em andamento fora da thread da interface.</summary>
        public bool ToolWindowPending { get; set; }

        /// <summary>A janela tinha WS_EX_APPWINDOW antes (devolvido ao desfazer).</summary>
        public bool HadAppWindow { get; set; }
    }
}
