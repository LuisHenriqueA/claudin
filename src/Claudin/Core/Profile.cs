namespace Claudin.Core;

public sealed class Profile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Cor em hexadecimal (#RRGGBB ou #AARRGGBB).</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>
    /// Pasta de dados do Claude para este perfil. Aceita variáveis de ambiente (ex.: %APPDATA%\Claude).
    /// Vazio ou nulo = &lt;raiz dos perfis&gt;\&lt;Id&gt;.
    /// </summary>
    public string? DataDir { get; set; }

    public string ResolveDataDir(string profilesRoot)
    {
        var raw = string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.ExpandEnvironmentVariables(profilesRoot), Id)
            : Environment.ExpandEnvironmentVariables(DataDir);
        return PathUtil.Normalize(raw);
    }

    /// <summary>True quando o perfil usa a pasta padrão do Claude (%APPDATA%\Claude), que pertence ao Claude.</summary>
    public bool UsesClaudeDefaultDataDir(string profilesRoot) =>
        PathUtil.AreSame(ResolveDataDir(profilesRoot), ClaudeDefaults.DefaultDataDir);
}
