using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Overlay;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W; public double Bottom => Y + H;
    public double VisibleWidthIn(RectD o) => Math.Max(0, Math.Min(Right, o.Right) - Math.Max(X, o.X));
    public double VisibleHeightIn(RectD o) => Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));
}

public static class OverlayLayout
{
    public const double MinWidth = 160, MaxWidth = 900, MinVisible = 48;

    public static double ClampWidth(double w) => double.IsNaN(w) ? 320 : Math.Clamp(w, MinWidth, MaxWidth);

    /// <summary>
    /// Итоговая позиция оверлея. Сохранённая позиция используется, только если окно достаточно видимо на виртуальном экране
    /// (учёт смены мониторов/разрешения); иначе — угол рабочей области основного монитора.
    /// </summary>
    public static (double X, double Y, double Width, bool UsedSaved) Resolve(double? left, double? top, double width, double height,
        RectD virtualScreen, RectD workArea, OverlayCorner corner, double margin)
    {
        var w = ClampWidth(width);
        if (left is { } l && top is { } t && !double.IsNaN(l) && !double.IsNaN(t))
        {
            var r = new RectD(l, t, w, Math.Max(height, 1));
            if (r.VisibleWidthIn(virtualScreen) >= Math.Min(MinVisible, w) && r.VisibleHeightIn(virtualScreen) >= Math.Min(MinVisible, r.H))
                return (l, t, w, true);
        }
        var (x, y) = OverlayPlacement.Compute(corner, workArea.X, workArea.Y, workArea.W, workArea.H, w, height, margin);
        return (x, y, w, false);
    }
}

public sealed record MonitorRect(RectD Bounds, RectD Work, bool IsPrimary);
public sealed record PlacementResult(double X, double Y, int MonitorIndex, bool UsedSaved, string Reason);

/// <summary>
/// Выбор монитора для оверлея. Все прямоугольники — в одних и тех же пикселях Windows. Монитор берётся по окну клиента Rust
/// (наибольшее пересечение). Если окно Rust не найдено или свёрнуто — используется сохранённая позиция либо основной монитор.
/// </summary>
public static class OverlayTarget
{
    public static int PickMonitor(IReadOnlyList<MonitorRect> monitors, RectD? windowRect)
    {
        if (windowRect is not { } w) return -1;
        var best = -1; double bestArea = 0;
        for (var i = 0; i < monitors.Count; i++)
        {
            var area = w.VisibleWidthIn(monitors[i].Bounds) * w.VisibleHeightIn(monitors[i].Bounds);
            if (area > bestArea) { bestArea = area; best = i; }
        }
        return best;
    }

    public static PlacementResult Resolve(double? savedLeft, double? savedTop, double widthPx, double heightPx, IReadOnlyList<MonitorRect> monitors,
        RectD? rustWindow, OverlayCorner corner, double marginPx)
    {
        if (monitors.Count == 0) return new PlacementResult(savedLeft ?? 0, savedTop ?? 0, -1, savedLeft is not null, "Мониторы не определены");
        var primary = Math.Max(0, monitors.ToList().FindIndex(m => m.IsPrimary));
        var rustMonitor = PickMonitor(monitors, rustWindow);
        var union = monitors.Select(m => m.Bounds).Aggregate((a, b) =>
        { var x = Math.Min(a.X, b.X); var y = Math.Min(a.Y, b.Y); return new RectD(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y); });

        bool hasSaved = false;
        if (savedLeft is { } l && savedTop is { } t && !double.IsNaN(l) && !double.IsNaN(t))
        {
            var r = new RectD(l, t, widthPx, Math.Max(heightPx, 1));
            hasSaved = r.VisibleWidthIn(union) >= Math.Min(OverlayLayout.MinVisible, widthPx) && r.VisibleHeightIn(union) >= Math.Min(OverlayLayout.MinVisible, r.H);
        }

        if (rustMonitor >= 0)
        {
            if (hasSaved)
            {
                var cx = savedLeft!.Value + widthPx / 2; var cy = savedTop!.Value + heightPx / 2;
                var b = monitors[rustMonitor].Bounds;
                if (cx >= b.X && cx < b.Right && cy >= b.Y && cy < b.Bottom)
                    return new PlacementResult(savedLeft.Value, savedTop.Value, rustMonitor, true, "Сохранённая позиция на мониторе с Rust");
            }
            var wa = monitors[rustMonitor].Work;
            var (x, y) = OverlayPlacement.Compute(corner, wa.X, wa.Y, wa.W, wa.H, widthPx, heightPx, marginPx);
            return new PlacementResult(x, y, rustMonitor, false, "Оверлей перенесён на монитор с Rust");
        }

        if (hasSaved)
            return new PlacementResult(savedLeft!.Value, savedTop!.Value, -1, true, "Окно Rust не найдено — сохранённая позиция");
        var pw = monitors[primary].Work;
        var (px, py) = OverlayPlacement.Compute(corner, pw.X, pw.Y, pw.W, pw.H, widthPx, heightPx, marginPx);
        return new PlacementResult(px, py, primary, false, "Окно Rust не найдено — основной монитор");
    }
}

