namespace Claudin.Core;

public enum TransferStage
{
    None,

    /// <summary>O pedido de resumo está na área de transferência; aguardando o usuário copiar a resposta do Claude.</summary>
    WaitingForDocument,

    /// <summary>A conversa nova foi aberta na conta de destino.</summary>
    Delivered,
}

/// <summary>O que está sendo levado para a outra conta.</summary>
public enum TransferKind
{
    /// <summary>Uma conversa do chat (claude://claude.ai/new).</summary>
    Chat,

    /// <summary>Uma sessão da aba Code (claude://code/new, na mesma pasta do projeto).</summary>
    Code,
}

/// <summary>Como entregar o documento na conta de destino.</summary>
/// <param name="Url">Link claude://... a abrir na instância de destino.</param>
/// <param name="UsedFallback">O documento não coube no link: ele vai pela área de transferência.</param>
/// <param name="ClipboardText">Texto a deixar na área de transferência (só no modo alternativo).</param>
/// <param name="Folder">Sessão do Code: a pasta do projeto usada no link (nula se não veio ou não existe).</param>
/// <param name="FolderMissing">Sessão do Code: o documento citou uma pasta que não existe neste computador.</param>
public sealed record TransferDelivery(string Url, bool UsedFallback, string? ClipboardText, string? Folder = null,
    bool FolderMissing = false);

/// <summary>
/// "Levar conversa para outra conta" (Fase 7), sem RPA e sem cookies:
/// 1. o Claudin copia um pedido de resumo; o usuário cola e envia na conversa de origem;
/// 2. o Claude responde com um documento que começa com <see cref="Marker"/>; o usuário clica em "Copiar";
/// 3. o Claudin lê essa cópia (só se tiver o marcador) e abre, na conta de destino, um chat novo já preenchido pelo
///    link oficial claude://claude.ai/new?q=... (Fase 0, seção 5d). Nada é enviado sozinho.
/// O conteúdo nunca é gravado em log.
/// </summary>
public sealed class ConversationTransfer(Func<DateTime> now, TimeSpan? timeout = null) : ObservableObject
{
    public const string Marker = "### CONTINUAÇÃO DE CONVERSA";

    /// <summary>Limite do texto no link (o Claude corta em 14336; margem de segurança).</summary>
    public const int MaxLinkTextLength = 14000;

    /// <summary>Limite da linha de comando do Windows é 32767; margem de segurança.</summary>
    public const int MaxCommandLineLength = 30000;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    public const string RequestPrompt =
        "Preciso continuar esta conversa em outra conta do Claude, que não tem acesso a este histórico. " +
        "Escreva um documento de transferência completo para que outro assistente continue exatamente de onde paramos.\n\n" +
        "Comece a resposta com a linha \"" + Marker + "\" e inclua, nesta ordem:\n" +
        "1. Objetivo: o que estou tentando fazer.\n" +
        "2. Contexto: informações, restrições e preferências que eu dei.\n" +
        "3. Decisões já tomadas e por quê.\n" +
        "4. Estado atual: o que já foi feito e os resultados.\n" +
        "5. Pendências e próximos passos.\n" +
        "6. Trechos essenciais: textos, códigos ou dados que precisam ser preservados literalmente.\n" +
        "7. A última pergunta ou pedido que ficou em aberto.\n\n" +
        "Seja específico e não omita detalhes de que o outro assistente precisaria. " +
        "Não inclua senhas, tokens nem outras credenciais.";

    public const string TargetIntro =
        "Continue a partir daqui. Esta é a transferência de uma conversa que comecei em outra conta do Claude. " +
        "Use o documento abaixo como contexto e confirme em poucas linhas que entendeu antes de seguirmos.";

    public const string FallbackIntro =
        "Vou colar abaixo a transferência de uma conversa que comecei em outra conta do Claude. " +
        "Use-a como contexto e confirme em poucas linhas que entendeu antes de seguirmos.";

    // ---------- Sessão do Code ----------

    public const string CodeMarker = "### CONTINUAÇÃO DE SESSÃO DO CODE";

    public const string FolderLabel = "Pasta do projeto:";

