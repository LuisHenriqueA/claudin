namespace Claudin.Core;

/// <summary>
/// Decide o que a desinstalação pode apagar. Regra de ouro: a pasta padrão do Claude (%APPDATA%\Claude) nunca é tocada,
/// nem nada que a contenha ou esteja dentro dela, nem pastas do sistema ou do usuário.
/// </summary>
public static class UninstallCleanup
{
    /// <summary>Pastas que nunca podem ser apagadas, nem coisas que as contenham.</summary>
    public static IReadOnlyList<string> DefaultProtectedDirs() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    /// <summary>Pastas de dados dos perfis que podem ser apagadas (sem checar se existem).</summary>
    public static IReadOnlyList<string> GetDeletableProfileDirs(AppConfig config, string claudeDefaultDir, IEnumerable<string> protectedDirs)
    {
        var protectedList = protectedDirs.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var result = new List<string>();
        foreach (var profile in config.Profiles)
        {
            string dir;
            try
            {
                dir = profile.ResolveDataDir(config.ProfilesRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (IsSafeToDelete(dir, claudeDefaultDir, protectedList) && !result.Any(r => PathUtil.AreSame(r, dir)))
            {
                result.Add(dir);
            }
        }
        return result;
    }

    public static bool IsSafeToDelete(string dir, string claudeDefaultDir, IEnumerable<string> protectedDirs)
    {
        string full;
        try
        {
            full = PathUtil.Normalize(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return false; // raiz de unidade
        }
        // Nem a pasta do Claude, nem o que a contém, nem o que está dentro dela.
        if (IsSameOrAncestor(full, claudeDefaultDir) || IsSameOrAncestor(claudeDefaultDir, full))
        {
            return false;
        }
        // Nem pastas protegidas, nem o que as contém.
        return !protectedDirs.Any(p => IsSameOrAncestor(full, p));
    }

    /// <summary>True se <paramref name="ancestor"/> é igual a <paramref name="path"/> ou o contém.</summary>
    public static bool IsSameOrAncestor(string ancestor, string path)
    {
        var a = PathUtil.Normalize(ancestor).TrimEnd('\\') + "\\";
        var p = PathUtil.Normalize(path).TrimEnd('\\') + "\\";
        return p.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}
