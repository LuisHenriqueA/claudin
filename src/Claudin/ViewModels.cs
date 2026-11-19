using System.Windows.Media;
using Claudin.Core;

namespace Claudin;

public sealed class ProfileButtonViewModel(ProfileInstance instance, bool isActive) : ObservableObject
{
    private bool _isActive = isActive;
    private Brush _colorBrush = ColorBrushes.FromHex(instance.Profile.Color);

    public ProfileInstance Instance { get; } = instance;

    public Profile Profile => Instance.Profile;

    public string Name => Profile.Name;

    public Brush ColorBrush => _colorBrush;

    public string ToolTip => $"{Profile.Name}\nPasta de dados: {Instance.DataDir}"
        + (_usageSummary is null ? "" : "\n" + _usageSummary);

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Nome ou cor mudaram nas configurações.</summary>
    public void Refresh()
    {
        _colorBrush = ColorBrushes.FromHex(Profile.Color);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(ToolTip));
        RefreshUsageRows();
    }

    // ---------- Consumo do plano (1.1) ----------

    /// <summary>Leitura acima disso é mostrada como desatualizada (o Claude grava a cada 5 a 15 min com a conta aberta).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(45);

    private PlanUsageResult _usage = new(PlanUsageStatus.NoFile);
    private DateTimeOffset _usageReadAt;
    private string? _usageSummary;

    public IReadOnlyList<UsageRowViewModel> UsageRows { get; private set; } = [];

    /// <summary>"Atualizado às 15:04 (há 3 min)" ou o motivo de não haver dados.</summary>
    public string UsageStatusText { get; private set; } = "";

    public bool UsageIsStale { get; private set; }

    public bool HasUsageRows => UsageRows.Count > 0;

    /// <summary>Relê o histórico que o Claude grava na pasta de dados desta conta.</summary>
    public void RefreshUsage(DateTimeOffset now)
    {
        _usage = PlanUsageReader.Read(Instance.DataDir, now);
        _usageReadAt = now;
        RefreshUsageRows();
    }

    private void RefreshUsageRows()
    {
        var snapshot = _usage.Snapshot;
        UsageRows = snapshot is null || snapshot.Windows.Count == 0
            ? []
            : snapshot.Windows.Select(w => new UsageRowViewModel(w, _colorBrush)).ToList();

        var age = snapshot is null ? TimeSpan.Zero : _usageReadAt - snapshot.Timestamp;
        UsageIsStale = snapshot is not null && age >= StaleAfter;
        UsageStatusText = _usage.Status switch
        {
            PlanUsageStatus.Ok when snapshot!.Windows.Count == 0 => "O Claude não registrou nenhuma janela de limite nesta conta.",
            PlanUsageStatus.Ok => $"Atualizado {FormatWhen(snapshot!.Timestamp.ToLocalTime(), _usageReadAt.ToLocalTime())} "
                + $"({PlanUsageReader.FormatAge(age)})"
                + (UsageIsStale ? ". Abra a conta para o Claude atualizar." : ""),
            PlanUsageStatus.Empty or PlanUsageStatus.NoFile =>
                "Sem dados ainda. O Claude registra o uso depois que a conta é aberta e está logada.",
            _ => "Não foi possível ler o uso desta conta (o formato do Claude pode ter mudado).",
        };
        _usageSummary = UsageRows.Count == 0
            ? null
            : string.Join(" · ", UsageRows.Take(2).Select(r => $"{r.Label}: {r.ValueText}"))
              + $" ({PlanUsageReader.FormatAge(age)})";

        OnPropertyChanged(nameof(UsageRows));
        OnPropertyChanged(nameof(HasUsageRows));
        OnPropertyChanged(nameof(UsageStatusText));
        OnPropertyChanged(nameof(UsageIsStale));
        OnPropertyChanged(nameof(ToolTip));
    }

    private static string FormatWhen(DateTimeOffset when, DateTimeOffset now) =>
        when.Date == now.Date ? $"às {when:HH:mm}" : $"em {when:dd/MM} às {when:HH:mm}";
}

/// <summary>Uma barra do painel de consumo.</summary>
public sealed class UsageRowViewModel
{
    private static readonly Brush Warn = ColorBrushes.FromHex("#F2C46B");
    private static readonly Brush Error = ColorBrushes.FromHex("#F28B82");
    private static readonly Brush Muted = ColorBrushes.FromHex("#6A6A6A");

    public UsageRowViewModel(UsageWindow window, Brush profileBrush)
    {
        Label = window.Label;
        Expired = window.Expired;
        Percent = window.Expired ? 0 : Math.Clamp(window.Percent, 0, 100);
        ValueText = window.Expired ? "renovada" : $"{Math.Round(window.Percent):0}%";
        BarBrush = window.Expired ? Muted
            : window.Percent >= 95 ? Error
            : window.Percent >= 80 ? Warn
            : profileBrush;
        ValueBrush = window.Expired ? Muted : window.Percent >= 80 ? BarBrush : null;
        ToolTip = window.Expired
            ? "A última leitura é mais antiga que esta janela: ela já renovou desde então."
            : $"{Label}: {window.Percent:0.#}% usado";
    }

    public string Label { get; }

    /// <summary>0 a 100, para a barra.</summary>
    public double Percent { get; }

    public string ValueText { get; }

    public Brush BarBrush { get; }

    /// <summary>Cor do número; nulo = cor normal do texto.</summary>
    public Brush? ValueBrush { get; }

    public bool Expired { get; }

    public string ToolTip { get; }
}

public sealed record FolderRowViewModel(string Name, Brush ColorBrush, string Path, string Status, Brush StatusBrush);

internal static class ColorBrushes
{
    public static Brush FromHex(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }
}
