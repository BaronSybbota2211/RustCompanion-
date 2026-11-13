using System;
using System.Threading;
using System.Threading.Tasks;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;
using RustCompanion.RustPlusAdapter;

namespace RustCompanion.App.Services;

/// <summary>
/// Единственный слушатель push-уведомлений Rust+ (FCM). Раньше он принадлежал вкладке «Rust+» и событие привязывалось к «текущему» серверу;
/// теперь слушатель не зависит от вкладок: каждое событие передаётся в <see cref="FcmEventRouter"/>, который определяет сервер по адресу из уведомления
/// и записывает результат в общее состояние (обе вкладки обновляются). Статус «запущен» выставляется только после успешного подключения слушателя.
/// </summary>
public sealed class RustPlusFcmService
{
    private readonly RustPlusPairing _pairing; private readonly FcmEventRouter _router; private readonly DiagnosticsService _diag;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAsyncDisposable? _handle;

    public bool IsListening => _handle is not null;
    public string Status { get; private set; } = "Приём push-уведомлений не запущен";
    public event Action? StatusChanged;

    public RustPlusFcmService(RustPlusPairing pairing, FcmEventRouter router, DiagnosticsService diag)
    { _pairing = pairing; _router = router; _diag = diag; }

    private void Set(string text) { Status = text; StatusChanged?.Invoke(); }

    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_handle is not null) return;
            if (!_pairing.HasCredentials) { Set("Нет учётных данных Rust+ — выполните вход через Steam на вкладке «Rust+»."); return; }
            var (res, handle) = await _pairing.StartListeningAsync(n => _ = RouteSafelyAsync(n), CancellationToken.None);
            if (res.Ok && handle is not null) { _handle = handle; Set("Слушатель push-уведомлений Rust+ запущен (сопряжение устройств, срабатывание сигнализаций)."); }
            else { Set("Push-уведомления недоступны: " + (res.Message ?? res.Code)); _diag.Record("Rust+ push", SourceState.Error, $"{res.Code} {res.Message}"); }
        }
        finally { _gate.Release(); }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await EnsureStartedAsync();
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var h = _handle; _handle = null;
            if (h is not null) { try { await h.DisposeAsync(); } catch (Exception) { } }
            Set("Приём push-уведомлений остановлен");
        }
        finally { _gate.Release(); }
    }

    private async Task RouteSafelyAsync(FcmNotification n)
    {
        try { await _router.HandleAsync(n.EventName, n.Props); }
        catch (Exception ex) { _diag.Record("Rust+ push", SourceState.Error, "Ошибка обработки уведомления: " + ex.GetType().Name); }
    }
}
