namespace Claudin.Core;

public enum LoginStage
{
    None,

    /// <summary>O usuário está entrando no perfil de destino (e-mail, link).</summary>
    WaitingForLink,

    /// <summary>O link caiu no perfil que recebe os claude:// e ele mostra o código.</summary>
    CodeShown,

    /// <summary>Código copiado: falta colar no perfil de destino.</summary>
    PasteCode,
}

/// <summary>
/// Assistente de login (Fase 4, revisada pela Fase 0).
///
/// No Claude da Store, o Windows entrega todo link claude:// à instância padrão (%APPDATA%\Claude), e não à
/// instância que pediu o login. A instância padrão mostra então um código, que o usuário cola na instância de destino.
/// O assistente só guia esse fluxo: nada é interceptado, lido ou alterado no registro. O código nunca passa pelo Claudin.
/// </summary>
public sealed class LoginAssistant(Func<DateTime> now, TimeSpan? timeout = null) : ObservableObject
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private LoginStage _stage;
    private ProfileInstance? _target;
    private ProfileInstance? _linkReceiver;
    private DateTime _startedAt;

    public LoginStage Stage
    {
        get => _stage;
        private set => Set(ref _stage, value);
    }

    /// <summary>Perfil em que o usuário quer fazer login.</summary>
    public ProfileInstance? Target
    {
        get => _target;
        private set => Set(ref _target, value);
    }

    /// <summary>Perfil que recebe os links claude:// (o que usa a pasta padrão do Claude). Nulo se nenhum usa.</summary>
    public ProfileInstance? LinkReceiver
    {
        get => _linkReceiver;
        private set => Set(ref _linkReceiver, value);
    }

    /// <summary>O próprio destino recebe os links: o login conclui direto, sem código.</summary>
    public bool TargetReceivesLinks => Target is not null && ReferenceEquals(Target, LinkReceiver);

    /// <summary>Só nesta etapa o Claudin presta atenção a "algo foi copiado" (sem ler o conteúdo).</summary>
    public bool WatchClipboard => Stage == LoginStage.CodeShown;

    public void Start(ProfileInstance target, ProfileInstance? linkReceiver)
    {
        Target = target;
        LinkReceiver = linkReceiver;
        _startedAt = now();
        Stage = LoginStage.WaitingForLink;
        Log.Info($"Assistente de login iniciado para o perfil '{target.Profile.Id}'");
    }

    public void Cancel() => Reset("cancelado");

    public void Complete() => Reset("concluído");

    /// <summary>
    /// Uma janela de perfil apareceu sozinha. Se for o receptor de links durante um login pendente, o link chegou:
    /// devolve o perfil a mostrar (o receptor, com o código). Nulo = comportamento normal.
    /// </summary>
    public ProfileInstance? OnAttention(ProfileInstance instance)
    {
        if (Stage is LoginStage.WaitingForLink or LoginStage.CodeShown && !TargetReceivesLinks
            && LinkReceiver is not null && ReferenceEquals(instance, LinkReceiver))
        {
            Stage = LoginStage.CodeShown;
            Log.Info("Assistente de login: o link chegou ao perfil receptor; mostrando o código");
            return instance;
        }
        return null;
    }

    /// <summary>Algo foi copiado enquanto o código estava na tela: devolve o destino, para o usuário colar.</summary>
    public ProfileInstance? OnClipboardChanged()
    {
        if (Stage != LoginStage.CodeShown)
        {
            return null;
        }
        Stage = LoginStage.PasteCode;
        return Target;
    }

    /// <summary>O usuário avisa que copiou o código (útil quando o receptor está fora do Claudin).</summary>
    public ProfileInstance? MarkCodeCopied()
    {
        if (Stage is not (LoginStage.WaitingForLink or LoginStage.CodeShown) || TargetReceivesLinks)
        {
            return null;
        }
        Stage = LoginStage.PasteCode;
        return Target;
    }

    /// <summary>Encerra sozinho depois do prazo. Retorna true se encerrou.</summary>
    public bool CheckTimeout()
    {
        if (Stage == LoginStage.None || now() - _startedAt < _timeout)
        {
            return false;
        }
        Reset("expirado");
        return true;
    }

    private void Reset(string reason)
    {
        if (Stage == LoginStage.None)
        {
            return;
        }
        Log.Info($"Assistente de login {reason}");
        Stage = LoginStage.None;
        Target = null;
        LinkReceiver = null;
    }
}
