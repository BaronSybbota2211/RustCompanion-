using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustCompanion.Core.Watch;

namespace RustCompanion.App;

/// <summary>
/// Окно со встроенным браузером (WebView2) для внешних сайтов. Профиль браузера постоянный (папка в %LocalAppData%\RustCompanion\webview2),
/// поэтому вход в BattleMetrics через Steam выполняется один раз и сохраняется между запусками приложения (если сайт выдал постоянные cookies).
/// Пароли и cookies приложением не читаются и не сохраняются: вход выполняет сам пользователь на сайте.
/// </summary>
public sealed class WebToolWindow : Window
{
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustCompanion", "webview2");
    private static readonly string BmFlag = Path.Combine(DataDir, "..", "battlemetrics-login.flag");
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gainsboro };
    private readonly string _startUrl; private readonly string? _afterLoginUrl; private string? _bmSteamId; private bool _bmDone; private readonly string? _fillScript; private bool _loggedPhase; private bool _scriptDone;

    private WebToolWindow(string title, string startUrl, string? afterLoginUrl, string? fillScript, bool withLogout)
    {
        _startUrl = startUrl; _afterLoginUrl = afterLoginUrl; _fillScript = fillScript; _loggedPhase = afterLoginUrl is not null;
        Icon = AppIcon.Source; Title = title; Width = 1100; Height = 800; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        var browser = new Button { Content = "Открыть в браузере", Padding = new Thickness(10, 2, 10, 2) };
        browser.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_web.Source?.ToString() ?? _startUrl) { UseShellExecute = true }); } catch (Exception) { } };
        bar.Children.Add(browser);
        if (withLogout)
        {
            var logout = new Button { Content = "Выйти из BattleMetrics", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            logout.Click += async (_, _) => await LogoutAsync();
            bar.Children.Add(logout);
        }
        bar.Children.Add(_status);
        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); root.Children.Add(_web); Content = root;
        Loaded += async (_, _) => await InitAsync();
    }

    public static bool BattleMetricsLoggedIn { get { try { return File.Exists(BmFlag); } catch (Exception) { return false; } } }
    private static void SetBmFlag(bool on) { try { Directory.CreateDirectory(DataDir); if (on) File.WriteAllText(BmFlag, DateTime.UtcNow.ToString("O")); else if (File.Exists(BmFlag)) File.Delete(BmFlag); } catch (Exception) { } }

    /// <summary>Если вход ещё не выполнен — сначала страница входа BattleMetrics, после успешного входа (сайт ушёл со страницы логина) — переход на профиль. Иначе сразу профиль.</summary>
    public static void OpenBattleMetrics(string profileUrl, string steamId)
    {
        var w = BattleMetricsLoggedIn ? new WebToolWindow("BattleMetrics", "https://www.battlemetrics.com/", profileUrl, null, true)
                                      : new WebToolWindow("BattleMetrics — вход", BattleMetricsLogin.LoginUrl, profileUrl, null, true);
        w._bmSteamId = steamId; w._loggedPhase = !BattleMetricsLoggedIn;
        w.Show(); w.Activate();
    }

    public static void OpenRustBans(string steamId)
    {
        var script = RustBansLink.FillScript(steamId);
        try { Clipboard.SetText(steamId); } catch (Exception) { }
        var w = new WebToolWindow("Статистика игрока — rustbans.ru", RustBansLink.Url, null, script, false);
        w.Show(); w.Activate();
    }

    private async Task InitAsync()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };   // всплывающие окна (вход Steam) открываются в этом же окне
            core.NavigationCompleted += async (_, _) => await OnNavigatedAsync();
            _status.Text = _loggedPhase ? "Войдите через Steam; после входа откроется профиль игрока." : "";
            core.Navigate(_startUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        { _status.Text = "Не найден Microsoft Edge WebView2 Runtime. Установите его: https://go.microsoft.com/fwlink/p/?LinkId=2124703"; }
        catch (Exception ex) { _status.Text = "Не удалось открыть окно: " + ex.Message; }
    }

    private async Task OnNavigatedAsync()
    {
        try
        {
            var url = _web.Source?.ToString();
            if (_loggedPhase && BattleMetricsLogin.IsLoggedInPage(url))
            { _loggedPhase = false; SetBmFlag(true); _status.Text = "Вход выполнен, вход сохранится для следующих запусков."; }
            if (!_loggedPhase && _bmSteamId is not null && !_bmDone && BattleMetricsLogin.IsLoggedInPage(url))
            {
                _bmDone = true; _status.Text = "Ищу игрока в BattleMetrics…";
                var bmId = await LookupBmIdAsync(_bmSteamId);
                if (bmId is not null) { _status.Text = "Игрок найден."; _web.CoreWebView2.Navigate(BattleMetricsLink.PlayerPage(bmId)); }
                else { _status.Text = "BattleMetrics не нашёл игрока по SteamID64 (он мог не играть на серверах, которые отслеживает BattleMetrics). Открыт обычный поиск."; if (_afterLoginUrl is not null) _web.CoreWebView2.Navigate(_afterLoginUrl); }
                return;
            }
            if (_fillScript is not null && !_scriptDone && url is not null && url.StartsWith(RustBansLink.Url, StringComparison.OrdinalIgnoreCase))
            {
                _scriptDone = true;
                var res = await _web.CoreWebView2.ExecuteScriptAsync(_fillScript);
                _status.Text = res.Contains("clicked") || res.Contains("submitted") ? "SteamID подставлен в поиск." : "Не удалось подставить SteamID автоматически: он скопирован в буфер обмена, вставьте его в поле поиска (Ctrl+V).";
            }
        }
        catch (Exception ex) { _status.Text = "Ошибка: " + ex.Message; }
    }

    /// <summary>ID игрока в BattleMetrics по SteamID64 через API сайта; null, если не найден или запрос не удался.</summary>
    private async Task<string?> LookupBmIdAsync(string steamId)
    {
        var expr = BattleMetricsLink.LookupScript(steamId); if (expr is null) return null;
        try
        {
            var args = System.Text.Json.JsonSerializer.Serialize(new { expression = expr, awaitPromise = true, returnByValue = true });
            var json = await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var v = doc.RootElement.GetProperty("result").GetProperty("value").GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (Exception) { return null; }
    }

    private async Task LogoutAsync()
    {
        try
        {
            if (_web.CoreWebView2 is null) return;
            foreach (var c in await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.battlemetrics.com")) _web.CoreWebView2.CookieManager.DeleteCookie(c);
            SetBmFlag(false); _status.Text = "Вы вышли из BattleMetrics. При следующем открытии нужно войти снова.";
        }
        catch (Exception ex) { _status.Text = "Не удалось выйти: " + ex.Message; }
    }
}
