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

    public string ToolTip => $"{Profile.Name}\nPasta de dados: {Instance.DataDir}";

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
    }
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