    public const string CodeRequestPrompt =
        "Preciso continuar esta sessão do Claude Code em outra conta do Claude, na mesma pasta deste computador, " +
        "mas sem acesso a este histórico. Escreva um documento de transferência completo para que outra sessão " +
        "continue exatamente de onde paramos.\n\n" +
        "Comece a resposta com a linha \"" + CodeMarker + "\" e, logo abaixo, uma linha \"" + FolderLabel +
        " \" seguida do caminho absoluto da pasta de trabalho desta sessão. Depois inclua, nesta ordem:\n" +
        "1. Objetivo: o que estou tentando fazer.\n" +
        "2. Contexto: informações, restrições e preferências que eu dei.\n" +
        "3. Decisões já tomadas e por quê.\n" +
        "4. Estado atual: o que já foi feito, arquivos criados ou alterados, branch e estado do git, testes e resultados.\n" +
        "5. Pendências e próximos passos.\n" +
        "6. Trechos essenciais: comandos, códigos ou dados que precisam ser preservados literalmente.\n" +
        "7. A última pergunta ou pedido que ficou em aberto.\n\n" +
        "Seja específico e não omita detalhes de que a outra sessão precisaria. " +
        "Não inclua senhas, tokens nem outras credenciais.";

    public const string CodeTargetIntro =
        "Continue a partir daqui. Esta é a transferência de uma sessão do Claude Code que comecei em outra conta do " +
        "Claude, nesta mesma pasta. Use o documento abaixo como contexto, confira o estado atual dos arquivos e do git " +
        "antes de mudar qualquer coisa, e confirme em poucas linhas que entendeu antes de seguirmos.";

    public const string CodeFallbackIntro =
        "Vou colar abaixo a transferência de uma sessão do Claude Code que comecei em outra conta do Claude, nesta " +
        "mesma pasta. Use-a como contexto e confirme em poucas linhas que entendeu antes de seguirmos.";

    public static string MarkerFor(TransferKind kind) => kind == TransferKind.Code ? CodeMarker : Marker;

    public static string RequestPromptFor(TransferKind kind) => kind == TransferKind.Code ? CodeRequestPrompt : RequestPrompt;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private TransferKind _kind;
    private TransferStage _stage;
    private ProfileInstance? _source;
    private ProfileInstance? _target;
    private string? _hint;
    private bool _usedFallback;
    private DateTime _startedAt;

    public TransferStage Stage
    {
        get => _stage;
        private set => Set(ref _stage, value);
    }

    public ProfileInstance? Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    public ProfileInstance? Target
    {
        get => _target;
        private set => Set(ref _target, value);
    }

    /// <summary>Aviso para o usuário (por exemplo, "o texto copiado não é o documento").</summary>
    public string? Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public bool UsedFallback
    {
        get => _usedFallback;
        private set => Set(ref _usedFallback, value);
    }

    public bool WatchClipboard => Stage == TransferStage.WaitingForDocument;

    public TransferKind Kind
    {
        get => _kind;
        private set => Set(ref _kind, value);
    }

    public void Start(ProfileInstance source, ProfileInstance target, TransferKind kind = TransferKind.Chat)
    {
        Source = source;
        Target = target;
        Kind = kind;
        Hint = null;
        UsedFallback = false;
        _startedAt = now();
        Stage = TransferStage.WaitingForDocument;
        Log.Info($"Levar {Describe(kind)}: de '{source.Profile.Id}' para '{target.Profile.Id}' iniciado");
    }

