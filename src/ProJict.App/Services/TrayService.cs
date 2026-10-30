using System.Windows;
using Forms = System.Windows.Forms;

namespace ProJict.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    /// <summary>What a click on the notification showing now does.</summary>
    private Action? _clicked;

    public TrayService(Action open, Action quickCapture, Action exportAll, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ProJict.ico"))!.Stream;
        using (stream)
        {
            _icon = new Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
                Text = "ProJict",
                Visible = true,
            };
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ProJict", null, (_, _) => open());
        menu.Items.Add("Quick capture…", null, (_, _) => quickCapture());
        menu.Items.Add("Export all now", null, (_, _) => exportAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var clicked = _clicked;
            _clicked = null;
            clicked?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _clicked = null;
    }

    /// <summary>A Windows notification from ProJict's icon; a click on it runs <paramref name="clicked"/>.</summary>
    public void ShowNotice(string title, string text, Action? clicked)
    {
        _clicked = clicked;
        // Windows cuts longer ones.
        _icon.ShowBalloonTip(10000, title.Length > 63 ? title[..62] + "…" : title, text.Length > 250 ? text[..249] + "…" : text, Forms.ToolTipIcon.None);
    }

    public void SetHotkeyHint(string? hotkey) =>
        _icon.Text = string.IsNullOrWhiteSpace(hotkey) ? "ProJict" : $"ProJict — quick capture: {hotkey}";

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
