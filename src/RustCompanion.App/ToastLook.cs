using System.Windows.Media;
using RustCompanion.Core.Notifications;

namespace RustCompanion.App;

/// <summary>Цвет и толщина акцентной линии карточки уведомления по её группе (см. таблицу оформления этапа 3).</summary>
internal static class ToastLook
{
    public static (Color Color, double Thickness) For(ToastCategory c) => c switch
    {
        ToastCategory.Alarm => (Color.FromRgb(0xF0, 0x2B, 0x32), 3),
        ToastCategory.Timer => (Color.FromRgb(0xF0, 0x2B, 0x32), 2),
        ToastCategory.Warning => (Color.FromRgb(0xE0, 0x80, 0x3A), 2),
        ToastCategory.Forecast => (Color.FromRgb(0xD9, 0x9A, 0x35), 2),
        ToastCategory.Restored => (Color.FromRgb(0x42, 0xA6, 0x6A), 2),
        _ => (Color.FromRgb(0x8A, 0x94, 0xA0), 2)
    };

    /// <summary>Светлее исходного цвета (для короткой подсветки линии при появлении).</summary>
    public static Color Lighten(Color c, double k = 0.55) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
}