    /// <summary>
    /// Algo novo foi copiado. Se for o documento (tem o marcador), devolve como entregá-lo; senão, só atualiza o aviso.
    /// </summary>
    /// <param name="baseCommandLineLength">Tamanho da linha de comando sem o link (executável + --user-data-dir).</param>
    /// <param name="folderExists">Sessão do Code: confere se a pasta citada existe (Directory.Exists).</param>
    public TransferDelivery? OnClipboardText(string? text, int baseCommandLineLength, Func<string, bool>? folderExists = null)
    {
        if (Stage != TransferStage.WaitingForDocument || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (string.Equals(text.Trim(), RequestPromptFor(Kind).Trim(), StringComparison.Ordinal))
        {
            return null; // o próprio pedido copiado de novo
        }
        var document = ExtractDocument(text, Kind);
        if (document is null)
        {
            Hint = "O texto copiado não parece ser o documento de transferência (falta o título \"" + MarkerFor(Kind) +
                   "\"). Copie a resposta inteira pelo botão Copiar do Claude.";
            return null;
        }
        var delivery = BuildDelivery(document, baseCommandLineLength, Kind, folderExists);
        UsedFallback = delivery.UsedFallback;
        Hint = null;
        Stage = TransferStage.Delivered;
        Log.Info($"Levar {Describe(Kind)}: documento recebido ({document.Length} caracteres; " +
                 $"{(delivery.UsedFallback ? "pela área de transferência" : "pelo link")}" +
                 $"{(Kind == TransferKind.Code ? delivery.Folder is null ? "; sem pasta" : "; com pasta" : "")})");
        return delivery;
    }

    private static string Describe(TransferKind kind) => kind == TransferKind.Code ? "sessão do Code" : "conversa";

    /// <summary>O documento a partir da linha do marcador; nulo se o marcador não aparece.</summary>
    public static string? ExtractDocument(string text, TransferKind kind = TransferKind.Chat)
    {
        var key = MarkerFor(kind).TrimStart('#', ' ');
        var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }
        var lineStart = text.LastIndexOf('\n', index) + 1;
        return text[lineStart..].Trim();
    }

    /// <summary>Caminho da linha "Pasta do projeto:" do documento (sem crases, aspas ou negrito); nulo se não houver.</summary>
    public static string? ExtractFolder(string document)
    {
        foreach (var raw in document.Split('\n'))
        {
            var line = raw.Trim().Trim('*', '-', ' ', '\t');
            var index = line.IndexOf(FolderLabel, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }
            var value = line[(index + FolderLabel.Length)..].Trim().Trim('*', '`', '"', '\'', ' ', '\t').TrimEnd('.');
            if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(value))
            {
                return null;
            }
            return value;
        }
        return null;
    }

    public static string BuildNewChatUrl(string text) => "claude://claude.ai/new?q=" + Uri.EscapeDataString(text);

    public static string BuildNewCodeUrl(string text, string? folder) =>
        "claude://code/new?q=" + Uri.EscapeDataString(text) + (folder is null ? "" : "&folder=" + Uri.EscapeDataString(folder));

    /// <summary>Usa o link quando o texto cabe; senão, link com uma instrução curta e o texto na área de transferência.</summary>
    public static TransferDelivery BuildDelivery(string document, int baseCommandLineLength,
        TransferKind kind = TransferKind.Chat, Func<string, bool>? folderExists = null)
    {
        string? folder = null;
        var folderMissing = false;
        if (kind == TransferKind.Code && ExtractFolder(document) is { } cited)
        {
            if (folderExists?.Invoke(cited) ?? true)
            {
                folder = cited;
            }
            else
            {
                folderMissing = true;
            }
        }
        string Url(string text) => kind == TransferKind.Code ? BuildNewCodeUrl(text, folder) : BuildNewChatUrl(text);

        var full = (kind == TransferKind.Code ? CodeTargetIntro : TargetIntro) + "\n\n" + document;
        var url = Url(full);
        var commandLine = baseCommandLineLength + url.Length + 3;
        if (full.Length <= MaxLinkTextLength && commandLine <= MaxCommandLineLength)
        {
            return new TransferDelivery(url, UsedFallback: false, ClipboardText: null, folder, folderMissing);
        }
        return new TransferDelivery(Url(kind == TransferKind.Code ? CodeFallbackIntro : FallbackIntro), UsedFallback: true,
            ClipboardText: document, folder, folderMissing);
    }

    public void Finish() => Reset("concluído");

    public void Cancel() => Reset("cancelado");

    public bool CheckTimeout()
    {
        if (Stage == TransferStage.None || now() - _startedAt < _timeout)
        {
            return false;
        }
        Reset("expirado");
        return true;
    }

    private void Reset(string reason)
    {
        if (Stage == TransferStage.None)
        {
            return;
        }
        Log.Info($"Levar {Describe(Kind)} {reason}");
        Stage = TransferStage.None;
        Source = null;
        Target = null;
        Hint = null;
        UsedFallback = false;
    }
}
