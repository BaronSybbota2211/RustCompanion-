using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.App;

/// <summary>
/// Карта сервера Rust+ без рамки и заголовка: сетка, команда с именами под точками, зоны/неизвестные маркеры, которые сервер ещё отправляет.
/// Закрытие, сворачивание и показ панели настроек — по правой кнопке мыши. Размер значков на экране не зависит от приближения (при приближении они меньше относительно карты).
/// Перетаскивание окна — левой кнопкой, размер — за правый нижний угол. Обновляется каждые 5 секунд.
/// </summary>
public sealed class MapWindow : Window
{
    private readonly RustPlusManager _mgr; private readonly RustMapsClient _rustMaps;
    private readonly ComboBox _source = new() { Width = 150, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Картинка под данными Rust+. «RustMaps» берёт карту этого сервера с rustmaps.com (нужен ключ API в настройках)" };
    private readonly Slider _margin = new() { Minimum = 0, Maximum = 0.25, Value = 0, Width = 80, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Совмещение: ширина океана по краям картинки RustMaps (доля от её ширины). Подберите так, чтобы монументы Rust+ легли на значки RustMaps" };
    private readonly Button _layersBtn = new() { Content = "Слои RustMaps ▾", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Ресурсы, животные, зоны запрета строительства и другие слои с rustmaps.com поверх карты (нужен ключ API)" };
    private readonly Slider _layerOpacity = new() { Minimum = 0.2, Maximum = 1, Value = 0.6, Width = 70, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Прозрачность слоёв RustMaps" };
    private readonly Slider _layerInset = new() { Minimum = 0, Maximum = 0.25, Value = 0, Width = 70, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Совмещение слоёв: поле океана у картинки слоя (доля её ширины). По умолчанию берётся из автосовмещения; подправьте, если слой сдвинут" };
    private RustMapsInfo? _rmInfo; private string? _rmInfoKey, _rmViewer; private double? _rmRatio;
    private readonly Dictionary<string, BitmapImage> _layerBmp = new(); private readonly Dictionary<string, Image> _layerImg = new();
    private RpMap? _plusMap; private BitmapImage? _plusBmp, _rmBmp; private string? _rmKey; private bool _sourceBusy;
    private readonly Image _img = new() { Stretch = Stretch.Fill };
    private readonly Canvas _layer = new();
    private readonly Canvas _stage = new();
    private readonly Canvas _labels = new() { IsHitTestVisible = false };      // подписи рисуются в экранных пикселях, без ScaleTransform: чёткий текст при любом приближении
    private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _grid = Check("Сетка", true);
    private readonly CheckBox _team = Check("Команда", true);
    private readonly CheckBox _events = Check("Зоны и события", true);
    private readonly CheckBox _mons = Check("Монументы", false);
    private readonly CheckBox _look = Check("Направление взгляда", true, "Стрелка у игрока, если сервер Rust+ прислал угол поворота. Если стрелок нет — сервер этот угол не передаёт");
    private readonly CheckBox _notes = Check("Метки", true, "Метки команды из Rust+: точка смерти и отметки игроков");
    private readonly CheckBox _follow = Check("Центр на мне", false, "Карта приближена и следует за игроком, который сопряг Rust+ с этим приложением");
    private readonly CheckBox _top = Check("Поверх игры", false);
    private readonly Slider _zoom = new() { Minimum = 1.5, Maximum = 8, Value = 3, Width = 80, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Масштаб в режиме «Центр на мне»" };
    private readonly Slider _opacity = new() { Minimum = 0.3, Maximum = 1, Value = 1, Width = 80, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Прозрачность окна" };
    private readonly Grid _view = new() { ClipToBounds = true };
    private readonly ScaleTransform _scale = new(1, 1); private readonly TranslateTransform _move = new();
    private readonly WrapPanel _bar = new() { Margin = new Thickness(6) };
    private readonly Border _barBorder = new() { Background = new SolidColorBrush(Color.FromArgb(220, 0x16, 0x1A, 0x1F)), VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _gear = new() { Content = "⚙", Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), Opacity = 0.7, ToolTip = "Показать или скрыть панель настроек" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private RpMap? _map; private long _mapKey; private bool _busy;
    private IReadOnlyList<RpMapMarker> _lastMarkers = Array.Empty<RpMapMarker>();
    private (double X, double Y)? _me;
    private bool _auto = true;                         // true — карта вписана в окно; false — пользователь приближал/сдвигал вручную
    private Point? _dragFrom; private bool _dragging;
    private double Scale => Math.Max(0.01, _scale.ScaleX);

    private static CheckBox Check(string text, bool on, string? tip = null) =>
        new() { Content = text, IsChecked = on, Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };

    public MapWindow(RustPlusManager mgr, RustMapsClient rustMaps)
    {
        _mgr = mgr; _rustMaps = rustMaps;
        _source.Items.Add("Фон: карта Rust+"); _source.Items.Add("Фон: RustMaps"); _source.Items.Add("Фон: RustMaps + иконки"); _source.SelectedIndex = 0;
        _source.SelectionChanged += async (_, _) => await ApplySourceAsync();
        _margin.ValueChanged += (_, _) => { if (_source.SelectedIndex > 0 && _rmBmp is not null) SetBackdrop(_rmBmp, (int)Math.Round(_margin.Value * _rmBmp.PixelWidth)); };
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.CanResizeWithGrip;   // AllowsTransparency нужен, чтобы Opacity делал окно прозрачным (без него карта лишь темнеет на чёрном фоне окна)
         ShowInTaskbar = true;
        Icon = AppIcon.Source; Title = "Rust Companion — карта"; Width = 760; Height = 780; MinWidth = 240; MinHeight = 240; Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x1F));
        foreach (var c in new CheckBox[] { _grid, _team, _events, _mons, _notes, _look, _follow, _top })
        { _bar.Children.Add(c); c.Click += (_, _) => { if (ReferenceEquals(c, _top)) Topmost = _top.IsChecked == true; else Refresh(); }; }
        _bar.Children.Add(_source); _bar.Children.Add(_margin);
        _bar.Children.Add(_layersBtn); _bar.Children.Add(_layerOpacity); _bar.Children.Add(_layerInset);
        _layersBtn.Click += async (_, _) => await ShowLayersMenuAsync();
        _layerOpacity.ValueChanged += (_, _) => PositionLayers(); _layerInset.ValueChanged += (_, _) => PositionLayers();
        _bar.Children.Add(_zoom); _bar.Children.Add(_opacity); _bar.Children.Add(_status);
        _opacity.ValueChanged += (_, _) => Opacity = _opacity.Value;
        _zoom.ValueChanged += (_, _) => Refresh();
        _barBorder.Child = _bar; _barBorder.Margin = new Thickness(0, 0, 36, 0);

        _stage.Children.Add(_img); _stage.Children.Add(_layer);
        _stage.HorizontalAlignment = HorizontalAlignment.Left; _stage.VerticalAlignment = VerticalAlignment.Top; _stage.RenderTransformOrigin = new Point(0, 0);
        _view.Background = Brushes.Transparent;               // чтобы колесо и перетаскивание ловились и на пустом фоне
        _view.MouseWheel += OnWheel; _view.MouseLeftButtonDown += OnDown; _view.MouseMove += OnMove; _view.MouseLeftButtonUp += OnUp;
        _view.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { _auto = true; Refresh(); } };
        var tg = new TransformGroup(); tg.Children.Add(_scale); tg.Children.Add(_move); _stage.RenderTransform = tg;
        _view.Children.Add(_stage); _view.Children.Add(_labels); _view.SizeChanged += (_, _) => Refresh();

        var root = new Grid(); root.Children.Add(_view); root.Children.Add(_barBorder); root.Children.Add(_gear);
        _gear.Click += (_, _) => ToggleBar();
        Content = root;

        var menu = new ContextMenu();                       // оформление — из Theme.xaml (тёмный фон, светлый текст, подсветка при наведении)
        MenuItem Item(string text, Action act) { var m = new MenuItem { Header = text }; m.Click += (_, _) => act(); return m; }
        menu.Items.Add(Item("Панель настроек: показать / скрыть", ToggleBar));
        menu.Items.Add(Item("Вписать карту в окно", () => { _auto = true; Refresh(); }));
        menu.Items.Add(Item("Свернуть", () => WindowState = WindowState.Minimized));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Закрыть", Close));
        ContextMenu = menu;
        // Перетаскивание окна: левая кнопка на панели настроек или Shift + левая кнопка в любом месте; левая кнопка на карте двигает карту.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && (Keyboard.Modifiers == ModifierKeys.Shift || IsInside(e.OriginalSource as DependencyObject, _barBorder)) && e.OriginalSource is not (Slider or CheckBox or Button)) { try { DragMove(); } catch (InvalidOperationException) { } } };

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static bool IsInside(DependencyObject? o, DependencyObject parent)
    { while (o is not null) { if (ReferenceEquals(o, parent)) return true; o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o); } return false; }

    private void ToggleBar() => _barBorder.Visibility = _barBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async Task RefreshAsync()
    {
        if (_busy) return; _busy = true;
        try
        {
            if (!_mgr.IsConnected) { _status.Text = "Нет соединения с Rust+."; return; }
            var key = _mgr.Server?.Id ?? 0;
            if (_map is null || _mapKey != key)
            {
                var m = await _mgr.GetMapAsync(CancellationToken.None);
                if (!m.Ok || m.Value is null) { _status.Text = "Карта: " + (m.Message ?? m.Code); return; }
                var raw = m.Value; _mapKey = key;
                var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(raw.Jpg); bmp.EndInit(); bmp.Freeze();
                // размеры берутся у самой картинки: числа из ответа сервера могут не совпадать с её пикселями
                var k = raw.Width > 0 ? (double)bmp.PixelWidth / raw.Width : 1.0;
                _map = raw with { Width = bmp.PixelWidth, Height = bmp.PixelHeight, OceanMargin = (int)Math.Round(raw.OceanMargin * k) };
                _img.Source = bmp; _img.Width = _map.Width; _img.Height = _map.Height; _layer.Width = _map.Width; _layer.Height = _map.Height; _stage.Width = _map.Width; _stage.Height = _map.Height;
                _auto = true;
                _plusMap = _map; _plusBmp = bmp; _rmKey = null; _rmBmp = null; ClearLayers(); _rmInfo = null; _rmInfoKey = null; _rmRatio = null;
                if (_source.SelectedIndex > 0) await ApplySourceAsync();
            }
            await _mgr.GetTeamFreshAsync(CancellationToken.None);
            var r = await _mgr.GetMapMarkersAsync(CancellationToken.None);
            if (r.Ok && r.Value is not null) { _lastMarkers = r.Value; var pl = r.Value.Where(k => k.Kind == RpMarkerKind.Player).ToList();
                _status.Text = $"Обновлено {DateTime.Now:HH:mm:ss}" + (pl.Count == 0 ? "" : pl.Any(k => k.Rotation is not null) ? $"; направление получено у {pl.Count(k => k.Rotation is not null)} из {pl.Count}" : "; сервер не передаёт направление игроков"); }
            else _status.Text = "Маркеры: " + (r.Message ?? r.Code);
            Refresh();
        }
        catch (Exception ex) { _status.Text = "Ошибка карты: " + ex.Message; }
        finally { _busy = false; }
    }

    /// <summary>Подставляет картинку фона. Данные Rust+ рисуются в мировых координатах, поэтому ложатся на любую карту того же сервера при верном поле океана.</summary>
    private void SetBackdrop(BitmapImage bmp, int oceanMargin)
    {
        if (_plusMap is null) return;
        _map = _plusMap with { Width = bmp.PixelWidth, Height = bmp.PixelHeight, OceanMargin = oceanMargin };
        _img.Source = bmp; _img.Width = _map.Width; _img.Height = _map.Height; _layer.Width = _map.Width; _layer.Height = _map.Height; _stage.Width = _map.Width; _stage.Height = _map.Height;
        PositionLayers();
        Refresh();
    }

    private void ClearLayers() { foreach (var i in _layerImg.Values) _stage.Children.Remove(i); _layerImg.Clear(); _layerInset.Visibility = Visibility.Collapsed; }

    /// <summary>Слои RustMaps лежат между картинкой и данными Rust+ и занимают тот же прямоугольник, что и карта (с отступом по ползунку).</summary>
    private void PositionLayers()
    {
        var map = _map; if (map is null) return;
        // слой — картинка того же мира с собственным полем океана r: её полный размер = размер мира на сцене / (1 − 2r), а мир лежит внутри неё со сдвигом r
        var r = Math.Min(_layerInset.Value, 0.49); var worldW = map.Width - 2.0 * map.OceanMargin; var worldH = map.Height - 2.0 * map.OceanMargin;
        var fullW = worldW / (1 - 2 * r); var fullH = worldH / (1 - 2 * r);
        foreach (var i in _layerImg.Values) { i.Width = fullW; i.Height = fullH; i.Opacity = _layerOpacity.Value; Canvas.SetLeft(i, map.OceanMargin - r * fullW); Canvas.SetTop(i, map.OceanMargin - r * fullH); }
    }

    /// <summary>Доля поля океана у картинок RustMaps: берётся из автосовмещения (по основной картинке карты), иначе как у карты Rust+.</summary>
    private async Task<double> EnsureRatioAsync()
    {
        if (_rmRatio is { } known) return known;
        var plus = _plusMap; var plusBmp = _plusBmp; var fallback = plus is { Width: > 0 } ? (double)plus.OceanMargin / plus.Width : 0;
        var url = _rmInfo?.ImageUrl ?? _rmInfo?.RawImageUrl;
        if (plus is null || plusBmp is null || string.IsNullOrEmpty(url)) return fallback;
        try
        {
            var bytes = await _rustMaps.DownloadAsync(url!, CancellationToken.None); if (bytes is null) return fallback;
            var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = new MemoryStream(bytes); b.EndInit(); b.Freeze();
            var margin = plus.OceanMargin;
            var cal = await Task.Run(() => MapCalibrator.FindMarginRatio(plusBmp, margin, b));
            if (cal.Score >= 0.5) { _rmRatio = cal.Ratio; return cal.Ratio; }
        }
        catch (Exception) { }
        return fallback;
    }

    private async Task ShowLayersMenuAsync()
    {
        try
        {
            var info = _mgr.Snapshot.Info;
            if (info?.MapSize is not > 0) { _status.Text = "Слои RustMaps: сервер не сообщил размер карты."; return; }
            var key = $"{info.MapSize}_{info.Seed}_{info.Name}";
            string? viewer = _rmViewer;
            if (_rmInfoKey != key)
            {
                _status.Text = "RustMaps: запрос слоёв…";
                var lk = await _rustMaps.LookupAsync(_mgr.Server?.Id ?? 0, info.Name ?? _mgr.Server?.Name, info.MapSize.Value, info.Seed ?? -1, string.Equals(info.Map, "Custom Map", StringComparison.OrdinalIgnoreCase), CancellationToken.None);
                _rmViewer = viewer = lk.ViewerUrl;
                if (!lk.Result.Ok || lk.Result.Info is null) { _status.Text = "RustMaps: " + lk.Result.Message; if (viewer is null) return; _rmInfo = null; _rmInfoKey = null; }
                else { _rmInfo = lk.Result.Info; _rmInfoKey = key; ClearLayers(); _status.Text = "RustMaps: слоёв найдено — " + (_rmInfo.Layers?.Count ?? 0); }
            }
            var menu = new ContextMenu { PlacementTarget = _layersBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var captured = await _rustMaps.Layers.GetAsync(_mgr.Server?.Id ?? 0);
            var layers = (_rmInfo?.Layers ?? Array.Empty<RustMapsLayer>()).Concat(captured).GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (layers.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "Слоёв пока нет: API RustMaps их не отдал", IsEnabled = false });
                menu.Items.Add(new MenuItem { Header = "Откройте окно RustMaps → «Интерактивная» и включите на сайте нужные слои —", IsEnabled = false });
                menu.Items.Add(new MenuItem { Header = "приложение запомнит их картинки, и они появятся здесь", IsEnabled = false });
            }
            foreach (var l in layers)
            {
                var layer = l; var mi = new MenuItem { Header = layer.Title, IsCheckable = true, IsChecked = _layerImg.ContainsKey(layer.Key) };
                mi.Click += async (_, _) => await ToggleLayerAsync(layer, mi.IsChecked);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var web = new MenuItem { Header = "Открыть интерактивную карту RustMaps (3D, ресурсы, животные, зоны)" };
            web.IsEnabled = viewer is not null;
            web.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(viewer!) { UseShellExecute = true }); } catch (Exception) { } };
            menu.Items.Add(web);
            menu.IsOpen = true;
        }
        catch (Exception ex) { _status.Text = "RustMaps: " + ex.Message; }
    }

    private async Task ToggleLayerAsync(RustMapsLayer layer, bool on)
    {
        try
        {
            if (!on) { if (_layerImg.Remove(layer.Key, out var old)) _stage.Children.Remove(old); if (_layerImg.Count == 0) _layerInset.Visibility = Visibility.Collapsed; return; }
            if (!_layerBmp.TryGetValue(layer.Key, out var bmp))
            {
                var bytes = await _rustMaps.DownloadAsync(layer.Url, CancellationToken.None);
                if (bytes is null) { _status.Text = "Слой не скачан: " + layer.Title; return; }
                try { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = new MemoryStream(bytes); b.EndInit(); b.Freeze(); bmp = b; _layerBmp[layer.Key] = b; }
                catch (Exception) { _status.Text = "Формат слоя не поддерживается: " + layer.Title; return; }
            }
            if (_layerImg.Count == 0) _layerInset.Value = Math.Min(_layerInset.Maximum, await EnsureRatioAsync());
            var img = new Image { Source = bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
            _layerImg[layer.Key] = img; _stage.Children.Insert(1, img); _layerInset.Visibility = Visibility.Visible; PositionLayers();
            _status.Text = "Слой включён: " + layer.Title;
        }
        catch (Exception ex) { _status.Text = "Слой: " + ex.Message; }
    }

    private async Task ApplySourceAsync()
    {
        if (_sourceBusy || _plusMap is null || _plusBmp is null) return;
        _sourceBusy = true;
        try
        {
            if (_source.SelectedIndex <= 0) { _margin.Visibility = Visibility.Collapsed; SetBackdrop(_plusBmp, _plusMap.OceanMargin); return; }
            var info = _mgr.Snapshot.Info;
            if (info?.MapSize is not > 0) { _status.Text = "RustMaps: сервер не сообщил размер карты."; _source.SelectedIndex = 0; return; }
            var icons = _source.SelectedIndex == 2; var key = $"{info.MapSize}_{info.Seed}_{info.Name}_{icons}";
            if (_rmKey != key)
            {
                _status.Text = "RustMaps: загрузка карты…";
                var lk = await _rustMaps.LookupAsync(_mgr.Server?.Id ?? 0, info.Name ?? _mgr.Server?.Name, info.MapSize.Value, info.Seed ?? -1, string.Equals(info.Map, "Custom Map", StringComparison.OrdinalIgnoreCase), CancellationToken.None);
                var r = lk.Result;
                if (!r.Ok || r.Info is null) { _status.Text = "RustMaps: " + r.Message; _source.SelectedIndex = 0; return; }
                var url = icons ? r.Info.ImageIconUrl ?? r.Info.ImageUrl : r.Info.ImageUrl ?? r.Info.RawImageUrl;
                var bytes = string.IsNullOrEmpty(url) ? null : await _rustMaps.DownloadAsync(url!, CancellationToken.None);
                if (bytes is null) { _status.Text = "RustMaps: не удалось скачать картинку карты."; _source.SelectedIndex = 0; return; }
                try { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = new MemoryStream(bytes); b.EndInit(); b.Freeze(); _rmBmp = b; _rmKey = key; }
                catch (Exception) { _status.Text = "RustMaps: формат картинки не поддерживается."; _source.SelectedIndex = 0; return; }
                var plusBmp = _plusBmp; var plusMargin = _plusMap.OceanMargin; var rmBmp = _rmBmp!;
                var cal = await Task.Run(() => MapCalibrator.FindMarginRatio(plusBmp, plusMargin, rmBmp));
                var ok = cal.Score >= 0.5; _rmRatio = ok ? cal.Ratio : null;
                _margin.Value = ok ? cal.Ratio : (_plusMap.Width > 0 ? Math.Min(_margin.Maximum, (double)_plusMap.OceanMargin / _plusMap.Width) : 0);
                _status.Text = ok ? $"RustMaps: карта совмещена автоматически (совпадение суши {cal.Score:P0}). Если есть сдвиг — поправьте ползунком."
                                  : "RustMaps: автосовмещение не удалось, подберите поле океана ползунком (ориентир — монументы).";
            }
            _margin.Visibility = Visibility.Visible;
            SetBackdrop(_rmBmp!, (int)Math.Round(_margin.Value * _rmBmp!.PixelWidth));
        }
        catch (Exception ex) { _status.Text = "RustMaps: " + ex.Message; }
        finally { _sourceBusy = false; }
    }

    /// <summary>Пересчёт масштаба/сдвига и перерисовка слоя (размеры значков зависят от масштаба).</summary>
    private void Refresh() { ApplyView(); Redraw(); }

    private double FitScale(RpMap map) => Math.Min(_view.ActualWidth / map.Width, _view.ActualHeight / map.Height);

    private void ApplyView()
    {
        var map = _map; if (map is null || map.Width <= 0 || _view.ActualWidth <= 0 || _view.ActualHeight <= 0) return;
        var fit = FitScale(map);
        if (_follow.IsChecked == true && _me is { } me)
        {
            var s = fit * _zoom.Value; _scale.ScaleX = _scale.ScaleY = s;
            _move.X = _view.ActualWidth / 2 - me.X * s; _move.Y = _view.ActualHeight / 2 - me.Y * s;
        }
        else if (_auto)
        {
            _scale.ScaleX = _scale.ScaleY = fit;
            _move.X = (_view.ActualWidth - map.Width * fit) / 2; _move.Y = (_view.ActualHeight - map.Height * fit) / 2;
        }
    }

    /// <summary>Колесо мыши: приближение к точке под курсором (в режиме «Центр на мне» меняется масштаб вокруг игрока).</summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var map = _map; if (map is null) return;
        var fit = FitScale(map); var f = Math.Pow(1.2, e.Delta / 120.0);
        if (_follow.IsChecked == true) { _zoom.Value = Math.Clamp(_zoom.Value * f, _zoom.Minimum, _zoom.Maximum); return; }
        var s = Scale; var ns = Math.Clamp(s * f, fit * 0.8, fit * 25);
        var p = e.GetPosition(_view);
        _move.X = p.X - (p.X - _move.X) * (ns / s); _move.Y = p.Y - (p.Y - _move.Y) * (ns / s);
        _scale.ScaleX = _scale.ScaleY = ns; _auto = false; Redraw(); e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e) { if (_follow.IsChecked == true || Keyboard.Modifiers == ModifierKeys.Shift) return; _dragFrom = e.GetPosition(_view); _dragging = true; _view.CaptureMouse(); }
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragFrom is not { } from) return;
        var p = e.GetPosition(_view); _move.X += p.X - from.X; _move.Y += p.Y - from.Y; _dragFrom = p; _auto = false; Redraw();
    }
    private void OnUp(object sender, MouseButtonEventArgs e) { _dragging = false; _dragFrom = null; _view.ReleaseMouseCapture(); }

