using System.ComponentModel;
using Claudin.Interop;

namespace Claudin.Core;

public enum ClaudeSource { Override, Alias, Package }

public sealed record ClaudeInstall(string ExecutablePath, ClaudeSource Source, string? PackageFullName, string? Version);

public sealed record LocateResult(ClaudeInstall? Install, IReadOnlyList<string> Notes)
{
    public bool Found => Install is not null;
}

public sealed record PackageInfo(string FullName, string InstallPath);

/// <summary>Acesso ao sistema usado pelo locator (substituível nos testes).</summary>
public interface IClaudeSystem
{
    string LocalAppData { get; }

    bool FileExists(string path);

    IReadOnlyList<PackageInfo> GetPackagesByFamily(string familyName);
}

/// <summary>
/// Encontra o executável do Claude Desktop. Ordem (Fase 0):
/// 1. caminho sobrescrito na configuração;
/// 2. alias claude-desktop.exe (estável entre versões);
/// 3. pacote MSIX: InstallLocation\app\Claude.exe (versão mais recente).
/// </summary>
public sealed class ClaudeInstallLocator(IClaudeSystem system)
{
    public LocateResult Locate(string? overridePath)
    {
        var notes = new List<string>();

        PackageInfo? package = null;
        try
        {
            package = system.GetPackagesByFamily(ClaudeDefaults.PackageFamilyName)
                .OrderByDescending(p => ParseVersion(p.FullName))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            notes.Add($"Falha ao consultar o pacote do Claude: {ex.Message}");
        }
        var version = package is null ? null : ParseVersion(package.FullName)?.ToString();

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var path = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
            if (system.FileExists(path))
            {
                return new LocateResult(new ClaudeInstall(path, ClaudeSource.Override, null, null), notes);
            }
            notes.Add($"O caminho configurado não existe: {path}. Usando a detecção automática.");
        }

        var alias = Path.Combine(system.LocalAppData, "Microsoft", "WindowsApps", ClaudeDefaults.AliasFileName);
        if (system.FileExists(alias))
        {
            return new LocateResult(new ClaudeInstall(alias, ClaudeSource.Alias, package?.FullName, version), notes);
        }
        notes.Add($"Alias {ClaudeDefaults.AliasFileName} não encontrado.");

        if (package is null)
        {
            notes.Add("O Claude Desktop (pacote da Microsoft Store/MSIX) não está instalado para este usuário.");
            return new LocateResult(null, notes);
        }

        var exe = Path.Combine(package.InstallPath, ClaudeDefaults.PackageRelativeExe);
        if (system.FileExists(exe))
        {
            return new LocateResult(new ClaudeInstall(exe, ClaudeSource.Package, package.FullName, version), notes);
        }
        notes.Add($"Pacote {package.FullName} encontrado, mas sem {ClaudeDefaults.PackageRelativeExe}.");
        return new LocateResult(null, notes);
    }

    /// <summary>Extrai a versão de um nome completo de pacote (Nome_Versão_Arquitetura_Recurso_Publicador).</summary>
    public static Version? ParseVersion(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : null;
    }
}

public sealed class WindowsClaudeSystem : IClaudeSystem
{
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<PackageInfo> GetPackagesByFamily(string familyName)
    {
        uint count = 0, bufferLength = 0;
        var rc = NativeMethods.GetPackagesByPackageFamily(familyName, ref count, null, ref bufferLength, null);
        if (rc == NativeMethods.ERROR_SUCCESS && count == 0)
        {
            return [];
        }
        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new Win32Exception(rc);
        }

        var pointers = new IntPtr[count];
        var buffer = new char[bufferLength];
        rc = NativeMethods.GetPackagesByPackageFamily(familyName, ref count, pointers, ref bufferLength, buffer);
        if (rc != NativeMethods.ERROR_SUCCESS)
        {
            throw new Win32Exception(rc);
        }

        // O buffer traz os nomes completos separados por '\0'. Os ponteiros apontam para dentro dele.
        var result = new List<PackageInfo>();
        foreach (var fullName in new string(buffer, 0, (int)bufferLength).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            uint pathLength = 0;
            if (NativeMethods.GetPackagePathByFullName(fullName, ref pathLength, null) != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }
            var pathBuffer = new char[pathLength];
            if (NativeMethods.GetPackagePathByFullName(fullName, ref pathLength, pathBuffer) == NativeMethods.ERROR_SUCCESS)
            {
                result.Add(new PackageInfo(fullName, new string(pathBuffer).TrimEnd('\0')));
            }
        }
        return result;
    }
}
