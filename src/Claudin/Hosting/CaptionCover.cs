using System.Windows.Forms;

namespace Claudin.Hosting;

/// <summary>
/// Janelinha do Claudin, sem foco e fora da barra de tarefas, que fica logo acima do Claude ativo cobrindo o canto
/// onde estavam os botões de janela dele. O recorte (SetWindowRgn) sozinho não basta: o Windows pinta a área recortada
/// de cinza (#202020) em vez de mostrar o que está atrás, então a capa recebe a cor do cabeçalho do Claude.
/// </summary>
internal sealed class CaptionCover : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    public CaptionCover()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = System.Drawing.Color.FromArgb(0x19, 0x19, 0x19);
        Text = "";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE; // clicar na capa não tira o foco do Claude
            return;
        }
        base.WndProc(ref m);
    }
}
