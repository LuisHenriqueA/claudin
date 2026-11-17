using System.Runtime.InteropServices;
using System.Text;
using Claudin.Interop;

namespace Claudin.Core;

/// <summary>Montagem e leitura de linhas de comando no padrão do Windows (o mesmo que o Chromium usa).</summary>
public static class CommandLine
{
    public const string UserDataDirSwitch = "user-data-dir";
    public const string TypeSwitch = "type";

    /// <summary>Argumentos para iniciar o Claude com uma pasta de dados própria.</summary>
    public static string BuildStartArguments(string dataDir) => $"--{UserDataDirSwitch}={Quote(dataDir)}";

    /// <summary>Coloca entre aspas seguindo as regras do CommandLineToArgvW (barras antes de aspas são dobradas).</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Divide uma linha de comando com CommandLineToArgvW.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        // Com string vazia, CommandLineToArgvW devolve o caminho do processo atual.
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }
        var argv = NativeMethods.CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            }
            return result;
        }
        finally
        {
            NativeMethods.LocalFree(argv);
        }
    }

    /// <summary>Valor de um switch no formato --nome=valor (o primeiro argumento, o executável, é ignorado).</summary>
    public static string? GetSwitchValue(IReadOnlyList<string> args, string name)
    {
        var prefix = $"--{name}=";
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }
        return null;
    }

    public static bool HasSwitch(IReadOnlyList<string> args, string name)
    {
        var exact = $"--{name}";
        for (var i = 1; i < args.Count; i++)
        {
            if (string.Equals(args[i], exact, StringComparison.OrdinalIgnoreCase)
                || args[i].StartsWith(exact + "=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
