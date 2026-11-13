using System.Windows.Media;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.App.ViewModels;

public static class BellBrushes
{
    private static SolidColorBrush Make(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public static readonly Brush Green = Make("#3FB950"), Red = Make("#E5604D"), Gray = Make("#8A94A0");
    public static Brush For(RpBellState s) => s switch { RpBellState.Green => Green, RpBellState.Red => Red, _ => Gray };
}
