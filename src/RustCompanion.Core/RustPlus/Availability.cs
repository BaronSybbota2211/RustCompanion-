using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

public enum AvailabilityTransition { None, BecameUnavailable, Restored }

/// <summary>Чистая логика: N неудачных чтений подряд → «недоступно»; первое удачное чтение после этого → «восстановлено». Без сети и БД.</summary>
public sealed class DeviceAvailabilityTracker
{
    private readonly Dictionary<(long, ulong), (int Fails, bool Unavailable)> _s = new();
    public int Required { get; }
    public DeviceAvailabilityTracker(int required = 3) { Required = Math.Max(1, required); }

    public AvailabilityTransition Record(long serverId, ulong entityId, bool success)
    {
        var key = (serverId, entityId);
        (int Fails, bool Unavailable) cur = _s.TryGetValue(key, out var v) ? v : (0, false);
        if (success)
        {
            _s[key] = (0, false);
            return cur.Unavailable ? AvailabilityTransition.Restored : AvailabilityTransition.None;
        }
        var fails = cur.Fails + 1;
        if (cur.Unavailable) { _s[key] = (fails, true); return AvailabilityTransition.None; }       // уже недоступно — повторного уведомления нет
        if (fails >= Required) { _s[key] = (fails, true); return AvailabilityTransition.BecameUnavailable; }
        _s[key] = (fails, false); return AvailabilityTransition.None;
    }

    public int Failures(long serverId, ulong entityId) => _s.TryGetValue((serverId, entityId), out var v) ? v.Fails : 0;
    public bool IsUnavailable(long serverId, ulong entityId) => _s.TryGetValue((serverId, entityId), out var v) && v.Unavailable;

    /// <summary>Счётчики неудач сбрасываются, а признак «недоступно» остаётся: пока связи с сервером нет, о состоянии устройства ничего не известно.</summary>
    public void ResetFailures(long serverId)
    {
        foreach (var k in _s.Keys.Where(k => k.Item1 == serverId).ToList()) _s[k] = (0, _s[k].Unavailable);
    }

    public void Forget(long serverId, ulong entityId) => _s.Remove((serverId, entityId));
}

/// <summary>
/// Контроль доступности Smart Alarm и Smart Switch: раз в DeviceProbeSeconds (по умолчанию 30 с) читается состояние каждого устройства активного сервера.
/// Три неудачных чтения подряд — «недоступно», восстановление — отдельное уведомление. Проверка идёт только при активном соединении с сервером;
/// если связь с самим сервером потеряна (проверяется отдельным запросом), неудачи НЕ засчитываются устройствам.
/// Причину недоступности приложение определить не может (устройство могло быть уничтожено, обесточено или недоступно по другой причине) и ничего не утверждает.
/// </summary>
public sealed class DeviceAvailabilityService
{
    private readonly RustPlusManager _mgr; private readonly RpDeviceRepository _devices; private readonly RustPlusHub _hub; private readonly IGameNotifier _notifier;
    private readonly ISettingsProvider _settings; private readonly IClock _clock; private readonly DiagnosticsService? _diag;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DeviceAvailabilityTracker _tracker; private int _requiredBuilt;

    public DeviceAvailabilityService(RustPlusManager mgr, RpDeviceRepository devices, RustPlusHub hub, IGameNotifier notifier, ISettingsProvider settings,
        IClock clock, DiagnosticsService? diag = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _mgr = mgr; _devices = devices; _hub = hub; _notifier = notifier; _settings = settings; _clock = clock; _diag = diag;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _requiredBuilt = Math.Max(2, settings.Current.DeviceProbeFailures); _tracker = new DeviceAvailabilityTracker(_requiredBuilt);
    }

    public DeviceAvailabilityTracker Tracker => _tracker;

