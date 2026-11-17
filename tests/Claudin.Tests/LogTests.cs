using Claudin.Core;

namespace Claudin.Tests;

public sealed class LogTests
{
    [Fact]
    public void Sanitize_ReplacesUserProfileCaseInsensitive()
    {
        var text = @"Config em C:\USERS\fulano\AppData\Local\Claudin\config.json";

        var sanitized = Log.Sanitize(text, @"C:\Users\fulano");

        Assert.Equal(@"Config em %USERPROFILE%\AppData\Local\Claudin\config.json", sanitized);
        Assert.DoesNotContain("fulano", sanitized);
    }

    [Fact]
    public void Sanitize_LeavesOtherPathsAlone()
    {
        Assert.Equal(@"C:\Claudin\perfis\y", Log.Sanitize(@"C:\Claudin\perfis\y", @"C:\Users\fulano"));
    }
}
