using Claudin.Core;

namespace Claudin.Tests;

public sealed class LoginAssistantTests
{
    private readonly FakeClock _clock = new();
    private readonly ProfileInstance _x = new(new Profile { Id = "x", Name = "Conta X" }, FakePaths.DefaultDataDir, true);
    private readonly ProfileInstance _y = new(new Profile { Id = "y", Name = "Conta Y" }, @"C:\Claudin\perfis\y", false);

    private LoginAssistant Create() => new(() => _clock.Now, TimeSpan.FromMinutes(15));

    [Fact]
    public void FullFlow_LinkArrivesAtReceiver_ThenCopy_ThenPaste()
    {
        var a = Create();
        a.Start(_y, _x);
        Assert.Equal(LoginStage.WaitingForLink, a.Stage);
        Assert.False(a.WatchClipboard);

        Assert.Same(_x, a.OnAttention(_x));        // o link caiu na Conta X, que mostra o código
        Assert.Equal(LoginStage.CodeShown, a.Stage);
        Assert.True(a.WatchClipboard);

        Assert.Same(_y, a.OnClipboardChanged());   // copiou: volta para a Conta Y
        Assert.Equal(LoginStage.PasteCode, a.Stage);
        Assert.False(a.WatchClipboard);

        a.Complete();
        Assert.Equal(LoginStage.None, a.Stage);
        Assert.Null(a.Target);
    }

    [Fact]
    public void ClipboardIgnored_UntilCodeIsShown()
    {
        var a = Create();
        a.Start(_y, _x);

        Assert.Null(a.OnClipboardChanged()); // copiar o próprio e-mail, por exemplo
        Assert.Equal(LoginStage.WaitingForLink, a.Stage);
    }

    [Fact]
    public void AttentionFromOtherProfiles_UsesNormalBehavior()
    {
        var a = Create();
        a.Start(_y, _x);

        Assert.Null(a.OnAttention(_y));
        Assert.Equal(LoginStage.WaitingForLink, a.Stage);
    }

    [Fact]
    public void WithoutLogin_AttentionIsNotIntercepted()
    {
        var a = Create();

        Assert.Null(a.OnAttention(_x));
        Assert.Null(a.OnClipboardChanged());
        Assert.Equal(LoginStage.None, a.Stage);
    }

    [Fact]
    public void TargetIsReceiver_NoCodeNeeded()
    {
        var a = Create();
        a.Start(_x, _x);

        Assert.True(a.TargetReceivesLinks);
        Assert.Null(a.OnAttention(_x));
        Assert.Null(a.MarkCodeCopied());
        Assert.Equal(LoginStage.WaitingForLink, a.Stage);
    }

    [Fact]
    public void NoReceiverInClaudin_UserMarksCodeCopiedManually()
    {
        var a = Create();
        a.Start(_y, null);

        Assert.Null(a.OnAttention(_x));
        Assert.Same(_y, a.MarkCodeCopied());
        Assert.Equal(LoginStage.PasteCode, a.Stage);
    }

    [Fact]
    public void ExpiresAfterTimeout()
    {
        var a = Create();
        a.Start(_y, _x);

        _clock.Now += TimeSpan.FromMinutes(14);
        Assert.False(a.CheckTimeout());
        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(a.CheckTimeout());
        Assert.Equal(LoginStage.None, a.Stage);
        Assert.False(a.CheckTimeout());
    }

    [Fact]
    public void Cancel_ResetsAndRaisesChange()
    {
        var a = Create();
        var changes = new List<string?>();
        a.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        a.Start(_y, _x);

        a.Cancel();

        Assert.Equal(LoginStage.None, a.Stage);
        Assert.Contains(nameof(LoginAssistant.Stage), changes);
    }
}

public sealed class SingleInstanceTests
{
    private static string UniqueName() => "ClaudinTest-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public void SecondAcquire_FailsWhileFirstIsAlive_ThenSucceedsAfterDispose()
    {
        var name = UniqueName();
        using (var first = SingleInstance.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(name));
        }
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Message_ReachesTheFirstInstance()
    {
        var name = UniqueName();
        using var first = SingleInstance.TryAcquire(name)!;
        var received = new TaskCompletionSource<string>();
        first.StartServer(m => received.TrySetResult(m));

        Assert.True(SingleInstance.SendToExisting(name, SingleInstance.ActivateMessage, TimeSpan.FromSeconds(5)));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SingleInstance.ActivateMessage, message);
    }

    [Fact]
    public void Send_WithoutServer_ReturnsFalse()
    {
        Assert.False(SingleInstance.SendToExisting(UniqueName(), "x", TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void NameFor_IsStableAndCaseInsensitive()
    {
        Assert.Equal(SingleInstance.NameFor(@"C:\Users\a\AppData\Local\Claudin"), SingleInstance.NameFor(@"c:\users\A\AppData\Local\Claudin\"));
        Assert.NotEqual(SingleInstance.NameFor(@"C:\a"), SingleInstance.NameFor(@"C:\b"));
    }
}
