using System;
using RustCompanion.Core;

namespace RustCompanion.App.Services;

public sealed class TrayService : INotifier, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayService(Action open, Action exit, Action hideOverlay)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => open());
        menu.Items.Add("Скрыть оверлей", null, (_, _) => hideOverlay());
        menu.Items.Add("Выход", null, (_, _) => exit());
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = RustCompanion.App.AppIcon.Tray,
            Text = "Rust Companion",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public void Notify(string title, string message)
    {
        _icon.ShowBalloonTip(10000, title, message, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
