using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Claudin.Core;

public sealed partial class AppConfig
{
    public const int CurrentVersion = 1;
    public const string DefaultProfilesRoot = @"C:\Claudin\perfis";
    public const string FallbackColor = "#808080";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Raiz das pastas de perfis sem pasta explícita. Aceita variáveis de ambiente.</summary>
    public string ProfilesRoot { get; set; } = DefaultProfilesRoot;

    /// <summary>Caminho do Claude escolhido pelo usuário. Nulo = detecção automática.</summary>
    public string? ClaudeExecutableOverride { get; set; }

    public string? ActiveProfileId { get; set; }

    public List<Profile> Profiles { get; set; } = [];

    /// <summary>Abrir a instância do perfil ativo quando o Claudin inicia.</summary>
    public bool OpenActiveProfileOnStart { get; set; } = true;

    /// <summary>O X do Claudin esconde na bandeja em vez de sair.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Recortar os botões de minimizar, maximizar e fechar do Claude, deixando só os do Claudin.</summary>
    public bool HideClaudeCaptionButtons { get; set; } = true;

    /// <summary>Moldura fina na cor da conta ativa em volta do Claude.</summary>
    public bool ShowProfileFrame { get; set; } = true;

    /// <summary>Tirar o botão do Claude da barra de tarefas, ficando só o do Claudin.</summary>
    public bool HideClaudeTaskbarButtons { get; set; } = true;

    /// <summary>Desejo do usuário; o estado real fica no registro (StartupRegistration).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Última posição e tamanho da janela (pixels físicos, coordenadas de área de trabalho).</summary>
    public WindowBounds? Window { get; set; }

    [JsonIgnore]
    public Profile? ActiveProfile => FindProfile(ActiveProfileId);

    public Profile? FindProfile(string? id) =>
        id is null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AppConfig CreateDefault() => new()
    {
        ActiveProfileId = "x",
        Profiles =
        [
            // Fase 0: o perfil X usa a pasta padrão do Claude, que é onde caem os links claude://.
            new Profile { Id = "x", Name = "Conta X", Color = "#D97757", DataDir = @"%APPDATA%\Claude" },
            new Profile { Id = "y", Name = "Conta Y", Color = "#4A8FE7" },
        ],
    };

    /// <summary>Corrige valores inválidos vindos do arquivo. Retorna a descrição de cada correção.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var fixes = new List<string>();

        if (string.IsNullOrWhiteSpace(ProfilesRoot))
        {
            ProfilesRoot = DefaultProfilesRoot;
            fixes.Add("profilesRoot vazio; usando o padrão.");
        }

        if (ClaudeExecutableOverride is not null && string.IsNullOrWhiteSpace(ClaudeExecutableOverride))
        {
            ClaudeExecutableOverride = null;
        }

        Profiles ??= [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<Profile>();
        foreach (var p in Profiles)
        {
            if (p?.Id is not { } id || !IdRegex().IsMatch(id))
            {
                fixes.Add($"Perfil com id inválido removido: '{p?.Id}'.");
                continue;
            }
            if (!seen.Add(id))
            {
                fixes.Add($"Perfil duplicado removido: '{p.Id}'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                p.Name = p.Id;
                fixes.Add($"Perfil '{p.Id}' sem nome; usando o id.");
            }
            if (!ColorRegex().IsMatch(p.Color ?? ""))
            {
                fixes.Add($"Cor inválida no perfil '{p.Id}': '{p.Color}'.");
                p.Color = FallbackColor;
            }
            valid.Add(p);
        }
        Profiles = valid;

        if (Profiles.Count == 0)
        {
            Profiles = CreateDefault().Profiles;
            fixes.Add("Nenhum perfil válido; perfis padrão recriados.");
        }

        if (FindProfile(ActiveProfileId) is null)
        {
            ActiveProfileId = Profiles[0].Id;
            fixes.Add($"Perfil ativo inválido; usando '{ActiveProfileId}'.");
        }

        if (Window is not null && !Window.IsPlausible)
        {
            Window = null;
            fixes.Add("Posição de janela inválida descartada.");
        }

        return fixes;
    }

    public static bool IsValidColor(string? color) => color is not null && ColorRegex().IsMatch(color);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex ColorRegex();
}

public sealed class WindowBounds
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public bool Maximized { get; set; }

    [JsonIgnore]
    public bool IsPlausible => Right - Left >= 200 && Bottom - Top >= 150 && Right - Left <= 20000 && Bottom - Top <= 20000;
}
