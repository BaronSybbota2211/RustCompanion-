using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RustCompanion.App;

/// <summary>Логотип приложения: значок окон (в том числе без рамки) и области уведомлений.</summary>
internal static class AppIcon
{
    public static ImageSource? Source
    {
        get { try { return BitmapFrame.Create(new Uri("pack://application:,,,/Resources/app.ico")); } catch (Exception) { return null; } }
    }

    /// <summary>Значок трея: берётся кадр нужного размера из app.ico (а не масштабированная копия 32 px), чтобы эмблема была чёткой в 16 px и при масштабе экрана.</summary>
    public static System.Drawing.Icon Tray
    {
        get
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
                if (res is not null) { using var s = res.Stream; return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize); }
            }
            catch (Exception) { }
            try { var p = Environment.ProcessPath; if (p is not null) { var i = System.Drawing.Icon.ExtractAssociatedIcon(p); if (i is not null) return i; } } catch (Exception) { }
            return System.Drawing.SystemIcons.Application;
        }
    }
}
