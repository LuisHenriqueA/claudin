namespace Claudin.Core;

/// <summary>Fatos sobre a instalação oficial do Claude Desktop levantados na Fase 0 (docs/ACHADOS.md).</summary>
public static class ClaudeDefaults
{
    /// <summary>Família do pacote MSIX do Claude Desktop.</summary>
    public const string PackageFamilyName = "Claude_pzs8sxrjxfjjc";

    /// <summary>Alias de execução do pacote, em %LOCALAPPDATA%\Microsoft\WindowsApps. Caminho estável entre versões.</summary>
    public const string AliasFileName = "claude-desktop.exe";

    /// <summary>Executável dentro da pasta de instalação do pacote.</summary>
    public const string PackageRelativeExe = @"app\Claude.exe";

    /// <summary>Pasta de dados que o Claude usa quando iniciado sem --user-data-dir (e pelos links claude://).</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
}
