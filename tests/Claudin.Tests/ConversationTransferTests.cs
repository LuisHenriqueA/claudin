using Claudin.Core;

namespace Claudin.Tests;

public sealed class ConversationTransferTests
{
    private const int BaseCommandLine = 120;

    private readonly FakeClock _clock = new();
    private readonly ProfileInstance _x = new(new Profile { Id = "x", Name = "Conta X" }, FakePaths.DefaultDataDir, true);
    private readonly ProfileInstance _y = new(new Profile { Id = "y", Name = "Conta Y" }, @"C:\Claudin\perfis\y", false);

    private ConversationTransfer Started()
    {
        var t = new ConversationTransfer(() => _clock.Now, TimeSpan.FromMinutes(30));
        t.Start(_x, _y);
        return t;
    }

    private static string Document(int bodyLength = 200) =>
        ConversationTransfer.Marker + "\n1. Objetivo: migrar o relatório.\n" + new string('a', bodyLength);

    [Fact]
    public void RequestPrompt_AsksForTheMarker()
    {
        Assert.Contains(ConversationTransfer.Marker, ConversationTransfer.RequestPrompt);
        Assert.False(ConversationTransfer.RequestPrompt.StartsWith('/'));
    }

    [Fact]
    public void ValidDocument_IsDeliveredByLink_WithIntro()
    {
        var t = Started();
        Assert.True(t.WatchClipboard);

        var delivery = t.OnClipboardText("Claro! Aqui está:\n\n" + Document(), BaseCommandLine);

        Assert.NotNull(delivery);
        Assert.False(delivery.UsedFallback);
        Assert.Null(delivery.ClipboardText);
        Assert.StartsWith("claude://claude.ai/new?q=", delivery.Url);
        var q = Uri.UnescapeDataString(delivery.Url["claude://claude.ai/new?q=".Length..]);
        Assert.StartsWith(ConversationTransfer.TargetIntro, q);
        Assert.Contains(ConversationTransfer.Marker, q);
        Assert.DoesNotContain("Claro! Aqui está", q); // o que vem antes do marcador fica de fora
        Assert.Equal(TransferStage.Delivered, t.Stage);
        Assert.False(t.WatchClipboard);
    }

    [Fact]
    public void UnrelatedCopy_IsIgnored_WithHint()
    {
        var t = Started();

        Assert.Null(t.OnClipboardText("um trecho qualquer que o usuário copiou", BaseCommandLine));

        Assert.Equal(TransferStage.WaitingForDocument, t.Stage);
        Assert.Contains("não parece", t.Hint);
    }

    [Fact]
    public void ThePromptItself_IsIgnoredSilently()
    {
        var t = Started();

        Assert.Null(t.OnClipboardText(ConversationTransfer.RequestPrompt, BaseCommandLine));

        Assert.Null(t.Hint);
        Assert.Equal(TransferStage.WaitingForDocument, t.Stage);
    }

    [Fact]
    public void Marker_IsFoundWithoutHashesOrInOtherCase()
    {
        Assert.NotNull(ConversationTransfer.ExtractDocument("Continuação de conversa\nconteúdo"));
        Assert.Equal("**CONTINUAÇÃO DE CONVERSA**\nx", ConversationTransfer.ExtractDocument("antes\n**CONTINUAÇÃO DE CONVERSA**\nx"));
        Assert.Null(ConversationTransfer.ExtractDocument("sem marcador"));
    }

    [Fact]
    public void LongDocument_UsesClipboardFallback()
    {
        var t = Started();

        var delivery = t.OnClipboardText(Document(ConversationTransfer.MaxLinkTextLength), BaseCommandLine)!;

        Assert.True(delivery.UsedFallback);
        Assert.True(t.UsedFallback);
        Assert.StartsWith(ConversationTransfer.Marker, delivery.ClipboardText);
        Assert.Equal(ConversationTransfer.BuildNewChatUrl(ConversationTransfer.FallbackIntro), delivery.Url);
    }

    [Fact]
    public void AccentHeavyDocument_ThatWouldOverflowCommandLine_UsesFallback()
    {
        // 9.000 caracteres cabem no limite do Claude, mas "ção" vira %C3%A7%C3%A3o: a linha de comando estoura.
        var doc = ConversationTransfer.Marker + "\n" + string.Concat(Enumerable.Repeat("ção ", 2250));

        var delivery = ConversationTransfer.BuildDelivery(doc, BaseCommandLine);

        Assert.True(delivery.UsedFallback);
    }

    [Fact]
    public void Url_RoundTripsAccentsAndNewLines()
    {
        var text = "Linha 1: ação, é, ü\nLinha 2 & 3 = 100% #ok?";

        var url = ConversationTransfer.BuildNewChatUrl(text);

        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("\n", url);
        Assert.Equal(text, Uri.UnescapeDataString(url["claude://claude.ai/new?q=".Length..]));
    }

