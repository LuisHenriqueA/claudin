using Claudin.Core;

namespace Claudin.Tests;

public sealed class WindowGeometryTests
{
    [Fact]
    public void CaptionHole_MatchesMeasurementAt100Percent()
    {
        // Medido com a instância de teste: janela 1510 px, bordas 7/0/7/7, botões de 1365 a 1503, 36 px de altura.
        var hole = CaptionButtons.Hole(1510, new FrameMargins(7, 0, 7, 7), 96);

        Assert.Equal(new PixelRect(1365, 0, 138, 36), hole);
    }

    [Fact]
    public void CaptionHole_ScalesWithDpi_AndNeverPassesTheLeftEdge()
    {
        Assert.Equal(new PixelRect(1400 - 9 - 207, 0, 207, 54), CaptionButtons.Hole(1400, new FrameMargins(9, 0, 9, 9), 144));

        var tiny = CaptionButtons.Hole(100, new FrameMargins(9, 0, 9, 9), 144);
        Assert.Equal(9, tiny.X);
        Assert.Equal(82, tiny.Width);
    }

    [Fact]
    public void Margins_FromPhase0Measurement()
    {
        // Fase 0, a 150%: GetWindowRect 100,150 1400x900; DWM 109,150 1382x891.
        var m = FrameMargins.FromRects(new PixelRect(100, 150, 1400, 900), new PixelRect(109, 150, 1382, 891));

        Assert.Equal(new FrameMargins(9, 0, 9, 9), m);
    }

    [Fact]
    public void Expand_MakesVisibleFrameMatchHost()
    {
        var host = new PixelRect(60, 93, 1798, 1115);
        var margins = new FrameMargins(9, 0, 9, 9);

        var target = WindowGeometry.Expand(host, margins);

        Assert.Equal(new PixelRect(51, 93, 1816, 1124), target);
        // o que sobra visível depois de tirar as bordas é exatamente a área host
        Assert.Equal(host, new PixelRect(target.X + 9, target.Y, target.Width - 18, target.Height - 9));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, 0, 65, 0)]
    public void Margins_Implausible_BecomeZero(int l, int t, int r, int b)
    {
        var window = new PixelRect(0, 0, 1000, 1000);
        var frame = PixelRect.FromLtrb(l, t, 1000 - r, 1000 - b);

        Assert.Equal(FrameMargins.Zero, FrameMargins.FromRects(window, frame));
    }

    [Fact]
    public void Margins_HiddenWindowReportsGarbage_BecomeZero()
    {
        // Janela oculta: o DWM pode devolver um retângulo vazio.
        Assert.Equal(FrameMargins.Zero, FrameMargins.FromRects(new PixelRect(100, 100, 800, 600), default));
    }

    [Fact]
    public void PixelRect_FromLtrb()
    {
        var r = PixelRect.FromLtrb(-11, 37, 1931, 1211);

        Assert.Equal(new PixelRect(-11, 37, 1942, 1174), r);
        Assert.Equal(1931, r.Right);
        Assert.Equal(1211, r.Bottom);
    }

    [Fact]
    public void SnapLimiter_AllowsBurstThenBlocks_ThenRecoversAfterWindow()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new SnapLimiter(3, TimeSpan.FromSeconds(3), () => now);

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        now = now.AddSeconds(4);
        Assert.True(limiter.TryAcquire());

        limiter.Reset();
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
    }
}
