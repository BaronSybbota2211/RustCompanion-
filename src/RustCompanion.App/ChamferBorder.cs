using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RustCompanion.App;

/// <summary>
/// Рамка со срезанными углами (верхний левый и нижний правый) и красной линией сверху — фон карточек уведомлений Overlay («Щит»).
/// Наследует Border, поэтому Child, Padding, Background, BorderBrush и BorderThickness работают как обычно; рисуется один раз при изменении размера, без анимаций и эффектов.
/// </summary>
public class ChamferBorder : Border
{
    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentThicknessProperty = DependencyProperty.Register(nameof(AccentThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Размер среза угла, в единицах WPF.</summary>
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    /// <summary>Цвет акцентной линии вдоль верхнего края (null — без линии). Цвет можно менять по типу события.</summary>
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public double AccentThickness { get => (double)GetValue(AccentThicknessProperty); set => SetValue(AccentThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var c = Math.Max(0, Math.Min(Chamfer, Math.Min(w, h) / 2));
        var t = BorderThickness.Left; var half = t / 2;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(c, half), true, true);
            ctx.LineTo(new Point(w - half, half), true, false);
            ctx.LineTo(new Point(w - half, h - c), true, false);
            ctx.LineTo(new Point(w - c, h - half), true, false);
            ctx.LineTo(new Point(half, h - half), true, false);
            ctx.LineTo(new Point(half, c), true, false);
        }
        geo.Freeze();

        Pen? pen = null;
        if (t > 0 && BorderBrush is not null) { pen = new Pen(BorderBrush, t) { LineJoin = PenLineJoin.Miter }; }
        dc.DrawGeometry(Background, pen, geo);

        if (AccentBrush is not null && AccentThickness > 0)
        {
            var ap = new Pen(AccentBrush, AccentThickness);   // не замораживается: кисть акцента может анимироваться (подсветка при появлении)
            dc.DrawLine(ap, new Point(c, AccentThickness / 2), new Point(w, AccentThickness / 2));
        }
    }
}