    public async Task ProbeOnceAsync(CancellationToken ct)
    {
        var required = Math.Max(2, _settings.Current.DeviceProbeFailures);
        if (required != _requiredBuilt) { _requiredBuilt = required; _tracker = new DeviceAvailabilityTracker(required); }

        var srv = _mgr.Server;
        if (!_mgr.IsConnected || srv is null)
        {
            if (srv is not null) { _tracker.ResetFailures(srv.Id); _hub.ResetAvailability(srv.Id, "нет соединения с сервером Rust+ — доступность устройств не проверяется"); }
            return;
        }
        var devices = (await _devices.ListAsync(srv.Id, ct)).Where(d => d.Kind is RpDeviceKind.SmartSwitch or RpDeviceKind.SmartAlarm).ToList();
        foreach (var d in devices)
        {
            ct.ThrowIfCancellationRequested();
            if (!_mgr.IsConnected || _mgr.Server?.Id != srv.Id) { _tracker.ResetFailures(srv.Id); return; }   // сервер сменился или связь потеряна посреди цикла
            var r = await _mgr.ReadDeviceAsync(d.EntityId, d.Kind, ct);
            var ok = r.Ok && r.Value is not null;
            if (!ok)
            {
                if (!_mgr.IsConnected || _mgr.Server?.Id != srv.Id) { _tracker.ResetFailures(srv.Id); return; }
                if (RpErrors.IsAuth(r) || r.Code == "not_connected") { _tracker.ResetFailures(srv.Id); return; }
                var ping = await _mgr.PingAsync(ct);                       // отвечает ли сам сервер?
                if (!ping.Ok)
                {
                    _tracker.ResetFailures(srv.Id);
                    _hub.ResetAvailability(srv.Id, "сервер Rust+ не отвечает — это потеря связи с сервером, а не недоступность устройств");
                    _diag?.Record("Rust+ доступность", SourceState.NoResponse, "сервер не отвечает, сбои устройств не засчитываются");
                    return;
                }
            }
            var t = _tracker.Record(srv.Id, d.EntityId, ok);
            if (ok)
            {
                _hub.SetState(srv.Id, d.EntityId, r.Value!);
                await _devices.MarkSynchronizedAsync(d.Id, r.Value!, _clock.UtcNow, ct);
            }
            _hub.SetAvailability(srv.Id, d.EntityId, ok ? DeviceAvailability.Available : _tracker.IsUnavailable(srv.Id, d.EntityId) ? DeviceAvailability.Unavailable : DeviceAvailability.Unknown,
                ok ? null : $"нет ответа устройства ({_tracker.Failures(srv.Id, d.EntityId)} из {required} проверок подряд)");

            if (t == AvailabilityTransition.BecameUnavailable)
            {
                await _devices.AddEventAsync(d.Id, _clock.UtcNow, "unavailable", $"{required} неудачных чтений подряд", ct);
                await _notifier.NotifyAsync(Build(GameNotifyKind.DeviceUnavailable, d, srv, required), ct);
            }
            else if (t == AvailabilityTransition.Restored)
            {
                await _devices.AddEventAsync(d.Id, _clock.UtcNow, "restored", "", ct);
                await _notifier.NotifyAsync(Build(GameNotifyKind.DeviceRestored, d, srv, required), ct);
            }
        }
    }

    public static GameNotification Build(string kind, RpDevice d, PairedServer srv, int failures)
    {
        var what = d.Kind == RpDeviceKind.SmartAlarm ? "Smart Alarm" : "Smart Switch";
        if (kind == GameNotifyKind.DeviceUnavailable)
            return new GameNotification(kind,
                $"УСТРОЙСТВО НЕДОСТУПНО! {what}: {d.Name}. Сервер: {srv.Name}. Нет ответа {failures} проверки подряд.",
                $"УСТРОЙСТВО НЕДОСТУПНО\n{what}: {d.Name}.\nСервер: {srv.Name}\nНет ответа на {failures} проверки подряд; причина не определена.", srv.Id);
        return new GameNotification(kind,
            $"Устройство снова доступно: {what} {d.Name}. Сервер: {srv.Name}.",
            $"Устройство снова доступно\n{what}: {d.Name}.\nСервер: {srv.Name}", srv.Id);
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ProbeOnceAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _diag?.Record("Rust+ доступность", SourceState.Error, ex.GetType().Name); }
                await _delay(TimeSpan.FromSeconds(Math.Max(10, _settings.Current.DeviceProbeSeconds)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
