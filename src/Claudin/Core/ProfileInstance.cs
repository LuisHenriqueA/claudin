namespace Claudin.Core;

public enum InstanceState
{
    /// <summary>Não aberta nesta sessão do Claudin.</summary>
    NotRunning,

    /// <summary>Iniciada; aguardando a janela.</summary>
    Starting,

    Running,

    /// <summary>A janela não responde (IsHungAppWindow).</summary>
    NotResponding,

    /// <summary>Estava aberta e o processo terminou.</summary>
    Exited,

    /// <summary>Falhou ao abrir.</summary>
    Failed,
}

/// <summary>Estado da instância do Claude de um perfil.</summary>
public sealed class ProfileInstance(Profile profile, string dataDir, bool usesClaudeDefaultDataDir) : ObservableObject
{
    private InstanceState _state = InstanceState.NotRunning;
    private int? _mainPid;
    private IntPtr _window;
    private bool _adopted;
    private string? _message;

    public Profile Profile { get; } = profile;

    public string DataDir { get; } = dataDir;

    public bool UsesClaudeDefaultDataDir { get; } = usesClaudeDefaultDataDir;

    public InstanceState State
    {
        get => _state;
        internal set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsAlive));
            }
        }
    }

    /// <summary>Iniciando, aberta ou sem responder: existe um processo do Claude para este perfil.</summary>
    public bool IsAlive => State is InstanceState.Starting or InstanceState.Running or InstanceState.NotResponding;

    public int? MainPid
    {
        get => _mainPid;
        internal set => Set(ref _mainPid, value);
    }

    /// <summary>Janela principal do Claude (IntPtr.Zero se ainda não encontrada).</summary>
    public IntPtr Window
    {
        get => _window;
        internal set => Set(ref _window, value);
    }

    /// <summary>True quando a instância já estava aberta e o Claudin só passou a acompanhá-la.</summary>
    public bool Adopted
    {
        get => _adopted;
        internal set => Set(ref _adopted, value);
    }

    /// <summary>Detalhe para o usuário (erro, aviso).</summary>
    public string? Message
    {
        get => _message;
        internal set => Set(ref _message, value);
    }

    internal IProcessHandle? Handle { get; set; }
}
