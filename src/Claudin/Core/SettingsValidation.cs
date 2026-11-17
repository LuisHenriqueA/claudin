namespace Claudin.Core;

public sealed record ProfileEdit(string Id, string Name, string Color);

/// <summary>Validação da tela de configurações (sem dependência de interface, para testar).</summary>
public static class SettingsValidation
{
    public const int MaxNameLength = 40;

    public static IReadOnlyList<string> Validate(string profilesRoot, string? claudeOverride, IReadOnlyList<ProfileEdit> profiles,
        Func<string, bool> fileExists)
    {
        var errors = new List<string>();

        var root = Environment.ExpandEnvironmentVariables(profilesRoot.Trim());
        if (string.IsNullOrWhiteSpace(root))
        {
            errors.Add("Informe a raiz das pastas dos perfis.");
        }
        else if (!Path.IsPathFullyQualified(root) || root.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            errors.Add("A raiz das pastas precisa ser um caminho completo, como C:\\Claudin\\perfis.");
        }

        if (!string.IsNullOrWhiteSpace(claudeOverride))
        {
            var exe = Environment.ExpandEnvironmentVariables(claudeOverride.Trim().Trim('"'));
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !fileExists(exe))
            {
                errors.Add("O caminho do Claude precisa apontar para um .exe que exista (ou ficar vazio para detecção automática).");
            }
        }

        var names = new List<string>();
        foreach (var p in profiles)
        {
            var name = p.Name.Trim();
            if (ValidateName(p.Id, name, names) is { } error)
            {
                errors.Add(error);
            }
            names.Add(name);
            if (!AppConfig.IsValidColor(p.Color.Trim()))
            {
                errors.Add($"Cor inválida em \"{(name.Length > 0 ? name : p.Id)}\": use #RRGGBB.");
            }
        }
        return errors;
    }

    /// <summary>Valida o nome de um perfil (configurações e "Renomear" da aba). Nulo = válido.</summary>
    public static string? ValidateName(string id, string name, IEnumerable<string> otherNames)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return $"O perfil '{id}' precisa de um nome.";
        }
        if (name.Length > MaxNameLength)
        {
            return $"O nome do perfil '{id}' passa de {MaxNameLength} caracteres.";
        }
        if (otherNames.Any(o => string.Equals(o.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return $"Já existe um perfil com o nome \"{name}\".";
        }
        return null;
    }
}
