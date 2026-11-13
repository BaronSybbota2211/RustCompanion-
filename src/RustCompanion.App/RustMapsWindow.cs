using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.App;

/// <summary>
/// Карта сервера с rustmaps.com для сервера, к которому подключён Rust+ (размер и seed приходят из getInfo).
/// Вкладка «Интерактивная» — страница rustmaps.com/map/{размер}_{seed} в WebView2: 3D-вид, монументы, зоны, биомы, тепловые карты (работает без ключа).
/// Вкладки «Карта» и «Монументы» строятся из ответа API (нужен ключ RustMaps API из настроек).
/// </summary>
public sealed class RustMapsWindow : Window
{
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustCompanion", "webview2");
    private readonly RustPlusManager _mgr; private readonly RustMapsClient _client;
    private readonly WebView2 _web = new();
    private readonly Image _img = new() { Stretch = Stretch.Uniform };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Slider _zoom = new() { Minimum = 0.5, Maximum = 4, Value = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _icons = new() { Content = "Иконки монументов", IsChecked = true, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gainsboro };
    private readonly ListView _list = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
    private readonly TabControl _tabs = new();
    private readonly Button _remember = new() { Content = "Запомнить эту карту", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, ToolTip = "Откройте на сайте карту своего сервера (Server Search или поиск по названию) и нажмите, чтобы приложение использовало её для этого сервера. Действует до вайпа (пока не изменятся размер и seed)" };
    private readonly Button _forget = new() { Content = "Забыть привязку", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
    private string? _webUrl, _siteTried;
    private RustMapsInfo? _info; private int _size; private long _seed; private string? _viewerUrl;
    private CancellationTokenSource? _cts; private bool _webReady;

    public RustMapsWindow(RustPlusManager mgr, RustMapsClient client)
    {
        _mgr = mgr; _client = client;
        Icon = AppIcon.Source; Title = "Карта RustMaps"; Background = (Brush?)Application.Current.TryFindResource("Bg") ?? new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x1F)); Foreground = (Brush?)Application.Current.TryFindResource("Text") ?? Brushes.Gainsboro; Width = 1200; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        bar.Children.Add(Btn("Обновить", async () => await LoadAsync(true)));
        bar.Children.Add(Btn("Открыть в браузере", () => { if (_viewerUrl is not null) Open(_viewerUrl); }));
        bar.Children.Add(Btn("Получить ключ API", () => Open(RustMapsClient.DashboardUrl)));
        _remember.Click += async (_, _) => await RememberAsync(); _forget.Click += async (_, _) => { if (_mgr.Server is { } sv) { await _client.Bindings.ClearAsync(sv.Id); await LoadAsync(); } };
        bar.Children.Add(_remember); bar.Children.Add(_forget);
        bar.Children.Add(_status);

        _img.LayoutTransform = _scale;
        _zoom.ValueChanged += (_, _) => { _scale.ScaleX = _scale.ScaleY = _zoom.Value; };
        _icons.Checked += async (_, _) => await ShowImageAsync(); _icons.Unchecked += async (_, _) => await ShowImageAsync();
        var mapBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        mapBar.Children.Add(new TextBlock { Text = "Масштаб", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        mapBar.Children.Add(_zoom); mapBar.Children.Add(_icons);
        var mapDock = new DockPanel();
        DockPanel.SetDock(mapBar, Dock.Top); mapDock.Children.Add(mapBar);
        mapDock.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _img });

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Монумент", Width = 320, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        grid.Columns.Add(new GridViewColumn { Header = "X, м", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("X") { StringFormat = "0" } });
        grid.Columns.Add(new GridViewColumn { Header = "Y, м", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Y") { StringFormat = "0" } });
        grid.Columns.Add(new GridViewColumn { Header = "Квадрат", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Cell") });
        _list.View = grid;

        _tabs.Items.Add(new TabItem { Header = "Интерактивная (3D, монументы, зоны)", Content = _web });
        _tabs.Items.Add(new TabItem { Header = "Карта", Content = mapDock });
        _tabs.Items.Add(new TabItem { Header = "Монументы", Content = _list });
        var details = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _hint };
        _tabs.Items.Add(new TabItem { Header = "Данные карты", Content = details });

        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); root.Children.Add(_tabs); Content = root;
        Loaded += async (_, _) => { await InitWebAsync(); await LoadAsync(); };
        Closed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); try { _web.Dispose(); } catch (Exception) { } };
    }

