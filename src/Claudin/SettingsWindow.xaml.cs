using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Claudin.Core;
using Claudin.Interop;
using Microsoft.Win32;

namespace Claudin;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly StartupRegistration _startup;
    private readonly List<ProfileEditViewModel> _profiles;

    public SettingsWindow(AppConfig config, LocateResult claude, StartupRegistration startup)
    {
        _config = config;
        _startup = startup;
        InitializeComponent();

        _profiles = config.Profiles.Select(p => new ProfileEditViewModel(p, p.ResolveDataDir(config.ProfilesRoot))).ToList();
        ProfilesList.ItemsSource = _profiles;
        RootBox.Text = config.ProfilesRoot;
        ClaudePathBox.Text = config.ClaudeExecutableOverride ?? "";
        DetectedText.Text = claude.Install is { } i
            ? $"Em uso: {i.ExecutablePath}{(i.Version is null ? "" : $" (versão {i.Version})")}"
            : "Claude Desktop não encontrado.";

        var exe = Environment.ProcessPath;
        StartWithWindowsBox.IsChecked = exe is not null && SafeIsEnabled(exe);
        OpenActiveBox.IsChecked = config.OpenActiveProfileOnStart;
        CloseToTrayBox.IsChecked = config.CloseToTray;
        SendClaudeToTrayBox.IsChecked = config.SendClaudeToTrayOnExit;
        HideCaptionBox.IsChecked = config.HideClaudeCaptionButtons;
        HideTaskbarBox.IsChecked = config.HideClaudeTaskbarButtons;
        ProfileFrameBox.IsChecked = config.ShowProfileFrame;
        UpdateRestoreButton();

        SourceInitialized += (_, _) =>
        {
            // Barra de título escura, como o resto do Claudin.
            var dark = 1;
            NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark, sizeof(int));
        };
    }

    /// <summary>Raiz das pastas ou caminho do Claude mudaram: só valem depois de reiniciar.</summary>
    public bool RestartNeeded { get; private set; }

    private bool SafeIsEnabled(string exe)
    {
        try
        {
            return _startup.IsEnabled(exe);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao ler o início com o Windows", ex);
            return false;
        }
    }

    private void UpdateRestoreButton()
    {
        RestoreRegistryButton.IsEnabled = _startup.HasBackup;
        if (!_startup.HasBackup && string.IsNullOrEmpty(RestoreStatus.Text))
        {
            RestoreStatus.Text = "Nada alterado até agora.";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var edits = _profiles.Select(p => new ProfileEdit(p.Id, p.Name, p.Color)).ToList();
        var overridePath = string.IsNullOrWhiteSpace(ClaudePathBox.Text) ? null : ClaudePathBox.Text.Trim().Trim('"');
        var errors = SettingsValidation.Validate(RootBox.Text, overridePath, edits, File.Exists);
        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join("\n", errors);
            return;
        }

        var root = RootBox.Text.Trim();
        RestartNeeded = !string.Equals(root, _config.ProfilesRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(overridePath, _config.ClaudeExecutableOverride, StringComparison.OrdinalIgnoreCase);

        foreach (var edit in _profiles)
        {
            edit.Apply();
        }
        _config.ProfilesRoot = root;
        _config.ClaudeExecutableOverride = overridePath;
        _config.OpenActiveProfileOnStart = OpenActiveBox.IsChecked == true;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        _config.SendClaudeToTrayOnExit = SendClaudeToTrayBox.IsChecked == true;
        _config.HideClaudeCaptionButtons = HideCaptionBox.IsChecked == true;
        _config.HideClaudeTaskbarButtons = HideTaskbarBox.IsChecked == true;
        _config.ShowProfileFrame = ProfileFrameBox.IsChecked == true;

        var wantStartup = StartWithWindowsBox.IsChecked == true;
        if (Environment.ProcessPath is { } exe && wantStartup != SafeIsEnabled(exe))
        {
            try
            {
                if (wantStartup)
                {
                    _startup.Enable(exe);
                }
                else
                {
                    _startup.Disable();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao alterar o início com o Windows", ex);
                ErrorText.Text = $"Não foi possível alterar o início com o Windows: {ex.Message}";
                return;
            }
        }
        _config.StartWithWindows = wantStartup;

        DialogResult = true;
    }

    private void OnBrowseRootClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Raiz das pastas dos perfis" };
        var current = Environment.ExpandEnvironmentVariables(RootBox.Text.Trim());
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }
        if (dialog.ShowDialog(this) == true)
        {
            RootBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseClaudeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Executável do Claude Desktop",
            Filter = "Claude Desktop (claude.exe)|claude.exe;Claude.exe;claude-desktop.exe|Programas (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ClaudePathBox.Text = dialog.FileName;
        }
    }

    private void OnClearClaudeClick(object sender, RoutedEventArgs e) => ClaudePathBox.Text = "";

    private void OnRestoreRegistryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var restored = _startup.RestoreOriginal();
            RestoreStatus.Text = restored ? "Registro restaurado." : "Nada para restaurar.";
            if (Environment.ProcessPath is { } exe)
            {
                StartWithWindowsBox.IsChecked = SafeIsEnabled(exe);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao restaurar o registro", ex);
            RestoreStatus.Text = $"Falha: {ex.Message}";
        }
        UpdateRestoreButton();
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDir);

    private void OnOpenConfigClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Home);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir pasta", ex);
        }
    }
}

public sealed class ProfileEditViewModel : ObservableObject
{
    private static readonly string[] PresetColors =
        ["#D97757", "#4A8FE7", "#5BB98C", "#B57EDC", "#E5B84B", "#E06C9F", "#4FB3BF", "#9AA0A6"];

    private readonly Profile _profile;
    private string _name;
    private string _color;

    public ProfileEditViewModel(Profile profile, string dataDir)
    {
        _profile = profile;
        _name = profile.Name;
        _color = profile.Color;
        FolderText = $"Pasta de dados: {dataDir}"
            + (PathUtil.AreSame(dataDir, ClaudeDefaults.DefaultDataDir) ? " (pasta padrão do Claude, recebe os links claude://)" : "");
        Presets = PresetColors.Select(hex => new ColorPreset(hex, ColorBrushes.FromHex(hex), new RelayCommand(() => Color = hex))).ToList();
    }

    public string Id => _profile.Id;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Color
    {
        get => _color;
        set
        {
            if (Set(ref _color, value))
            {
                OnPropertyChanged(nameof(PreviewBrush));
            }
        }
    }

    public Brush PreviewBrush => AppConfig.IsValidColor(Color.Trim()) ? ColorBrushes.FromHex(Color.Trim()) : Brushes.Transparent;

    public string FolderText { get; }

    public string NameLabel => $"Nome do perfil {Id}";

    public string ColorLabel => $"Cor do perfil {Id} em hexadecimal";

    public IReadOnlyList<ColorPreset> Presets { get; }

    public void Apply()
    {
        _profile.Name = Name.Trim();
        _profile.Color = Color.Trim().ToUpperInvariant();
    }
}

public sealed record ColorPreset(string Hex, Brush Brush, ICommand Pick);

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
