using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RustCompanion.App.Services;
using RustCompanion.Core.Notifications;

namespace RustCompanion.App;

/// <summary>
/// Внешнее обычное окно Windows поверх остальных: не активируется, в режиме «закреплено» пропускает мышь и клавиатуру сквозь себя.
/// Не внедряется в процесс Rust и не читает память игры.
/// </summary>
public partial class OverlayWindow : Window
{
    private bool _locked = true;
    public event Action? BoundsCommitted;
    public int LastTopmostError { get; private set; }
    public bool LastTopmostOk { get; private set; } = true;

    public OverlayWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) => { if (!_locked) { try { DragMove(); } catch (InvalidOperationException) { } BoundsCommitted?.Invoke(); } };
        SizeChanged += (_, _) => { if (!_locked && IsLoaded) BoundsCommitted?.Invoke(); };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyLock(_locked);
    }

    /// <summary>locked=true: клики проходят сквозь окно (WS_EX_TRANSPARENT). locked=false: окно можно перетаскивать и менять размер.</summary>
    public void ApplyLock(bool locked)
    {
        _locked = locked;
        var h = Handle;
        if (h != IntPtr.Zero)
        {
            var ex = OverlayWindowStyles.Apply(Win32.GetWindowLong(h, OverlayWindowStyles.GWL_EXSTYLE));
            if (!locked) ex &= ~OverlayWindowStyles.WS_EX_TRANSPARENT;
            Win32.SetWindowLong(h, OverlayWindowStyles.GWL_EXSTYLE, ex);
        }
        IsHitTestVisible = !locked;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        Chrome.BorderThickness = locked ? new Thickness(0) : new Thickness(1);
        EditHint.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private sealed record ToastView(string Key, ChamferBorder Card);
    private readonly Dictionary<string, ToastView> _toasts = new();     // только «живые» карточки; уходящие в словаре не хранятся
    private int _leaving;
    private static readonly Brush TitleBrush = Frozen(0xF0, 0xEC, 0xEA), BodyBrush = Frozen(0xD6, 0xD0, 0xCE), MutedBrush = Frozen(0xA3, 0x9B, 0x99), LineBrush = Frozen(0x3A, 0x34, 0x36);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    /// <summary>Идёт ли сейчас анимация исчезновения: окно нельзя скрывать, пока она не закончилась.</summary>
    public bool HasExitAnimations => _leaving > 0;

    public void SetContent(IReadOnlyList<string> hud, IReadOnlyList<OverlayToastData> toasts)
    {
        HudLines.ItemsSource = hud.ToList();
        HudPanel.Visibility = hud.Count > 0 || !_locked ? Visibility.Visible : Visibility.Collapsed;
        SyncToasts(toasts);
        ToastHost.Margin = new Thickness(0, HudPanel.Visibility == Visibility.Visible && ToastHost.Children.Count > 0 ? 6 : 0, 0, 0);
    }

    private static string KeyOf(OverlayToastData d) => $"{d.Kind}|{d.At.UtcTicks}|{d.Title}|{d.Body}";

    /// <summary>Сравнивает показанные карточки с очередью: новые появляются с анимацией, исчезнувшие уходят плавно, остальные не трогаются (анимации не перезапускаются каждую секунду).</summary>
    private void SyncToasts(IReadOnlyList<OverlayToastData> toasts)
    {
        var wanted = toasts.Select(t => (Key: KeyOf(t), Data: t)).ToList();
        var keys = wanted.Select(w => w.Key).ToHashSet();
        foreach (var v in _toasts.Values.Where(v => !keys.Contains(v.Key)).ToList()) RemoveToast(v);
        foreach (var (key, data) in wanted)
            if (!_toasts.ContainsKey(key)) AddToast(key, data);
    }

    private void AddToast(string key, OverlayToastData d)
    {
        var (color, thickness) = ToastLook.For(OverlayToastKinds.CategoryOf(d.Kind));
        var accent = new SolidColorBrush(color);                       // не замороженная: цвет анимируется при появлении
        var card = new ChamferBorder
        {
            Style = (Style)FindResource("ToastCard"), AccentBrush = accent, AccentThickness = thickness,
            Margin = new Thickness(0, 0, 0, 6), RenderTransform = new TranslateTransform(0, 0)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = d.Title, Foreground = TitleBrush, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var time = new TextBlock { Text = d.At.ToLocalTime().ToString("HH:mm"), Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 1); grid.Children.Add(time);
        var line = new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 5, 0, 5) };
        Grid.SetRow(line, 1); Grid.SetColumnSpan(line, 2); grid.Children.Add(line);
        var body = new TextBlock { Text = d.Body, Foreground = BodyBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(body, 2); Grid.SetColumnSpan(body, 2); grid.Children.Add(body);
        card.Child = grid;

        _toasts[key] = new ToastView(key, card);
        ToastHost.Children.Add(card);

        // Короткое появление: проявление + небольшой сдвиг сверху; линия акцента на мгновение светлее. Анимации одноразовые (FillBehavior.Stop), постоянной нагрузки нет.
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(180);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ToastLook.Lighten(color), color, TimeSpan.FromMilliseconds(350)) { FillBehavior = FillBehavior.Stop });
    }

    private void RemoveToast(ToastView v)
    {
        _toasts.Remove(v.Key);
        var card = v.Card; var h = card.ActualHeight;
        if (h <= 0) { ToastHost.Children.Remove(card); return; }
        _leaving++;
        var dur = TimeSpan.FromMilliseconds(200);
        card.Height = h;                                                    // фиксируем высоту, чтобы плавно схлопнуть: соседние карточки сдвинутся без скачка
        var collapse = new DoubleAnimation(h, 0, dur);
        collapse.Completed += (_, _) => { ToastHost.Children.Remove(card); if (_leaving > 0) _leaving--; };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur));
        card.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(card.Margin, new Thickness(0), dur));
        card.BeginAnimation(FrameworkElement.HeightProperty, collapse);
    }

    public void Place(double x, double y, double width)
    {
        Left = x; Top = y; Width = width;
    }

    /// <summary>Перемещение в физических пикселях Windows (та же система координат, что у мониторов и окна Rust).</summary>
    public bool MovePx(int x, int y, double widthDip)
    {
        Width = widthDip;
        var h = Handle;
        return h != IntPtr.Zero && Win32.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public (int X, int Y)? GetPositionPx() => Handle != IntPtr.Zero && Win32.GetWindowRect(Handle, out var r) ? (r.Left, r.Top) : null;

    /// <summary>Повторно ставит окно выше остальных topmost-окон без активации. Результат запоминается для диагностики.</summary>
    public void ReassertTopmost()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return;
        LastTopmostOk = Win32.SetWindowPos(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        LastTopmostError = LastTopmostOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
    }

    public int ExStyle => Handle == IntPtr.Zero ? 0 : Win32.GetWindowLong(Handle, OverlayWindowStyles.GWL_EXSTYLE);
    public DpiScale CurrentDpi => VisualTreeHelper.GetDpi(this);
}
