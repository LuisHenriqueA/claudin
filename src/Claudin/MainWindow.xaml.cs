using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Claudin.Core;
using Claudin.Hosting;
using Claudin.Interop;

namespace Claudin;

public partial class MainWindow : Window
{
    private enum HostAction { None, Open, ShowWindow, ForceRestart }

    private enum LoginAction { None, Cancel, Complete, GoToTarget, CodeCopied }

    private enum TransferAction { None, CopyPromptAgain, Cancel, Finish, CopyDocumentAgain }

    /// <summary>Só para testes manuais: id do perfil tratado como receptor dos links claude://.</summary>
    private const string DevLinkProfileVariable = "CLAUDIN_DEV_LINK_PROFILE";

    private readonly AppState _state;
    private readonly InstanceManager _instances;
    private readonly ObservableCollection<ProfileButtonViewModel> _profiles = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _usageTimer;
    private DateTime _usagePopupClosedAt;
    private readonly LoginAssistant _login = new(() => DateTime.UtcNow);
    private readonly ConversationTransfer _transfer = new(() => DateTime.UtcNow);
    private uint _ownClipboardSequence;
    private string? _fallbackDocument;
    private bool _transferFolderMissing;
    private readonly Dictionary<ProfileInstance, bool> _wasAlive = [];
    private WindowHost? _host;
    private DateTime _lastSwitch;
    private const double ProfileFrameThickness = 2;
    private Brush _claudeBackground = Frozen(Color.FromRgb(0x19, 0x19, 0x19)); // cabeçalho do Claude no tema escuro
    private ProfileHotkeys? _hotkeys;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _startupAdoptionDone;
    private bool _clipboardListening;
    private bool _runtimeStarted;
    private bool _closingForExit;

    /// <summary>O usuário fechou a janela e o Claudin deve sair (não está configurado para ir à bandeja).</summary>
    public event Action? ExitRequested;

    public ProfileInstance? ActiveInstance => ActiveProfile?.Instance;

