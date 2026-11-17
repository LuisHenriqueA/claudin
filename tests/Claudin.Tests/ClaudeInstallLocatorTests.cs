using Claudin.Core;

namespace Claudin.Tests;

public sealed class ClaudeInstallLocatorTests
{
    private const string LocalAppData = @"C:\Users\teste\AppData\Local";
    private const string Alias = @"C:\Users\teste\AppData\Local\Microsoft\WindowsApps\claude-desktop.exe";
    private const string PkgOld = "Claude_5.1100.0.0_x64__pzs8sxrjxfjjc";
    private const string PkgNew = "Claude_5.1231.0.0_x64__pzs8sxrjxfjjc";
    private const string PkgNewDir = @"C:\Program Files\WindowsApps\Claude_5.1231.0.0_x64__pzs8sxrjxfjjc";
    private const string PkgOldDir = @"C:\Program Files\WindowsApps\Claude_5.1100.0.0_x64__pzs8sxrjxfjjc";

    private sealed class FakeSystem : IClaudeSystem
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<PackageInfo> Packages { get; } = [];

        public bool ThrowOnPackages { get; set; }

        public string? RequestedFamily { get; private set; }

        public string LocalAppData => ClaudeInstallLocatorTests.LocalAppData;

        public bool FileExists(string path) => Files.Contains(path);

        public IReadOnlyList<PackageInfo> GetPackagesByFamily(string familyName)
        {
            RequestedFamily = familyName;
            if (ThrowOnPackages)
            {
                throw new InvalidOperationException("falha simulada");
            }
            return Packages;
        }
    }

    private static FakeSystem FullInstall()
    {
        var sys = new FakeSystem();
        sys.Files.Add(Alias);
        sys.Files.Add(PkgNewDir + @"\app\Claude.exe");
        sys.Files.Add(PkgOldDir + @"\app\Claude.exe");
        sys.Packages.Add(new PackageInfo(PkgOld, PkgOldDir));
        sys.Packages.Add(new PackageInfo(PkgNew, PkgNewDir));
        return sys;
    }

    [Fact]
    public void Alias_IsPreferred_AndVersionComesFromNewestPackage()
    {
        var sys = FullInstall();

        var result = new ClaudeInstallLocator(sys).Locate(null);

        Assert.True(result.Found);
        Assert.Equal(Alias, result.Install!.ExecutablePath);
        Assert.Equal(ClaudeSource.Alias, result.Install.Source);
        Assert.Equal(PkgNew, result.Install.PackageFullName);
        Assert.Equal("5.1231.0.0", result.Install.Version);
        Assert.Equal(ClaudeDefaults.PackageFamilyName, sys.RequestedFamily);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Override_WhenExists_Wins()
    {
        var sys = FullInstall();
        sys.Files.Add(@"D:\Claude\claude.exe");

        var result = new ClaudeInstallLocator(sys).Locate("  \"D:\\Claude\\claude.exe\"  ");

        Assert.Equal(@"D:\Claude\claude.exe", result.Install!.ExecutablePath);
        Assert.Equal(ClaudeSource.Override, result.Install.Source);
        Assert.Null(result.Install.Version);
    }

    [Fact]
    public void Override_WhenMissing_FallsBackToAlias_WithNote()
    {
        var result = new ClaudeInstallLocator(FullInstall()).Locate(@"D:\nao\existe.exe");

        Assert.Equal(ClaudeSource.Alias, result.Install!.Source);
        Assert.Contains(result.Notes, n => n.Contains(@"D:\nao\existe.exe"));
    }

    [Fact]
    public void WithoutAlias_UsesNewestPackageExe()
    {
        var sys = FullInstall();
        sys.Files.Remove(Alias);

        var result = new ClaudeInstallLocator(sys).Locate(null);

        Assert.Equal(ClaudeSource.Package, result.Install!.Source);
        Assert.Equal(PkgNewDir + @"\app\Claude.exe", result.Install.ExecutablePath);
        Assert.Equal("5.1231.0.0", result.Install.Version);
    }

    [Fact]
    public void PackageWithoutExe_IsNotFound()
    {
        var sys = new FakeSystem();
        sys.Packages.Add(new PackageInfo(PkgNew, PkgNewDir));

        var result = new ClaudeInstallLocator(sys).Locate(null);

        Assert.False(result.Found);
        Assert.Contains(result.Notes, n => n.Contains(PkgNew));
    }

    [Fact]
    public void NothingInstalled_ReturnsNotFoundWithExplanation()
    {
        var result = new ClaudeInstallLocator(new FakeSystem()).Locate(null);

        Assert.False(result.Found);
        Assert.Null(result.Install);
        Assert.Contains(result.Notes, n => n.Contains("não está instalado"));
    }

    [Fact]
    public void PackageQueryFailure_DoesNotPreventAlias()
    {
        var sys = new FakeSystem { ThrowOnPackages = true };
        sys.Files.Add(Alias);

        var result = new ClaudeInstallLocator(sys).Locate(null);

        Assert.Equal(ClaudeSource.Alias, result.Install!.Source);
        Assert.Null(result.Install.Version);
        Assert.Contains(result.Notes, n => n.Contains("falha simulada"));
    }

    [Theory]
    [InlineData(PkgNew, "5.1231.0.0")]
    [InlineData("Claude_1.2.3.4_neutral__abc", "1.2.3.4")]
    [InlineData("SemVersao", null)]
    [InlineData("Nome_xyz_x64__p", null)]
    public void ParseVersion(string fullName, string? expected)
    {
        Assert.Equal(expected, ClaudeInstallLocator.ParseVersion(fullName)?.ToString());
    }
}