    private void Redraw()
    {
        _layer.Children.Clear(); _labels.Children.Clear();
        var map = _map; if (map is null || map.MapSize <= 0) return;
        double U(double screenPx) => screenPx / Scale;           // экранные пиксели → единицы слоя: значки не растут при приближении карты

        var team = _mgr.Snapshot.Team?.Members;
        var self = _mgr.Server?.PlayerId;
        var meMember = team?.FirstOrDefault(t => self is not null && t.SteamId == self && t.X is not null && t.Y is not null);
        var prevMe = _me; _me = meMember is null ? null : RpMapGeometry.ToPixel(meMember.X!.Value, meMember.Y!.Value, map);
        if (_follow.IsChecked == true && prevMe != _me) { ApplyView(); }
        if (_follow.IsChecked == true && _me is null) _status.Text = "Ваша позиция не получена: нужно быть в команде и в игре.";

        if (_grid.IsChecked == true) DrawGrid(map, U);
        if (_mons.IsChecked == true) foreach (var m in map.Monuments) { var (x, y) = RpMapGeometry.ToPixel(m.X, m.Y, map); Text(m.Name, x, y, Brushes.White, U(10), false, false); }

        if (_events.IsChecked == true)
            foreach (var k in _lastMarkers.Where(k => RpMapGeometry.IsEvent(k.Kind)))
            {
                var (x, y) = RpMapGeometry.ToPixel(k.X, k.Y, map);
                if (k.Kind == RpMarkerKind.Radius && k.Radius is > 0)
                {
                    var rp = k.Radius.Value * (map.Width - 2.0 * map.OceanMargin) / map.MapSize;
                    var e = new Ellipse { Width = rp * 2, Height = rp * 2, Stroke = Brushes.OrangeRed, StrokeThickness = U(1.5), Fill = new SolidColorBrush(Color.FromArgb(40, 255, 69, 0)) };
                    Canvas.SetLeft(e, x - rp); Canvas.SetTop(e, y - rp); _layer.Children.Add(e);
                }
                Dot(x, y, U(9), Brushes.Violet, RpMapGeometry.Label(k), U(11));
            }

        if (_notes.IsChecked == true && _mgr.Snapshot.Team?.Notes is { } tn)
            foreach (var n in tn)
            {
                var (x, y) = RpMapGeometry.ToPixel(n.X, n.Y, map);
                if (n.IsDeath) Dot(x, y, U(11), new SolidColorBrush(Color.FromRgb(0xE5, 0x30, 0x30)), "Место смерти", U(11));
                else Dot(x, y, U(10), new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)), string.IsNullOrWhiteSpace(n.Label) ? "Метка" : n.Label!, U(11));
            }

        var looking = 0;
        if (_team.IsChecked == true && team is not null)
            foreach (var t in team.Where(t => t.X is not null && t.Y is not null))
            {
                var (x, y) = RpMapGeometry.ToPixel(t.X!.Value, t.Y!.Value, map);
                var brush = t.Alive == false ? new SolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x4D)) : t.Online == false ? new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA0)) : new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                Dot(x, y, U(10), brush, t.Name, U(11), labelBelow: true);
                if (_look.IsChecked == true && _lastMarkers.FirstOrDefault(k => k.Kind == RpMarkerKind.Player && k.SteamId == t.SteamId && k.Rotation is not null)?.Rotation is { } rot)
                {
                    var a = U(16);       // стрелка направления взгляда: Rust+ даёт угол в градусах, значок официального приложения повёрнут на −Rotation (Y вниз)
                    var arrow = new Polygon { Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = a / 14, IsHitTestVisible = false,
                        Points = new PointCollection { new Point(0, -a * 0.9), new Point(a * 0.45, a * 0.3), new Point(0, 0), new Point(-a * 0.45, a * 0.3) },
                        RenderTransform = new RotateTransform(-rot) };
                    Canvas.SetLeft(arrow, x); Canvas.SetTop(arrow, y); _layer.Children.Add(arrow);
                    looking++;
                }
            }
    }

    private void Dot(double x, double y, double size, Brush fill, string label, double fontSize, bool labelBelow = false)
    {
        var s = new Ellipse { Width = size, Height = size, Fill = fill, Stroke = Brushes.Black, StrokeThickness = Math.Max(0.1, size / 8) };
        Canvas.SetLeft(s, x - size / 2); Canvas.SetTop(s, y - size / 2); _layer.Children.Add(s);
        if (string.IsNullOrEmpty(label)) return;
        if (labelBelow) Text(label, x, y + size / 2 + fontSize * 0.1, Brushes.White, fontSize, true, true);
        else Text(label, x + size / 2 + fontSize * 0.2, y - fontSize * 0.7, Brushes.White, fontSize, true, false);
    }

    /// <summary>Подпись в координатах слоя карты (x, y, size — в единицах слоя): переводится в экранные пиксели и рисуется поверх без масштабирования.</summary>
    private void Text(string text, double x, double y, Brush fg, double size, bool shadow, bool centered)
    {
        var sc = Scale;
        var t = new TextBlock
        {
            Text = text, Foreground = fg, FontSize = Math.Max(10, size * sc), FontWeight = FontWeights.SemiBold, Padding = new Thickness(2, 0, 2, 0),
            Background = new SolidColorBrush(Color.FromArgb((byte)(shadow ? 190 : 120), 0, 0, 0))      // тёмная подложка: текст читается на любой местности
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display); TextOptions.SetTextRenderingMode(t, TextRenderingMode.ClearType);
        var left = x * sc + _move.X; var top = y * sc + _move.Y;
        if (centered) { t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); left -= t.DesiredSize.Width / 2; }
        Canvas.SetLeft(t, left); Canvas.SetTop(t, top); _labels.Children.Add(t);
    }

    private void DrawGrid(RpMap map, Func<double, double> U)
    {
        var cells = (int)Math.Ceiling(map.MapSize / RpGrid.CellSize);
        var line = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        var (x0, y0) = RpMapGeometry.ToPixel(0, 0, map); var (x1, y1) = RpMapGeometry.ToPixel(map.MapSize, map.MapSize, map);
        for (var i = 0; i <= cells; i++)
        {
            var wx = Math.Min(i * RpGrid.CellSize, map.MapSize);
            var px = RpMapGeometry.ToPixel(wx, 0, map).X;
            var py = RpMapGeometry.ToPixel(0, map.MapSize - wx, map).Y;      // строки сверху вниз
            _layer.Children.Add(new Line { X1 = px, Y1 = y1, X2 = px, Y2 = y0, Stroke = line, StrokeThickness = U(0.8) });
            _layer.Children.Add(new Line { X1 = x0, Y1 = py, X2 = x1, Y2 = py, Stroke = line, StrokeThickness = U(0.8) });
            if (i < cells)
            {
                var cx = RpMapGeometry.ToPixel((i + 0.5) * RpGrid.CellSize, 0, map).X;
                Text(RpGrid.Letters(i), cx, y1 + U(1), Brushes.White, U(11), false, true);
                var cy = RpMapGeometry.ToPixel(0, map.MapSize - (i + 0.5) * RpGrid.CellSize, map).Y;
                Text(i.ToString(), x0 + U(2), cy - U(7), Brushes.White, U(11), false, false);
            }
        }
    }
}