    [Fact]
    public void Url_FitsInCommandLine_ThroughWindowsParser()
    {
        var url = ConversationTransfer.BuildNewChatUrl("Teste com \"aspas\" e barra \\ final\\");
        var args = CommandLine.Split("claude.exe " + CommandLine.BuildStartArguments(@"C:\Claudin\perfis\y") + " " + CommandLine.Quote(url));

        Assert.Equal(3, args.Count);
        Assert.Equal(url, args[2]);
    }

    // ---------- Sessão do Code ----------

    private static string CodeDocument(string folderLine = @"Pasta do projeto: C:\Repo\Claudin") =>
        ConversationTransfer.CodeMarker + "\n" + folderLine + "\n1. Objetivo: terminar a Fase 8.\n" + new string('b', 200);

    [Fact]
    public void Code_RequestPrompt_AsksForMarkerAndFolder()
    {
        Assert.Contains(ConversationTransfer.CodeMarker, ConversationTransfer.CodeRequestPrompt);
        Assert.Contains(ConversationTransfer.FolderLabel, ConversationTransfer.CodeRequestPrompt);
        Assert.False(ConversationTransfer.CodeRequestPrompt.StartsWith('/'));
    }

    [Fact]
    public void Code_ValidDocument_OpensCodeSessionInTheFolder()
    {
        var t = new ConversationTransfer(() => _clock.Now);
        t.Start(_x, _y, TransferKind.Code);

        var delivery = t.OnClipboardText("Pronto:\n\n" + CodeDocument(), BaseCommandLine, _ => true)!;

        Assert.StartsWith("claude://code/new?q=", delivery.Url);
        Assert.EndsWith("&folder=" + Uri.EscapeDataString(@"C:\Repo\Claudin"), delivery.Url);
        Assert.Equal(@"C:\Repo\Claudin", delivery.Folder);
        Assert.False(delivery.FolderMissing);
        var q = Uri.UnescapeDataString(delivery.Url["claude://code/new?q=".Length..delivery.Url.IndexOf("&folder=", StringComparison.Ordinal)]);
        Assert.StartsWith(ConversationTransfer.CodeTargetIntro, q);
        Assert.DoesNotContain("Pronto:", q);
    }

    [Fact]
    public void Code_ChatDocument_IsNotAccepted()
    {
        var t = new ConversationTransfer(() => _clock.Now);
        t.Start(_x, _y, TransferKind.Code);

        Assert.Null(t.OnClipboardText(Document(), BaseCommandLine, _ => true));
        Assert.Contains(ConversationTransfer.CodeMarker, t.Hint);
    }

    [Fact]
    public void Code_MissingFolder_OpensWithoutFolder_AndFlagsIt()
    {
        var delivery = ConversationTransfer.BuildDelivery(CodeDocument(), BaseCommandLine, TransferKind.Code, _ => false);

        Assert.DoesNotContain("&folder=", delivery.Url);
        Assert.Null(delivery.Folder);
        Assert.True(delivery.FolderMissing);
    }

    [Theory]
    [InlineData(@"Pasta do projeto: `C:\Repo\Claudin`", @"C:\Repo\Claudin")]
    [InlineData(@"**Pasta do projeto:** C:\Repo\Claudin.", @"C:\Repo\Claudin")]
    [InlineData(@"- Pasta do projeto: ""C:\Meus Projetos\app""", @"C:\Meus Projetos\app")]
    [InlineData(@"Pasta do projeto: pasta\relativa", null)]
    [InlineData("Sem a linha", null)]
    public void Code_ExtractFolder_ToleratesMarkdown(string line, string? expected)
    {
        Assert.Equal(expected, ConversationTransfer.ExtractFolder(ConversationTransfer.CodeMarker + "\n" + line + "\nresto"));
    }

    [Fact]
    public void Code_LongDocument_UsesFallback_KeepingTheFolder()
    {
        var doc = CodeDocument() + new string('c', ConversationTransfer.MaxLinkTextLength);

        var delivery = ConversationTransfer.BuildDelivery(doc, BaseCommandLine, TransferKind.Code, _ => true);

        Assert.True(delivery.UsedFallback);
        Assert.Equal(ConversationTransfer.BuildNewCodeUrl(ConversationTransfer.CodeFallbackIntro, @"C:\Repo\Claudin"), delivery.Url);
        Assert.StartsWith(ConversationTransfer.CodeMarker, delivery.ClipboardText);
    }

    [Fact]
    public void NotStarted_IgnoresClipboard()
    {
        var t = new ConversationTransfer(() => _clock.Now);

        Assert.Null(t.OnClipboardText(Document(), BaseCommandLine));
        Assert.Equal(TransferStage.None, t.Stage);
    }

    [Fact]
    public void Timeout_And_Cancel_Reset()
    {
        var t = Started();
        _clock.Now += TimeSpan.FromMinutes(31);
        Assert.True(t.CheckTimeout());
        Assert.Equal(TransferStage.None, t.Stage);

        t.Start(_y, _x);
        t.Cancel();
        Assert.Equal(TransferStage.None, t.Stage);
        Assert.Null(t.Source);
    }
}
