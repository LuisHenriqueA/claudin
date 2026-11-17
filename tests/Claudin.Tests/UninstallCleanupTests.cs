using Claudin.Core;

namespace Claudin.Tests;

public sealed class UninstallCleanupTests
{
    private const string ClaudeDefault = @"C:\Users\u\AppData\Roaming\Claude";

    private static readonly string[] Protected =
    [
        @"C:\Users\u",
        @"C:\Users\u\AppData\Roaming",
        @"C:\Users\u\AppData\Local",
        @"C:\Windows",
        @"C:\Program Files",
    ];

    private static AppConfig Config(string root, params Profile[] profiles) => new() { ProfilesRoot = root, Profiles = [.. profiles] };

    [Fact]
    public void DefaultProfiles_OnlyOwnFolderIsDeletable()
    {
        var config = Config(@"C:\Claudin\perfis",
            new Profile { Id = "x", DataDir = ClaudeDefault },
            new Profile { Id = "y" });

        var dirs = UninstallCleanup.GetDeletableProfileDirs(config, ClaudeDefault, Protected);

        Assert.Equal([@"C:\Claudin\perfis\y"], dirs);
    }

    [Theory]
    [InlineData(ClaudeDefault)]                                   // a própria pasta do Claude
    [InlineData(@"C:\USERS\U\APPDATA\ROAMING\CLAUDE\")]           // em outra grafia
    [InlineData(@"C:\Users\u\AppData\Roaming")]                    // algo que contém a pasta do Claude
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\sub")]        // algo dentro da pasta do Claude
    [InlineData(@"C:\Users\u")]                                    // perfil do usuário
    [InlineData(@"C:\Users")]                                      // contém pasta protegida
    [InlineData(@"C:\")]                                           // raiz
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows\System32")]                           // dentro de pasta protegida? (contém? não; mas é protegida por estar dentro)
    public void NeverDeletes_DangerousFolders(string dir)
    {
        var protectedPlusInside = Protected.Append(@"C:\Windows\System32").ToArray();

        Assert.False(UninstallCleanup.IsSafeToDelete(dir, ClaudeDefault, protectedPlusInside));
    }

    [Theory]
    [InlineData(@"C:\Claudin\perfis\y")]
    [InlineData(@"D:\Dados\ClaudeConta2")]
    [InlineData(@"C:\Users\u\AppData\Local\Claudin-perfis\y")] // dentro de pasta protegida, mas não a contém
    public void AllowsDeleting_OwnProfileFolders(string dir)
    {
        Assert.True(UninstallCleanup.IsSafeToDelete(dir, ClaudeDefault, Protected));
    }

    [Fact]
    public void Duplicates_AreListedOnce()
    {
        var config = Config(@"C:\P",
            new Profile { Id = "a", DataDir = @"C:\Mesma" },
            new Profile { Id = "b", DataDir = @"c:\mesma\" });

        Assert.Single(UninstallCleanup.GetDeletableProfileDirs(config, ClaudeDefault, Protected));
    }

    [Fact]
    public void ProfilePointingToAppData_IsRefused()
    {
        var config = Config(@"C:\P", new Profile { Id = "a", DataDir = @"C:\Users\u\AppData\Roaming" });

        Assert.Empty(UninstallCleanup.GetDeletableProfileDirs(config, ClaudeDefault, Protected));
    }

    [Fact]
    public void IsSameOrAncestor_DoesNotMatchPrefixesOfNames()
    {
        Assert.False(UninstallCleanup.IsSameOrAncestor(@"C:\Claude", @"C:\Claude-Data"));
        Assert.True(UninstallCleanup.IsSameOrAncestor(@"C:\Claude", @"C:\Claude\x"));
    }
}