    public MainWindow(AppState state, InstanceManager instances)
    {
        _state = state;
        _instances = instances;
        InitializeComponent();

        foreach (var inst in instances.Instances)
        {
            var isActive = string.Equals(inst.Profile.Id, state.Config.ActiveProfileId, StringComparison.OrdinalIgnoreCase);
            _profiles.Add(new ProfileButtonViewModel(inst, isActive));
            inst.PropertyChanged += OnInstanceChanged;
        }
        ProfileList.ItemsSource = _profiles;
        UsageList.ItemsSource = _profiles;
        RefreshUsage();
        // Para a dica das abas; com o painel aberto, a leitura é mais frequente (OnUsagePopupOpened).
        _usageTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => RefreshUsage(), Dispatcher);
        _usageTimer.Start();

        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            SafeRefresh();
            _login.CheckTimeout();
            _transfer.CheckTimeout();
        }, Dispatcher);

        _login.PropertyChanged += (_, _) =>
        {
            RenderLoginStrip();
            UpdateClipboardListener();
        };
        _transfer.PropertyChanged += (_, _) =>
        {
            RenderTransferStrip();
            UpdateClipboardListener();
        };

        RenderClaudeDiagnostics();
        Render();
        RenderLoginStrip();
        RenderTransferStrip();

        if (state.Config.Window is { IsPlausible: true })
        {
            WindowStartupLocation = WindowStartupLocation.Manual; // a posição salva é aplicada em SourceInitialized
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => StartRuntime();
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += (_, _) =>
        {
            UpdateMaximizedLayout();
            _host?.RequestSync();
        };
        DpiChanged += (_, _) =>
        {
            UpdateMaximizedLayout();
            _host?.RequestSync();
        };
        LocationChanged += (_, _) => _host?.RequestSync();
        HostArea.SizeChanged += (_, _) => _host?.RequestSync();
        IsVisibleChanged += (_, _) => _host?.RequestSync();
        Activated += (_, _) =>
        {
            _host?.OnClaudinActivated();
            _hotkeys?.SetEnabled(true);
        };
    }

    private ProfileButtonViewModel? ActiveProfile => _profiles.FirstOrDefault(p => p.IsActive);

    /// <summary>Mostra a janela, ou, em segundo plano (início com o Windows), só cria o handle e fica na bandeja.</summary>
    public void Start(bool background)
    {
        if (background)
        {
            new WindowInteropHelper(this).EnsureHandle();
            StartRuntime();
            Log.Info("Iniciado em segundo plano (bandeja)");
        }
        else
        {
            Show();
        }
    }

    /// <summary>Adota instâncias abertas, liga o acompanhamento e, se configurado, abre o perfil ativo. Roda uma vez.</summary>
    private void StartRuntime()
    {
        if (_runtimeStarted)
        {
            return;
        }
        _runtimeStarted = true;
        SafeRefresh(); // adota instâncias que já estavam abertas, sem tratar como "pedido de atenção"
        _startupAdoptionDone = true;
        _refreshTimer.Start();
        if (_state.Config.OpenActiveProfileOnStart && _state.Claude.Found && ActiveProfile?.Instance is { IsAlive: false } active)
        {
            _ = OpenAsync(active);
        }
    }

    /// <summary>Fecha de verdade (pedido pelo App ao sair), sem ir para a bandeja.</summary>
    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    /// <summary>Seleciona um perfil a partir da bandeja, abrindo a instância se precisar.</summary>
    public void SelectFromOutside(ProfileInstance inst)
    {
        if (_profiles.FirstOrDefault(p => ReferenceEquals(p.Instance, inst)) is { } vm)
        {
            _ = SelectProfileAsync(vm, start: true, focus: true);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingForExit)
        {
            e.Cancel = true;
            if (_state.Config.CloseToTray)
            {
                Hide(); // o Claude ativo também some (WindowHost esconde quando o Claudin não está visível)
            }
            else
            {
                // Não dá para chamar Close() de dentro do Closing: a saída roda logo depois.
                Dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
            }
            return;
        }
        SavePlacement();
    }

    private void SavePlacement()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        var wp = new NativeMethods.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(_hwnd, ref wp))
        {
            return;
        }
        var r = wp.rcNormalPosition;
        _state.Config.Window = new WindowBounds
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
            Maximized = WindowState == WindowState.Maximized,
        };
        try
        {
            _state.ConfigService.Save(_state.Config);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar a posição da janela", ex);
        }
    }

    private void RestorePlacement()
    {
        if (_state.Config.Window is not { IsPlausible: true } b)
        {
            return;
        }
        var rect = new NativeMethods.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        if (NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONULL) == IntPtr.Zero)
        {
            Log.Info("Posição salva fora dos monitores atuais; usando a posição padrão");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var wp = new NativeMethods.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
            showCmd = NativeMethods.SW_HIDE, // ainda não visível: só define o retângulo "normal"
            rcNormalPosition = rect,
        };
        NativeMethods.SetWindowPlacement(_hwnd, ref wp);
        if (b.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        RestorePlacement();
        UpdateMaximizedLayout();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        try
        {
            _host = new WindowHost(this, HostArea, _state.Config.HideClaudeCaptionButtons,
                _state.Config.HideClaudeTaskbarButtons);
            _host.AttentionRequested += HandleAttention;
            _host.HiddenByUserChanged += _ => Render();
            _host.ClaudeColorChanged += color =>
            {
                _claudeBackground = Frozen(color);
                Render();
            };
            _host.ForegroundChanged += fg => _hotkeys?.SetEnabled(fg == _hwnd || (_host?.IsManaged(fg) ?? false));
            _host.Activate(ActiveProfile?.Instance, focus: false);
            foreach (var inst in _instances.Instances)
            {
                _host.Track(inst);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar a hospedagem de janelas", ex);
        }
        try
        {
            _hotkeys = new ProfileHotkeys(_hwnd, _profiles.Count, index =>
            {
                if (index < _profiles.Count)
                {
                    _ = SelectProfileAsync(_profiles[index], start: true, focus: true);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao registrar os atalhos", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _usageTimer.Stop();
        if (_clipboardListening)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
            _clipboardListening = false;
        }
        _source?.RemoveHook(WndProc);
        _hotkeys?.Dispose();
        _host?.Dispose(); // janelas escondidas pelo Claudin voltam a aparecer; o Claude continua aberto
    }

    private void SafeRefresh()
    {
        try
        {
            _instances.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao atualizar as instâncias", ex);
        }
    }

    private void OnInstanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ProfileInstance inst && e.PropertyName is nameof(ProfileInstance.Window) or nameof(ProfileInstance.State))
        {
            var wasAlive = _wasAlive.GetValueOrDefault(inst);
            _wasAlive[inst] = inst.IsAlive;

            // Instância aberta por fora do Claudin depois que ele já estava rodando (menu Iniciar, link claude:// que
            // fez o Windows abrir a instância padrão): o usuário quer vê-la. Seleciona antes de hospedar, sem piscar.
            if (_startupAdoptionDone && !wasAlive && inst.IsAlive && inst.Adopted && !IsActiveProfile(inst))
            {
                HandleAttention(inst);
            }
            _host?.Track(inst);

            if (ReferenceEquals(_login.Target, inst) && inst.State is InstanceState.Exited or InstanceState.Failed)
            {
                _login.Cancel();
            }
        }
        Render();
    }

    private bool IsActiveProfile(ProfileInstance inst) => ReferenceEquals(ActiveProfile?.Instance, inst);

    /// <summary>Traz o Claudin para a frente (pedido de outra execução do Claudin).</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            // Cópias feitas pelo próprio Claudin não contam.
            if (NativeMethods.GetClipboardSequenceNumber() == _ownClipboardSequence)
            {
                return IntPtr.Zero;
            }
            // Login: só o aviso de que algo foi copiado; o conteúdo (o código de login) nunca é lido.
            if (_login.OnClipboardChanged() is { } target)
            {
                SelectInstance(target, focus: true);
            }
            // Levar conversa: lê a cópia, que só é aceita se for o documento de transferência.
            else if (_transfer.WatchClipboard)
            {
                OnTransferClipboard();
            }
        }
        return IntPtr.Zero;
    }

    // ---------- Consumo das contas (1.1) ----------

    private void RefreshUsage()
    {
        var now = DateTimeOffset.Now;
        foreach (var p in _profiles)
        {
            try
            {
                p.RefreshUsage(now);
            }
            catch (Exception ex)
            {
                Log.Warn($"Consumo: falha ao ler o histórico do perfil {p.Profile.Id}: {ex.GetType().Name}");
            }
        }
    }

    private void OnUsageButtonClick(object sender, RoutedEventArgs e)
    {
        // O clique no próprio botão já fechou o painel (StaysOpen=False): não reabrir na mesma hora.
        if (DateTime.UtcNow - _usagePopupClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }
        UsagePopup.IsOpen = !UsagePopup.IsOpen;
    }

    private void OnUsagePopupOpened(object? sender, EventArgs e)
    {
        RefreshUsage();
        _usageTimer.Interval = TimeSpan.FromSeconds(15);
    }

    private void OnUsagePopupClosed(object? sender, EventArgs e)
    {
        _usagePopupClosedAt = DateTime.UtcNow;
        _usageTimer.Interval = TimeSpan.FromMinutes(1);
    }

    private void OnUsageRefreshClick(object sender, RoutedEventArgs e) => RefreshUsage();

    // ---------- Levar conversa para outra conta (Fase 7) ----------

    private void OnTransferButtonClick(object sender, RoutedEventArgs e)
    {
        if (_transfer.Stage != TransferStage.None)
        {
            _transfer.Cancel();
            return;
        }
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = TransferButton };
        var source = ActiveProfile?.Instance;
        if (source is not { IsAlive: true })
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "Para levar uma conversa ou sessão, abra antes a conta de origem",
                IsEnabled = false,
            });
        }
        var targets = source is { IsAlive: true }
            ? _instances.Instances.Where(i => !ReferenceEquals(i, source)).ToList()
            : [];
        foreach (var target in targets)
        {
            var chat = new System.Windows.Controls.MenuItem { Header = $"Levar conversa do chat para {target.Profile.Name}" };
            chat.Click += (_, _) => StartTransfer(source!, target, TransferKind.Chat);
            menu.Items.Add(chat);
            var code = new System.Windows.Controls.MenuItem { Header = $"Levar sessão do Code para {target.Profile.Name}" };
            code.Click += (_, _) => StartTransfer(source!, target, TransferKind.Code);
            menu.Items.Add(code);
        }
        menu.IsOpen = true;
    }

    private void StartTransfer(ProfileInstance source, ProfileInstance target, TransferKind kind)
    {
        if (_state.Claude.Install is null)
        {
            MessageBox.Show(this, "Claude Desktop não encontrado.", "Levar conversa", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _transfer.Start(source, target, kind);
        if (!TrySetClipboard(ConversationTransfer.RequestPromptFor(kind)))
        {
            _transfer.Cancel();
            MessageBox.Show(this, "Não foi possível usar a área de transferência agora. Tente de novo.", "Levar conversa",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _host?.Activate(source, focus: true);
    }

    private void OnTransferClipboard()
    {
        if (_transfer.Target is not { } target || _state.Claude.Install is not { } install)
        {
            return;
        }
        var text = TryGetClipboardText();
        var baseLength = install.ExecutablePath.Length + 3 + CommandLine.BuildStartArguments(target.DataDir).Length;
        if (_transfer.OnClipboardText(text, baseLength, Directory.Exists) is not { } delivery)
        {
            return;
        }
        _transferFolderMissing = delivery.FolderMissing;
        _fallbackDocument = delivery.UsedFallback ? delivery.ClipboardText : null;
        if (_fallbackDocument is not null)
        {
            TrySetClipboard(_fallbackDocument);
        }
        try
        {
            // Mesmo mecanismo da Fase 0: abre (ou repassa para) a instância da pasta de destino.
            using var handle = new WindowsProcessHost().Launch(install.ExecutablePath,
                CommandLine.BuildStartArguments(target.DataDir) + " " + CommandLine.Quote(delivery.Url));
        }
        catch (Exception ex)
        {
            Log.Error("Levar conversa: falha ao abrir a conta de destino", ex);
            MessageBox.Show(this, $"Não foi possível abrir {target.Profile.Name}: {ex.Message}", "Levar conversa",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _transfer.Cancel();
            return;
        }
        SelectInstance(target, focus: true);
    }

    private void OnTransferPrimaryClick(object sender, RoutedEventArgs e) => RunTransferAction((TransferAction)TransferPrimary.Tag);

    private void OnTransferSecondaryClick(object sender, RoutedEventArgs e) => RunTransferAction((TransferAction)TransferSecondary.Tag);

    private void RunTransferAction(TransferAction action)
    {
        switch (action)
        {
            case TransferAction.CopyPromptAgain:
                TrySetClipboard(ConversationTransfer.RequestPromptFor(_transfer.Kind));
                if (_transfer.Source is { } source)
                {
                    SelectInstance(source, focus: true);
                }
                break;
            case TransferAction.CopyDocumentAgain:
                if (_fallbackDocument is not null)
                {
                    TrySetClipboard(_fallbackDocument);
                }
                break;
            case TransferAction.Cancel:
                _transfer.Cancel();
                break;
            case TransferAction.Finish:
                _fallbackDocument = null;
                _transfer.Finish();
                break;
        }
    }

    private void RenderTransferStrip()
    {
        var source = _transfer.Source?.Profile.Name ?? "";
        var target = _transfer.Target?.Profile.Name ?? "";
        var code = _transfer.Kind == TransferKind.Code;
        var what = code ? "sessão do Code" : "conversa";
        var where = code ? "sessão do Code" : "conversa";
        var opened = code ? $"uma sessão nova do Code em {target}, na mesma pasta do projeto" : $"um chat novo em {target}";
        var folderNote = code && _transferFolderMissing
            ? " A pasta citada no resumo não existe neste computador, então a sessão abriu sem pasta: escolha a pasta antes de enviar."
            : "";
        (string text, TransferAction primary, string primaryText, TransferAction secondary, string secondaryText) = _transfer.Stage switch
        {
            TransferStage.WaitingForDocument => (
                $"Levar {what} de {source} para {target}: o pedido de resumo foi copiado. Na {where} que você quer levar, " +
                "cole (Ctrl+V) e envie. Quando o Claude responder, clique em \"Copiar\" na resposta: o Claudin lê só essa cópia " +
                $"e abre {opened}, já com o contexto.",
                TransferAction.CopyPromptAgain, "Copiar o pedido de novo", TransferAction.Cancel, "Cancelar"),
            TransferStage.Delivered when _transfer.UsedFallback => (
                $"O documento é grande demais para ir pelo link, então ficou na área de transferência. Em {opened}, " +
                "clique no fim do texto, cole (Ctrl+V) e envie." + folderNote,
                TransferAction.Finish, "Concluído", TransferAction.CopyDocumentAgain, "Copiar de novo"),
            TransferStage.Delivered => (
                $"Pronto: abri {opened}, já com o contexto. Revise e envie. O aviso \"Tenha cuidado antes de executar este " +
                "prompt\" é do próprio Claude, para todo texto que chega por link." + folderNote,
                TransferAction.Finish, "Concluído", TransferAction.None, ""),
            _ => ("", TransferAction.None, "", TransferAction.None, ""),
        };
        TransferStrip.Visibility = _transfer.Stage == TransferStage.None ? Visibility.Collapsed : Visibility.Visible;
        TransferText.Text = text;
        TransferHint.Text = _transfer.Hint ?? "";
        TransferHint.Visibility = string.IsNullOrEmpty(_transfer.Hint) ? Visibility.Collapsed : Visibility.Visible;
        SetTransferAction(TransferPrimary, primary, primaryText);
        SetTransferAction(TransferSecondary, secondary, secondaryText);
        TransferButton.Foreground = _transfer.Stage == TransferStage.None
            ? (Brush)FindResource("TextBrush")
            : new SolidColorBrush(Color.FromRgb(0x7F, 0xB2, 0xFF));
        TransferButton.ToolTip = _transfer.Stage == TransferStage.None
            ? "Levar a conversa ou a sessão do Code aberta para outra conta (abre uma nova lá, já com o contexto)."
            : "Cancelar o \"Levar\" em andamento";
    }

    private static void SetTransferAction(System.Windows.Controls.Button button, TransferAction action, string text)
    {
        button.Tag = action;
        button.Content = text;
        button.Visibility = action == TransferAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Escreve na área de transferência e anota a sequência, para não reagir à própria cópia.</summary>
    private bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                _ownClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60); // outro programa está com a área de transferência aberta
            }
        }
        Log.Warn("Área de transferência ocupada; não foi possível copiar");
        return false;
    }

    private static string? TryGetClipboardText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
        return null;
    }

    // ---------- Assistente de login ----------

    private ProfileInstance? FindLinkReceiver()
    {
        var dev = Environment.GetEnvironmentVariable(DevLinkProfileVariable);
        if (!string.IsNullOrWhiteSpace(dev))
        {
            return _instances.Instances.FirstOrDefault(i => string.Equals(i.Profile.Id, dev, StringComparison.OrdinalIgnoreCase));
        }
        return _instances.Instances.FirstOrDefault(i => i.UsesClaudeDefaultDataDir);
    }

    private async void OnLoginButtonClick(object sender, RoutedEventArgs e)
    {
        if (_login.Stage != LoginStage.None)
        {
            _login.Cancel();
            return;
        }
        if (ActiveProfile?.Instance is not { } target)
        {
            return;
        }
        _login.Start(target, FindLinkReceiver());
        if (target.IsAlive)
        {
            _host?.Activate(target, focus: true);
        }
        else
        {
            await OpenAsync(target);
        }
    }

    private void OnLoginPrimaryClick(object sender, RoutedEventArgs e) => RunLoginAction((LoginAction)LoginPrimary.Tag);

    private void OnLoginSecondaryClick(object sender, RoutedEventArgs e) => RunLoginAction((LoginAction)LoginSecondary.Tag);

    private void RunLoginAction(LoginAction action)
    {
        switch (action)
        {
            case LoginAction.Cancel:
                _login.Cancel();
                break;
            case LoginAction.Complete:
                _login.Complete();
                break;
            case LoginAction.GoToTarget:
                if (_login.Target is { } target)
                {
                    SelectInstance(target, focus: true);
                }
                break;
            case LoginAction.CodeCopied:
                if (_login.MarkCodeCopied() is { } t)
                {
                    SelectInstance(t, focus: true);
                }
                break;
        }
    }

    private void RenderLoginStrip()
    {
        var target = _login.Target?.Profile.Name ?? "";
        var receiver = _login.LinkReceiver?.Profile.Name;
        (string text, LoginAction primary, string primaryText, LoginAction secondary, string secondaryText) = _login.Stage switch
        {
            LoginStage.WaitingForLink when _login.TargetReceivesLinks => (
                $"Login em {target}: entre normalmente na janela abaixo. O link de login abre direto nesta conta, sem código.",
                LoginAction.Complete, "Concluído", LoginAction.None, ""),
            LoginStage.WaitingForLink when receiver is null => (
                $"Login em {target}: entre com seu e-mail e abra o link recebido. O código vai aparecer na janela padrão " +
                "do Claude, fora do Claudin. Copie o código e clique em \"Copiei o código\".",
                LoginAction.CodeCopied, "Copiei o código", LoginAction.Cancel, "Cancelar"),
            LoginStage.WaitingForLink => (
                $"Login em {target}: na janela abaixo, entre com seu e-mail e abra o link recebido. " +
                $"O código vai aparecer em {receiver}, e o Claudin mostra essa conta para você.",
                LoginAction.Cancel, "Cancelar", LoginAction.None, ""),
            LoginStage.CodeShown => (
                $"{receiver} recebeu o link e mostra um código de login. Copie o código: o Claudin volta para {target} sozinho. " +
                "Não use \"Logar aqui mesmo\", isso só fecha a tela do código.",
                LoginAction.GoToTarget, $"Ir para {target}", LoginAction.Cancel, "Cancelar"),
            LoginStage.PasteCode => (
                $"Cole o código em {target} (Ctrl+V) para concluir o login.",
                LoginAction.Complete, "Concluído", LoginAction.Cancel, "Cancelar"),
            _ => ("", LoginAction.None, "", LoginAction.None, ""),
        };

        LoginStrip.Visibility = _login.Stage == LoginStage.None ? Visibility.Collapsed : Visibility.Visible;
        LoginText.Text = text;
        SetLoginAction(LoginPrimary, primary, primaryText);
        SetLoginAction(LoginSecondary, secondary, secondaryText);
        LoginButton.ToolTip = _login.Stage == LoginStage.None
            ? "Assistente de login: ajuda a entrar numa conta no perfil ativo."
            : "Cancelar o assistente de login";
        LoginButton.Foreground = _login.Stage == LoginStage.None
            ? (Brush)FindResource("TextBrush")
            : (Brush)FindResource("WarnBrush");
    }

    private static void SetLoginAction(System.Windows.Controls.Button button, LoginAction action, string text)
    {
        button.Tag = action;
        button.Content = text;
        button.Visibility = action == LoginAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool WatchClipboard => _login.WatchClipboard || _transfer.WatchClipboard;

    private void UpdateClipboardListener()
    {
        if (_hwnd == IntPtr.Zero || WatchClipboard == _clipboardListening)
        {
            return;
        }
        if (WatchClipboard)
        {
            _clipboardListening = NativeMethods.AddClipboardFormatListener(_hwnd);
            if (!_clipboardListening)
            {
                Log.Warn("Não foi possível observar a área de transferência; use o botão para voltar ao perfil de destino");
            }
        }
        else
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
            _clipboardListening = false;
        }
    }

    // ---------- Perfis ----------

    private async void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProfileButtonViewModel selected)
        {
            await SelectProfileAsync(selected, start: true, focus: true);
        }
    }

    /// <summary>Clique direito na aba → Renomear: troca só o nome exibido (a pasta e a conta não mudam).</summary>
    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileButtonViewModel vm)
        {
            return;
        }
        var others = _profiles.Where(p => !ReferenceEquals(p, vm)).Select(p => p.Name).ToList();
        var dialog = new RenameWindow(vm.Profile.Id, vm.Name, others) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewName == vm.Profile.Name)
        {
            return;
        }
        var previous = vm.Profile.Name;
        vm.Profile.Name = dialog.NewName;
        try
        {
            _state.ConfigService.Save(_state.Config);
        }
        catch (Exception ex)
        {
            vm.Profile.Name = previous;
            Log.Error("Falha ao salvar o novo nome do perfil", ex);
            MessageBox.Show(this, $"Não foi possível salvar o novo nome:\n\n{ex.Message}", "Claudin",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Log.Info($"Perfil '{vm.Profile.Id}' renomeado");
        vm.Refresh();
        Render();
        RenderLoginStrip();
    }

    /// <summary>Torna o perfil ativo, mostra a janela dele na área host e, se pedido, abre a instância.</summary>
    private async Task SelectProfileAsync(ProfileButtonViewModel selected, bool start, bool focus)
    {
        if (!selected.IsActive)
        {
            foreach (var vm in _profiles)
            {
                vm.IsActive = ReferenceEquals(vm, selected);
            }
            _state.Config.ActiveProfileId = selected.Profile.Id;
            try
            {
                _state.ConfigService.Save(_state.Config);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao salvar o perfil ativo", ex);
            }
        }
        _lastSwitch = DateTime.UtcNow;
        _host?.Activate(selected.Instance, focus);
        Render();
        if (start)
        {
            await OpenAsync(selected.Instance);
        }
    }

    /// <summary>
    /// Uma janela de perfil apareceu sozinha (link, notificação, bandeja) ou uma instância foi aberta por fora.
    /// Durante um login pendente, o assistente decide o que mostrar (o perfil com o código).
    /// </summary>
    private void HandleAttention(ProfileInstance inst) => SelectInstance(_login.OnAttention(inst) ?? inst, focus: false);

    private void SelectInstance(ProfileInstance inst, bool focus)
    {
        if (_profiles.FirstOrDefault(p => ReferenceEquals(p.Instance, inst)) is not { } vm)
        {
            return;
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        _ = SelectProfileAsync(vm, start: false, focus: focus);
    }

    private async Task OpenAsync(ProfileInstance instance)
    {
        try
        {
            await _instances.StartAsync(instance.Profile.Id);
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao abrir o perfil '{instance.Profile.Id}'", ex);
        }
    }

    // ---------- Área host ----------

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Render()
    {
        if (ActiveProfile?.Instance is not { } inst)
        {
            return;
        }
        var name = inst.Profile.Name;
        var muted = (Brush)FindResource("MutedBrush");
        var ok = (Brush)FindResource("OkBrush");
        var warn = (Brush)FindResource("WarnBrush");
        var err = (Brush)FindResource("ErrorBrush");
        var hiddenByUser = inst.IsAlive && (_host?.IsHiddenByUser(inst) ?? false);

        // Com o Claude aberto, a área atrás dele fica lisa, na cor dele: se a janela demorar um instante para se
        // redesenhar numa troca, não aparece o cartão de status. Se a janela não aparecer em 2 s, o cartão volta.
        var claudeExpected = inst.State == InstanceState.Running && !hiddenByUser
            && ((_host?.IsShowing(inst) ?? false) || DateTime.UtcNow - _lastSwitch < TimeSpan.FromSeconds(2));
        StatusPanel.Visibility = claudeExpected ? Visibility.Hidden : Visibility.Visible;
        HostArea.Background = claudeExpected ? _claudeBackground : (Brush)FindResource("HostBrush");

        // Moldura na cor da conta ativa: mostra em qual conta você está antes de colar ou enviar algo.
        HostArea.BorderBrush = ActiveProfile.ColorBrush;
        var frame = new Thickness(_state.Config.ShowProfileFrame ? ProfileFrameThickness : 0);
        if (HostArea.BorderThickness != frame)
        {
            HostArea.BorderThickness = frame;
            _host?.RequestSync(); // o Claude ocupa só o miolo da moldura
        }

        var (icon, iconBrush, title, detail) = inst.State switch
        {
            InstanceState.NotRunning => ("", muted, $"{name} não está aberta",
                "Abra para iniciar o Claude com a pasta de dados deste perfil."),
            InstanceState.Starting => ("", muted, $"Abrindo {name}…",
                "Aguardando a janela do Claude (até 30 s)."),
            InstanceState.Running when hiddenByUser => ("", muted, $"{name} está na bandeja",
                "A janela foi fechada pelo X do Claude, mas ele continua aberto em segundo plano."),
            InstanceState.Running => ("", ok, $"{name} aberta", RunningDetail(inst)),
            InstanceState.NotResponding => ("", warn, $"{name} não está respondendo",
                "A janela do Claude parou de responder. Aguarde, ou force o fechamento e abra de novo."),
            InstanceState.Exited => ("", muted, $"{name} fechada",
                inst.Message ?? "A instância do Claude deste perfil foi encerrada."),
            _ => ("", err, $"Não foi possível abrir {name}", inst.Message ?? "Erro desconhecido."),
        };
        StatusIcon.Text = icon;
        StatusIcon.Foreground = iconBrush;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;

        var (primary, primaryText, secondary, secondaryText) = inst.State switch
        {
            InstanceState.NotRunning => (HostAction.Open, $"Abrir {name}", HostAction.None, ""),
            InstanceState.Running => (HostAction.ShowWindow, hiddenByUser ? "Mostrar de novo" : "Mostrar janela", HostAction.None, ""),
            InstanceState.NotResponding => (HostAction.ShowWindow, "Mostrar janela", HostAction.ForceRestart, "Forçar fechamento e reabrir"),
            InstanceState.Exited => (HostAction.Open, "Reabrir", HostAction.None, ""),
            InstanceState.Failed => (HostAction.Open, "Tentar de novo", HostAction.None, ""),
            _ => (HostAction.None, "", HostAction.None, ""),
        };
        SetAction(PrimaryAction, primary, primaryText);
        SetAction(SecondaryAction, secondary, secondaryText);

        RenderProfileRows();
    }

    private static string RunningDetail(ProfileInstance inst)
    {
        var parts = new List<string> { $"PID {inst.MainPid}" };
        parts.Add(inst.Window == IntPtr.Zero ? "janela ainda não encontrada" : $"janela 0x{inst.Window.ToInt64():X}");
        if (inst.Adopted)
        {
            parts.Add("já estava aberta, o Claudin passou a acompanhá-la");
        }
        var text = string.Join(" · ", parts) + ".";
        return inst.Message is null ? text : $"{text}\n{inst.Message}";
    }

    private static void SetAction(System.Windows.Controls.Button button, HostAction action, string text)
    {
        button.Tag = action;
        button.Content = text;
        button.Visibility = action == HostAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnPrimaryActionClick(object sender, RoutedEventArgs e) => await RunActionAsync((HostAction)PrimaryAction.Tag);

    private async void OnSecondaryActionClick(object sender, RoutedEventArgs e) => await RunActionAsync((HostAction)SecondaryAction.Tag);

    private async Task RunActionAsync(HostAction action)
    {
        if (ActiveProfile?.Instance is not { } inst)
        {
            return;
        }
        switch (action)
        {
            case HostAction.Open:
                await OpenAsync(inst);
                break;
            case HostAction.ShowWindow:
                if (_host is not null)
                {
                    _host.Activate(inst, focus: true);
                }
                else
                {
                    _instances.ShowWindow(inst.Profile.Id);
                }
                break;
            case HostAction.ForceRestart:
                var answer = MessageBox.Show(this,
                    $"Forçar o fechamento do Claude de \"{inst.Profile.Name}\"?\n\n" +
                    "O que não tiver sido salvo nessa instância pode ser perdido.",
                    "Claudin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
                if (await _instances.ForceCloseAsync(inst.Profile.Id))
                {
                    await OpenAsync(inst);
                }
                else
                {
                    MessageBox.Show(this, "O processo não encerrou. Tente pelo Gerenciador de Tarefas.", "Claudin",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                break;
        }
    }

    // ---------- Diagnóstico ----------

    private void RenderClaudeDiagnostics()
    {
        var claude = _state.Claude;
        if (claude.Install is { } install)
        {
            ClaudeIcon.Text = "";
            ClaudeIcon.Foreground = (Brush)FindResource("OkBrush");
            ClaudeTitle.Text = "Claude Desktop encontrado";
            ClaudePath.Text = install.ExecutablePath;
            var source = install.Source switch
            {
                ClaudeSource.Alias => $"alias de execução {ClaudeDefaults.AliasFileName}",
                ClaudeSource.Package => "pasta do pacote MSIX",
                _ => "caminho definido na configuração",
            };
            ClaudeDetail.Text = install.Version is null
                ? $"Origem: {source}."
                : $"Origem: {source}. Versão {install.Version}.";
        }
        else
        {
            ClaudeIcon.Text = "";
            ClaudeIcon.Foreground = (Brush)FindResource("ErrorBrush");
            ClaudeTitle.Text = "Claude Desktop não encontrado";
            ClaudePath.Visibility = Visibility.Collapsed;
            ClaudeDetail.Text =
                "Instale o Claude Desktop (Microsoft Store ou claude.ai/download) e abra o Claudin de novo. " +
                "Se ele estiver em outro lugar, informe o caminho em \"claudeExecutableOverride\" no config.json.";
        }
        if (claude.Notes.Count > 0)
        {
            NotesText.Text = string.Join("\n", claude.Notes.Select(n => "• " + n));
            NotesText.Visibility = Visibility.Visible;
        }
        ConfigPathText.Text = $"Configuração: {_state.ConfigService.ConfigPath}";
    }

    private void RenderProfileRows()
    {
        var muted = (Brush)FindResource("MutedBrush");
        var warn = (Brush)FindResource("WarnBrush");
        var err = (Brush)FindResource("ErrorBrush");
        FolderList.ItemsSource = _instances.Instances.Select(inst =>
        {
            var folder = _state.Folders.FirstOrDefault(f => ReferenceEquals(f.Profile, inst.Profile));
            var folderText = folder?.Status switch
            {
                FolderStatus.ClaudeDefault => "pasta padrão do Claude",
                FolderStatus.Failed => $"erro na pasta: {folder.Error}",
                _ => "pasta própria",
            };
            var stateText = inst.State switch
            {
                InstanceState.NotRunning => "não aberta",
                InstanceState.Starting => "abrindo…",
                InstanceState.Running => $"aberta (PID {inst.MainPid}{(inst.Adopted ? ", adotada" : "")})",
                InstanceState.NotResponding => $"sem resposta (PID {inst.MainPid})",
                InstanceState.Exited => "fechada",
                _ => "falhou",
            };
            var brush = inst.State switch
            {
                InstanceState.Failed => err,
                InstanceState.NotResponding => warn,
                _ => folder?.Status == FolderStatus.Failed ? err : muted,
            };
            return new FolderRowViewModel(inst.Profile.Name, ColorBrushes.FromHex(inst.Profile.Color), inst.DataDir,
                $"{folderText} · {stateText}", brush);
        }).ToList();
    }

    // ---------- Legenda ----------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var startup = new StartupRegistration(new RunKeyStore(), AppPaths.RegistryBackupFile);
        var dialog = new SettingsWindow(_state.Config, _state.Claude, startup) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            _state.ConfigService.Save(_state.Config);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar as configurações", ex);
            MessageBox.Show(this, $"Não foi possível salvar as configurações:\n\n{ex.Message}", "Claudin",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        foreach (var vm in _profiles)
        {
            vm.Refresh();
        }
        if (_host is not null)
        {
            _host.HideCaptionButtons = _state.Config.HideClaudeCaptionButtons;
            _host.HideTaskbarButtons = _state.Config.HideClaudeTaskbarButtons;
        }
        Render();
        RenderLoginStrip();
        if (dialog.RestartNeeded)
        {
            MessageBox.Show(this, "A raiz das pastas ou o caminho do Claude mudou. Feche e abra o Claudin para aplicar.",
                "Claudin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Com WindowChrome, a janela maximizada ultrapassa a tela pela espessura da borda de redimensionamento.
    /// Compensa com uma margem igual a essa espessura, no DPI do monitor atual.
    /// </summary>
    private void UpdateMaximizedLayout()
    {
        if (WindowState == WindowState.Maximized)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }
            var padded = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpi);
            var fx = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSIZEFRAME, dpi) + padded;
            var fy = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSIZEFRAME, dpi) + padded;
            var scale = dpi / 96.0;
            RootBorder.Margin = new Thickness(fx / scale, fy / scale, fx / scale, fy / scale);
            RootBorder.BorderThickness = new Thickness(0);
            MaxRestoreButton.Content = "";
            MaxRestoreButton.ToolTip = "Restaurar";
            System.Windows.Automation.AutomationProperties.SetName(MaxRestoreButton, "Restaurar");
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            MaxRestoreButton.Content = "";
            MaxRestoreButton.ToolTip = "Maximizar";
            System.Windows.Automation.AutomationProperties.SetName(MaxRestoreButton, "Maximizar");
        }
    }
}