    private static Button Btn(string text, Action a) { var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) }; b.Click += (_, _) => a(); return b; }
    private static Button Btn(string text, Func<Task> a) { var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) }; b.Click += async (_, _) => await a(); return b; }
    private static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } }

    private async Task InitWebAsync()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; _web.CoreWebView2.Navigate(e.Uri); };
            _web.CoreWebView2.SourceChanged += (_, _) =>
            {
                _webUrl = _web.Source?.ToString();
                _remember.IsEnabled = Uri.TryCreate(_webUrl, UriKind.Absolute, out var u) && u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)
                    && u.AbsolutePath.StartsWith("/map/", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.Length > 5;
            };
            _web.CoreWebView2.WebResourceResponseReceived += (_, e) => OnWebResponse(e);
            _webReady = true;
        }
        catch (WebView2RuntimeNotFoundException) { _status.Text = "Не найден Microsoft Edge WebView2 Runtime: https://go.microsoft.com/fwlink/p/?LinkId=2124703"; }
        catch (Exception ex) { _status.Text = "Не удалось запустить встроенный браузер: " + ex.Message; }
    }

    private async Task LoadAsync(bool force = false)
    {
        _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
        var srv = _mgr.Server; var info = _mgr.Snapshot.Info;
        if (srv is null || info?.MapSize is not > 0)
        {
            _status.Text = "Подключитесь к серверу на вкладке Rust+: размер и seed карты берутся у подключённого сервера.";
            _hint.Text = _status.Text; return;
        }
        _size = info.MapSize.Value; _seed = info.Seed ?? -1;
        var name = info.Name ?? srv.Name;
        Title = $"Карта RustMaps — {name} ({_size}, seed {(info.Seed?.ToString() ?? "?")})";
        var custom = string.Equals(info.Map, "Custom Map", StringComparison.OrdinalIgnoreCase);
        _status.Text = "Ищу карту сервера на RustMaps (по размеру и seed, затем по названию)…";
        var lk = await _client.LookupAsync(srv.Id, name, _size, _seed, custom, ct);
        if (ct.IsCancellationRequested) return;
        _viewerUrl = lk.ViewerUrl;
        string? siteReport = null;
        var siteKey = $"{srv.Id}_{_size}_{_seed}_{name}";
        if (lk.Result.Status == RustMapsStatus.Error && !lk.FromBinding && _webReady && (force || _siteTried != siteKey))
        {
            _siteTried = siteKey; _tabs.SelectedIndex = 0;
            _status.Text = "Ищу сервер через поиск на сайте RustMaps (название, затем IP)…";
            var (url, rep) = await new RustMapsSiteSearch(_web).FindAsync(new[] { name, name.Split('|')[0].Trim(), srv.Ip }, name, ct);
            if (ct.IsCancellationRequested) return;
            if (url is not null)
            {
                await _client.Bindings.SetAsync(srv.Id, new RustMapsBinding(url, _size, _seed));
                await LoadAsync(); return;
            }
            siteReport = rep;
        }
        if (_webReady) _web.CoreWebView2.Navigate(_viewerUrl ?? "https://rustmaps.com/");
        var r = lk.Result;
        if (!r.Ok)
        {
            _info = null; _status.Text = r.Message.Split('\n')[0]; _hint.Text = r.Message + (siteReport is null ? "" : "\n\nПоиск на сайте RustMaps:\n" + siteReport) + "\n\nИнтерактивная карта на первой вкладке работает и без ключа. Если сервер не найден автоматически, найдите его на сайте и нажмите «Запомнить эту карту».";
            _list.ItemsSource = null; _img.Source = null; return;
        }
        _info = r.Info; _status.Text = $"Готово: {_info!.Monuments.Count} монументов.";
        if (!string.IsNullOrEmpty(_info!.PageUrl) && _webReady) { _viewerUrl = _info.PageUrl; _web.CoreWebView2.Navigate(_viewerUrl); }
        var mapSize = _size;
        _list.ItemsSource = _info.Monuments.OrderBy(m => m.Name).Select(m => new MonumentRow(m.Name, m.X, m.Y, Cell(m, mapSize))).ToList();
        _hint.Text = string.Join("\n", new[] { $"Размер: {_info.Size}", $"Seed: {_info.Seed}", _info.IsStaging ? "Ветка: staging" : "Ветка: release", _info.IsCustom ? "Кастомная карта" : "Процедурная карта" }
            .Concat(_info.Details.Where(d => d.Key is not ("seed" or "size" or "isStaging" or "isCustomMap")).Select(d => $"{d.Key}: {d.Value}"))
            .Concat(new[] { "", $"Слои (ресурсы, животные, зоны): {_info.Layers?.Count ?? 0}" }).Concat((_info.Layers ?? Array.Empty<RustMapsLayer>()).Select(l => "  " + l.Title + " — " + l.Url)));
        await ShowImageAsync();
    }

    /// <summary>
    /// Запоминает картинки, которые страница карты RustMaps сама загружает при включении слоёв (ресурсы, животные, зоны запрета строительства и т. п.),
    /// чтобы потом показать их поверх мини-карты Rust+. Служебные картинки сайта (иконки, логотипы) отбрасываются.
    /// </summary>
    private void OnWebResponse(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri; var srv = _mgr.Server;
            if (srv is null || e.Response.StatusCode != 200 || !Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
                || !u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)) return;
            var ct = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : "";
            if (!ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ct.Contains("svg", StringComparison.OrdinalIgnoreCase)) return;
            var p = u.AbsolutePath;
            if (p.Contains("/assets/", StringComparison.OrdinalIgnoreCase) || p.Contains("favicon", StringComparison.OrdinalIgnoreCase) || p.Contains("logo", StringComparison.OrdinalIgnoreCase)
                || p.Contains("avatar", StringComparison.OrdinalIgnoreCase) || p.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)) return;
            if (_info is { } inf && new[] { inf.ImageUrl, inf.ImageIconUrl, inf.RawImageUrl, inf.ThumbnailUrl }.Any(x => string.Equals(x, uri, StringComparison.OrdinalIgnoreCase))) return;
            var mapPage = Uri.TryCreate(_webUrl, UriKind.Absolute, out var wu) && wu.AbsolutePath.StartsWith("/map/", StringComparison.OrdinalIgnoreCase);
            if (!mapPage) return;                                    // слои собираем только со страницы карты
            var id = srv.Id; var url = uri;
            _ = Task.Run(async () =>
            {
                if (await _client.Layers.AddAsync(id, url))
                    Dispatcher.Invoke(() => _status.Text = "Слой RustMaps запомнен: " + RustMapsLayerStore.KeyFor(url) + " — он доступен в «Слои RustMaps» окна карты Rust+.");
            });
        }
        catch (Exception) { }
    }

    private async Task RememberAsync()
    {
        var srv = _mgr.Server; var info = _mgr.Snapshot.Info;
        if (srv is null || info?.MapSize is not > 0 || _webUrl is null || RustMapsLocator.FromUrl(_webUrl, null, null) is null) return;
        await _client.Bindings.SetAsync(srv.Id, new RustMapsBinding(_webUrl, info.MapSize.Value, info.Seed ?? -1));
        await LoadAsync();
    }

    /// <summary>Квадрат сетки по координатам RustMaps (начало — центр карты); null, если координат нет.</summary>
    private static string? Cell(RustMapsMonument m, int size)
    {
        if (m.X is null || m.Y is null) return null;
        return RpGrid.Cell(m.X + size / 2.0, m.Y + size / 2.0, size);
    }

    private async Task ShowImageAsync()
    {
        var info = _info; if (info is null) return;
        var url = _icons.IsChecked == true ? info.ImageIconUrl ?? info.ImageUrl : info.ImageUrl ?? info.RawImageUrl;
        if (string.IsNullOrEmpty(url)) { _status.Text = "RustMaps не вернул картинку этой карты."; return; }
        var bytes = await _client.DownloadAsync(url, _cts?.Token ?? default);
        if (bytes is null) { _status.Text = "Не удалось скачать картинку карты."; return; }
        try
        {
            var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
            _img.Source = bmp; _img.Width = Math.Min(bmp.PixelWidth, 1600); _img.Height = _img.Width * bmp.PixelHeight / bmp.PixelWidth;
        }
        catch (Exception) { _status.Text = "Формат картинки не поддерживается; смотрите интерактивную карту."; }
    }

    private sealed record MonumentRow(string Name, double? X, double? Y, string? Cell);
}
