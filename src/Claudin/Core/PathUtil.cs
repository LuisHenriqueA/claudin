namespace Claudin.Core;

public static class PathUtil
{
    /// <summary>Caminho absoluto, sem barra final (exceto na raiz do volume).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
    }

    /// <summary>Compara caminhos como o Windows (sem diferenciar maiúsculas/minúsculas).</summary>
    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
