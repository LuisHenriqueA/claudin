namespace Claudin.Core;

/// <summary>Retângulo em pixels físicos de tela.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>
/// Bordas invisíveis da janela: diferença entre GetWindowRect e DWMWA_EXTENDED_FRAME_BOUNDS.
/// Na Fase 0: 9 px à esquerda, à direita e embaixo, e 0 em cima (a 150%).
/// </summary>
public readonly record struct FrameMargins(int Left, int Top, int Right, int Bottom)
{
    public const int MaxPlausible = 64;

    public static FrameMargins Zero => default;

    /// <summary>Calcula as bordas. Valores fora do plausível (janela oculta, DWM indisponível) viram zero.</summary>
    public static FrameMargins FromRects(PixelRect window, PixelRect visibleFrame)
    {
        var m = new FrameMargins(
            visibleFrame.X - window.X,
            visibleFrame.Y - window.Y,
            window.Right - visibleFrame.Right,
            window.Bottom - visibleFrame.Bottom);
        return m.IsPlausible ? m : Zero;
    }

    public bool IsPlausible =>
        Left is >= 0 and <= MaxPlausible && Top is >= 0 and <= MaxPlausible
        && Right is >= 0 and <= MaxPlausible && Bottom is >= 0 and <= MaxPlausible;
}

public static class WindowGeometry
{
    /// <summary>Retângulo a passar ao SetWindowPos para que a parte visível da janela ocupe exatamente a área host.</summary>
    public static PixelRect Expand(PixelRect host, FrameMargins margins) =>
        new(host.X - margins.Left, host.Y - margins.Top,
            host.Width + margins.Left + margins.Right, host.Height + margins.Top + margins.Bottom);
}

/// <summary>
/// Botões de minimizar, maximizar e fechar que o Claude desenha no canto superior direito
/// ("Window Controls Overlay" do Electron). O Claudin recorta esse canto da janela com SetWindowRgn, para ficarem só os
/// botões do Claudin. As medidas vêm do app.asar (só leitura): faixa de 36 DIP na janela principal, com 3 botões de 46 DIP.
/// </summary>
public static class CaptionButtons
{
    public const double WidthDip = 138;
    public const double HeightDip = 36;

    /// <summary>Retângulo a recortar, relativo ao canto da janela (o que GetWindowRect devolve, com as bordas invisíveis).</summary>
    public static PixelRect Hole(int windowWidth, FrameMargins margins, uint dpi)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var right = windowWidth - margins.Right;
        var width = Math.Min((int)Math.Ceiling(WidthDip * scale), Math.Max(0, right - margins.Left));
        return new PixelRect(right - width, margins.Top, width, (int)Math.Ceiling(HeightDip * scale));
    }
}

/// <summary>
/// Limita quantas vezes o Claudin "puxa de volta" uma janela num intervalo. Evita brigar para sempre com o Claude
/// (por exemplo, quando o tamanho mínimo dele é maior que a área host).
/// </summary>
public sealed class SnapLimiter(int maxAttempts, TimeSpan window, Func<DateTime> now)
{
    private readonly Queue<DateTime> _attempts = new();

    public bool TryAcquire()
    {
        var t = now();
        while (_attempts.Count > 0 && t - _attempts.Peek() > window)
        {
            _attempts.Dequeue();
        }
        if (_attempts.Count >= maxAttempts)
        {
            return false;
        }
        _attempts.Enqueue(t);
        return true;
    }

    public void Reset() => _attempts.Clear();
}
