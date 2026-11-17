using System.Windows;
using System.Windows.Interop;
using Claudin.Core;
using Claudin.Interop;

namespace Claudin;

/// <summary>"Renomear" no clique direito da aba de um perfil.</summary>
public partial class RenameWindow : Window
{
    private readonly string _id;
    private readonly IReadOnlyList<string> _otherNames;

    public RenameWindow(string id, string currentName, IReadOnlyList<string> otherNames)
    {
        InitializeComponent();
        _id = id;
        _otherNames = otherNames;
        NameBox.Text = currentName;
        NameBox.MaxLength = SettingsValidation.MaxNameLength;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark, sizeof(int));
        };
    }

    /// <summary>O nome escolhido, já sem espaços nas pontas.</summary>
    public string NewName => NameBox.Text.Trim();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (SettingsValidation.ValidateName(_id, NameBox.Text, _otherNames) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
