using System.Text.Json;

namespace Claudin.Core;

/// <summary>Uma janela de limite do plano (sessão de 5 h, semanal…) na última leitura do Claude.</summary>
/// <param name="Key">Chave curta usada pelo Claude no arquivo (fh, sd…).</param>
/// <param name="Percent">Uso em %, como o Claude gravou (pode passar de 100).</param>
/// <param name="Expired">
/// A leitura é mais antiga que a própria janela (por exemplo, mais de 5 h para a sessão): ela já renovou desde então,
/// e o valor não vale mais.
/// </param>
public sealed record UsageWindow(string Key, string Label, double Percent, bool Expired);

/// <summary>A amostra mais recente do histórico de uso de uma conta.</summary>
public sealed record PlanUsageSnapshot(DateTimeOffset Timestamp, IReadOnlyList<UsageWindow> Windows);

public enum PlanUsageStatus
{
    Ok,

    /// <summary>O arquivo não existe: a conta ainda não foi aberta e logada, ou o Claude é de uma versão que não o grava.</summary>
    NoFile,

    /// <summary>O arquivo existe, mas não tem nenhuma amostra.</summary>
    Empty,

    /// <summary>Formato desconhecido (o Claude mudou o arquivo) ou falha de leitura.</summary>
    Unreadable,
}

public sealed record PlanUsageResult(PlanUsageStatus Status, PlanUsageSnapshot? Snapshot = null);

/// <summary>
/// Lê o histórico de uso do plano que o <b>próprio Claude Desktop</b> grava na pasta de dados de cada conta
/// (<c>plan-usage-history.json</c>, 30 dias de amostras, uma a cada poucos minutos enquanto a conta está aberta).
/// O arquivo só tem percentuais por janela de limite e o id da organização (ignorado aqui): nenhum token, cookie ou
/// credencial (regra 2 do plano). Os horários de renovação não são gravados nele.
/// </summary>
/// <remarks>
/// Formato visto no Claude 6.6395 (lido no <c>app.asar</c>, só leitura):
/// <c>{ "version": 2, "samples": [ { "t": &lt;ms Unix&gt;, "org": "&lt;uuid&gt;", "u": { "fh": 24, "sd": 68 } } ] }</c>.
/// A versão 1 tinha <c>fh</c> e <c>sd</c> direto na amostra. É um formato interno, sem documentação: qualquer outra
/// coisa vira <see cref="PlanUsageStatus.Unreadable"/>, sem derrubar o Claudin.
/// </remarks>
public static class PlanUsageReader
{
    public const string FileName = "plan-usage-history.json";

    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    /// <summary>Janelas mostradas, na ordem de exibição. As outras chaves do arquivo são ignoradas.</summary>
    private static readonly (string Key, string Label, TimeSpan? Length)[] KnownWindows =
    [
        ("fh", "Sessão (5 h)", FiveHours),
        ("sd", "Semanal", SevenDays),
        ("sn", "Semanal · Sonnet", SevenDays),
        ("so", "Semanal · Opus", SevenDays),
        ("cw", "Semanal · Cowork", SevenDays),
        ("xu", "Uso extra (mês)", null),
    ];

    public static PlanUsageResult Read(string dataDir, DateTimeOffset now)
    {
        var path = Path.Combine(dataDir, FileName);
        string json;
        try
        {
            // O Claude grava por troca atômica (arquivo novo + rename); o compartilhamento amplo evita brigar com ele.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PlanUsageResult(PlanUsageStatus.NoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PlanUsageResult(PlanUsageStatus.Unreadable);
        }
        return Parse(json, now);
    }

    public static PlanUsageResult Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
            {
                return new PlanUsageResult(PlanUsageStatus.Unreadable);
            }

            JsonElement? latest = null;
            long latestT = long.MinValue;
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind == JsonValueKind.Object
                    && sample.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms) && ms > latestT)
                {
                    latest = sample;
                    latestT = ms;
                }
            }
            if (latest is not { } chosen)
            {
                return new PlanUsageResult(samples.GetArrayLength() == 0 ? PlanUsageStatus.Empty : PlanUsageStatus.Unreadable);
            }

            // Versão 2: valores dentro de "u". Versão 1: fh/sd direto na amostra.
            var values = chosen.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.Object ? u : chosen;
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(latestT);
            var age = now - timestamp;

            var windows = new List<UsageWindow>();
            foreach (var (key, label, length) in KnownWindows)
            {
                if (values.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var pct))
                {
                    windows.Add(new UsageWindow(key, label, pct, length is { } l && age >= l));
                }
            }
            return new PlanUsageResult(PlanUsageStatus.Ok, new PlanUsageSnapshot(timestamp, windows));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return new PlanUsageResult(PlanUsageStatus.Unreadable);
        }
    }

    /// <summary>"agora", "há 3 min", "há 2 h", "há 3 dias".</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "agora";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"há {(int)age.TotalMinutes} min";
        }
        if (age < TimeSpan.FromDays(1))
        {
            return $"há {(int)age.TotalHours} h";
        }
        var days = (int)age.TotalDays;
        return days == 1 ? "há 1 dia" : $"há {days} dias";
    }
}