public sealed record HudEntry(string Id, string Label, DateTimeOffset? TargetUtc, string? Note = null);

public static class OverlayHud
{
    public const string Chinook = "chinook", Heli = "heli", BradleyAppear = "bradley_appear", Airdrop = "airdrop", Cargo = "cargo",
        BradleyDestroyed = "bradley_destroyed", Containers = "containers", Monuments = "monuments";

    public static readonly IReadOnlyList<(string Id, string Title)> Catalog = new[]
    {
        (Chinook, "Чинук (прогноз)"), (Heli, "Патрульный вертолёт (прогноз)"), (BradleyAppear, "Bradley (прогноз по отметкам)"), (Airdrop, "Аэрдроп (прогноз)"),
        (Cargo, "Грузовой корабль (прогноз)"), (BradleyDestroyed, "Bradley (после уничтожения)"), (Containers, "Закрытые контейнеры"), (Monuments, "Респавн отслеживаемых РТ")
    };

    public static ISet<string> ParseSelection(string? csv) =>
        new HashSet<string>((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => Catalog.Any(c => c.Id == id)));

    public static string Serialize(IEnumerable<string> ids) => string.Join(",", Catalog.Select(c => c.Id).Where(ids.Contains));

    /// <summary>Строки оверлея: ближайшие события сверху. «~» — оценка; без прогноза — «нет прогноза», а не выдуманное время.</summary>
    public static IReadOnlyList<string> Build(IEnumerable<HudEntry> entries, DateTimeOffset now, int max = 6) =>
        entries.OrderBy(e => e.TargetUtc is null ? 1 : 0).ThenBy(e => e.TargetUtc)
            .Take(Math.Max(1, max))
            .Select(e =>
            {
                if (e.TargetUtc is not { } t) return $"{e.Label}  — {(string.IsNullOrEmpty(e.Note) ? "нет прогноза" : e.Note)}";
                var rem = t - now;
                return rem > TimeSpan.Zero
                    ? $"{e.Label}  ~{t.ToLocalTime():HH:mm}  ({(int)rem.TotalHours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2})"
                    : $"{e.Label}  ~{t.ToLocalTime():HH:mm}  время наступило";
            }).ToList();
}

public enum FullscreenState { Unknown, NotFullscreen, FullscreenWindowOrApp, ExclusiveD3D, Other }

public static class OverlayDiagnostics
{
    /// <summary>Расшифровка SHQueryUserNotificationState: единственный стандартный способ отличить эксклюзивный D3D-полноэкран от остальных режимов.</summary>
    public static (FullscreenState State, string Advice) FromNotificationState(int quns) => quns switch
    {
        3 => (FullscreenState.ExclusiveD3D, "Обнаружен эксклюзивный полноэкранный режим Direct3D: обычное окно оверлея в нём не отображается. Переключите Rust в оконный или безрамочный режим (Borderless/Windowed)."),
        2 => (FullscreenState.FullscreenWindowOrApp, "Активно полноэкранное приложение (оно может быть и безрамочным окном). Оверлей должен быть виден; если нет — переключите Rust в Borderless/Windowed."),
        4 => (FullscreenState.Other, "Включён режим презентации Windows: оверлей может быть скрыт системой."),
        5 or 1 or 6 or 7 => (FullscreenState.NotFullscreen, "Полноэкранного приложения не обнаружено: оверлей должен быть виден поверх оконного/безрамочного режима."),
        _ => (FullscreenState.Unknown, "Состояние экрана не удалось определить.")
    };
}
