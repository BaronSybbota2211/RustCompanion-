using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustCompanion.RustPlusAdapter;

namespace RustCompanion.App.Services;

/// <summary>
/// Вход через Steam во встроенном окне (WebView2). Facepunch завершает вход редиректом на rust-plus://rust-auth-session/?token=…&amp;steamId=…;
/// окно перехватывает этот редирект, ссылка никуда не передаётся и не требует регистрации протокола в Windows.
/// </summary>
public sealed class SteamLoginService : ISteamLoginProvider
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public async Task<SteamLoginData> LoginAsync(CancellationToken ct)
    {
        var dispatcher = Application.Current.Dispatcher;
        var window = await dispatcher.InvokeAsync(() => { var w = new SteamLoginWindow(); w.Show(); w.Activate(); return w; });
        using var reg = ct.Register(window.CloseSafe);
        try
        {
            var url = await window.Result.WaitAsync(LoginTimeout, ct);
            return RustAuthLink.Parse(url);
        }
        finally { window.CloseSafe(); }
    }
}

/// <summary>
/// Окно входа Facepunch/Steam на WebView2 (движок Edge, встроен в Windows 10/11). Вход выполняет сам пользователь, Steam Guard и защита не обходятся.
/// Окно нужно потому, что страница /app у Facepunch не делает редирект на rust-plus://, если в User-Agent есть «Windows» (она отвечает «Login Failed»),
/// а в обычном браузере User-Agent не изменить. Здесь отправляется User-Agent не от Windows, а итоговая ссылка rust-plus://… перехватывается и не покидает приложение.
/// </summary>
internal sealed class SteamLoginWindow : Window
{
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private readonly WebView2 _web = new();
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "rc-login-" + Guid.NewGuid().ToString("N"));
    private bool _closed;

    public Task<string> Result => _result.Task;

    public SteamLoginWindow()
    {
        Title = "Rust Companion — вход через Steam";
        Width = 560; Height = 760; MinWidth = 420; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _web;
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => OnClosed();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.UserAgent = UserAgent;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, e) => { if (RustAuthLink.IsAuthLink(e.Uri)) { e.Cancel = true; Done(e.Uri); } };
            core.NewWindowRequested += (_, e) => { if (RustAuthLink.IsAuthLink(e.Uri)) { e.Handled = true; Done(e.Uri); } };
            core.WebResourceResponseReceived += (_, e) =>
            {
                try
                {
                    var status = e.Response.StatusCode;
                    if (status >= 300 && status < 400 && e.Response.Headers.Contains("Location"))
                    {
                        var loc = e.Response.Headers.GetHeader("Location");
                        if (RustAuthLink.IsAuthLink(loc)) Done(loc);
                    }
                }
                catch (Exception) { /* заголовки недоступны — полагаемся на NavigationStarting */ }
            };
            core.Navigate(RustAuthLink.LoginUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _result.TrySetException(new InvalidOperationException("Не найден Microsoft Edge WebView2 Runtime. Установите его (бесплатно): https://go.microsoft.com/fwlink/p/?LinkId=2124703 — и повторите вход."));
        }
        catch (Exception ex) { _result.TrySetException(new InvalidOperationException("Не удалось открыть окно входа: " + ex.Message)); }
    }

    private void Done(string url) => _result.TrySetResult(url);

    /// <summary>Закрывает окно из любого потока; безопасно вызывать повторно.</summary>
    public void CloseSafe()
    {
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) { try { Close(); } catch (Exception) { } } }));
    }

    private void OnClosed()
    {
        _closed = true;
        _result.TrySetException(new InvalidOperationException("Окно входа закрыто до завершения входа. Нажмите «Войти через Steam» ещё раз."));
        try { _web.Dispose(); } catch (Exception) { }
        // Временный профиль браузера (cookie Steam/Facepunch) удаляется, чтобы вход не оставался на диске.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); return; }
                catch (Exception) { await Task.Delay(800); }
            }
        });
    }
}
