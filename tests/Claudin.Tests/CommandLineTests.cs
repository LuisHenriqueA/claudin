using Claudin.Core;

namespace Claudin.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData(@"C:\Claudin\perfis\y")]
    [InlineData(@"C:\Pasta com espaço\perfil")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\termina\com\barra\")]
    [InlineData(@"C:\tem ""aspas""\aqui")]
    [InlineData(@"\\servidor\share\perfil")]
    public void BuildStartArguments_RoundTripsThroughWindowsParser(string dataDir)
    {
        var args = CommandLine.Split("claude.exe " + CommandLine.BuildStartArguments(dataDir));

        Assert.Equal(2, args.Count);
        Assert.Equal(dataDir, CommandLine.GetSwitchValue(args, CommandLine.UserDataDirSwitch));
    }

    [Fact]
    public void BuildStartArguments_Format()
    {
        Assert.Equal(@"--user-data-dir=""C:\Claudin\perfis\y""", CommandLine.BuildStartArguments(@"C:\Claudin\perfis\y"));
    }

    [Fact]
    public void Split_ChromiumChildCommandLine()
    {
        var args = CommandLine.Split(
            @"""C:\Program Files\WindowsApps\Claude_5\app\claude.exe"" --type=crashpad-handler --user-data-dir=C:\Claudin\teste\C /prefetch:4 --monitor-self");

        Assert.Equal(@"C:\Program Files\WindowsApps\Claude_5\app\claude.exe", args[0]);
        Assert.True(CommandLine.HasSwitch(args, "type"));
        Assert.Equal(@"C:\Claudin\teste\C", CommandLine.GetSwitchValue(args, "user-data-dir"));
        Assert.True(CommandLine.HasSwitch(args, "monitor-self"));
        Assert.False(CommandLine.HasSwitch(args, "monitor"));
    }

    [Fact]
    public void Split_EmptyReturnsNothing()
    {
        Assert.Empty(CommandLine.Split(""));
        Assert.Empty(CommandLine.Split(null));
    }

    [Fact]
    public void GetSwitchValue_IgnoresExecutableAndMissing()
    {
        var args = CommandLine.Split(@"""--user-data-dir=X"" --outro=1");

        Assert.Null(CommandLine.GetSwitchValue(args, "user-data-dir")); // o primeiro argumento é o executável
        Assert.Equal("1", CommandLine.GetSwitchValue(args, "outro"));
        Assert.Null(CommandLine.GetSwitchValue(args, "nao-existe"));
    }
}
