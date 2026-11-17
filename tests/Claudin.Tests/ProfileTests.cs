using Claudin.Core;

namespace Claudin.Tests;

public sealed class ProfileTests
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    [Fact]
    public void ResolveDataDir_WithoutDataDir_UsesRootAndId()
    {
        var profile = new Profile { Id = "y" };

        Assert.Equal(@"C:\Claudin\perfis\y", profile.ResolveDataDir(@"C:\Claudin\perfis"));
        Assert.Equal(@"D:\Outra\y", profile.ResolveDataDir(@"D:\Outra\"));
    }

    [Fact]
    public void ResolveDataDir_ExpandsEnvironmentVariables()
    {
        var profile = new Profile { Id = "x", DataDir = @"%APPDATA%\Claude" };

        Assert.Equal(Path.Combine(AppData, "Claude"), profile.ResolveDataDir(@"C:\qualquer"));
    }

    [Fact]
    public void ResolveDataDir_RootWithEnvironmentVariable()
    {
        var profile = new Profile { Id = "y" };

        Assert.Equal(Path.Combine(AppData, "Claudin", "y"), profile.ResolveDataDir(@"%APPDATA%\Claudin"));
    }

    [Theory]
    [InlineData(@"%APPDATA%\Claude")]
    [InlineData(@"%APPDATA%\CLAUDE\")]
    [InlineData(@"%APPDATA%\Outra\..\Claude")]
    public void UsesClaudeDefaultDataDir_TrueForDefaultFolderInAnySpelling(string dataDir)
    {
        Assert.True(new Profile { Id = "x", DataDir = dataDir }.UsesClaudeDefaultDataDir(@"C:\Claudin\perfis"));
    }

    [Fact]
    public void UsesClaudeDefaultDataDir_FalseForOtherFolders()
    {
        Assert.False(new Profile { Id = "y" }.UsesClaudeDefaultDataDir(@"C:\Claudin\perfis"));
        Assert.False(new Profile { Id = "x", DataDir = @"%APPDATA%\Claude-Data" }.UsesClaudeDefaultDataDir(@"C:\Claudin\perfis"));
    }

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a\b")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a\.\b\..\c", @"C:\a\c")]
    public void PathUtil_Normalize(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.Normalize(input));
    }
}
