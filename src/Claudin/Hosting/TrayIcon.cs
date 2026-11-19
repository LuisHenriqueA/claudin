using Claudin.Core;
using Forms = System.Windows.Forms;

namespace Claudin.Hosting;

/// <summary>Ícone na bandeja: mostrar o Claudin, alternar perfil e sair (mantendo ou fechando as contas do Claude).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly IReadOnlyList<ProfileInstance> _profiles;
    private readonly Func<ProfileInstance?> _active;
    private readonly Func<bool> _sendClaudeToTrayOnExit;

    public TrayIcon(IReadOnlyList<ProfileInstance> profiles, Func<ProfileInstance?> active, Func<bool> sendClaudeToTrayOnExit)
    {
        _profiles = profiles;
        _active = active;
        _sendClaudeToTrayOnExit = sendClaudeToTrayOnExit;
        _menu = new Forms.ContextMenuStrip();
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Claudin",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowRequested?.Invoke();
            }
        };
        BuildMenu();
    }

    public event Action? ShowRequested;

    public event Action<ProfileInstance>? ProfileRequested;

    public event Action? ExitRequested;

    public event Action? ExitAndCloseClaudeRequested;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var show = new Forms.ToolStripMenuItem("Mostrar o Claudin") { Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold) };
        show.Click += (_, _) => ShowRequested?.Invoke();
        _menu.Items.Add(show);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var active = _active();
        for (var i = 0; i < _profiles.Count; i++)
        {
            var inst = _profiles[i];
            var label = $"{inst.Profile.Name}{(inst.IsAlive ? "" : " (fechada)")}";
            var item = new Forms.ToolStripMenuItem(label)
            {
                Checked = ReferenceEquals(inst, active),
                ShortcutKeyDisplayString = i < 9 ? $"Ctrl+Alt+{i + 1}" : null,
            };
            item.Click += (_, _) => ProfileRequested?.Invoke(inst);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem(_sendClaudeToTrayOnExit()
            ? "Sair (o Claude das contas vai para a bandeja, aberto)"
            : "Sair (as contas do Claude continuam abertas)");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);
        var exitAll = new Forms.ToolStripMenuItem("Sair e fechar as contas do Claude…");
        exitAll.Click += (_, _) => ExitAndCloseClaudeRequested?.Invoke();
        _menu.Items.Add(exitAll);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Claudin.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Ícone da bandeja indisponível: {ex.Message}");
        }
        return System.Drawing.SystemIcons.Application;
    }
}
