namespace Claudin.Core;

/// <summary>
/// Log simples em arquivo (%LOCALAPPDATA%\Claudin\logs). Nunca lança exceção.
/// O caminho do perfil do usuário é trocado por %USERPROFILE% para não gravar o nome do usuário.
/// </summary>
public static class Log
{
    private static LogWriter? _writer;

    public static void Init(string directory) => _writer = new LogWriter(directory);

    /// <summary>Para de gravar (usado antes de apagar a pasta de logs na desinstalação).</summary>
    public static void Disable() => _writer = null;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERRO", message, ex);

    public static string Sanitize(string text, string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(userProfile)
            ? text
            : text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"[{level}] {Sanitize(message)}";
        if (ex is not null)
        {
            line += Environment.NewLine + "    " + Sanitize(ex.ToString());
        }
        _writer?.Write(line);
    }
}

/// <summary>
/// Arquivos claudin-AAAAMMDD.log com rotação: um arquivo por dia, e um novo (-1, -2…) quando passa do tamanho máximo.
/// Arquivos mais antigos que o prazo de retenção são apagados na inicialização.
/// </summary>
public sealed class LogWriter
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;
    public const int DefaultRetentionDays = 14;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly Func<DateTime> _now;
    private string? _currentPath;
    private DateTime _currentDay;
    private long _currentSize;

    public LogWriter(string directory, long maxBytes = DefaultMaxBytes, int retentionDays = DefaultRetentionDays, Func<DateTime>? now = null)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _now = now ?? (() => DateTime.Now);
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOlderThan(retentionDays);
        }
        catch
        {
            // Sem log não é motivo para derrubar o app.
        }
    }

    public string? CurrentPath => _currentPath;

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var now = _now();
                var text = $"{now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
                var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                if (_currentPath is null || now.Date != _currentDay || _currentSize + bytes > _maxBytes)
                {
                    Roll(now, bytes);
                }
                File.AppendAllText(_currentPath!, text);
                _currentSize += bytes;
            }
        }
        catch
        {
            // Ignorado de propósito.
        }
    }

    /// <summary>Escolhe o primeiro arquivo do dia em que a próxima linha cabe (ou um vazio, se a linha sozinha passar do limite).</summary>
    private void Roll(DateTime now, long nextBytes)
    {
        _currentDay = now.Date;
        var baseName = $"claudin-{now:yyyyMMdd}";
        for (var i = 0; ; i++)
        {
            var path = Path.Combine(_directory, i == 0 ? $"{baseName}.log" : $"{baseName}-{i}.log");
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size == 0 || size + nextBytes <= _maxBytes)
            {
                _currentPath = path;
                _currentSize = size;
                return;
            }
        }
    }

    private void DeleteOlderThan(int days)
    {
        var limit = _now().Date.AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(_directory, "claudin-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= 16
                && DateTime.TryParseExact(name.Substring(8, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < limit)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
