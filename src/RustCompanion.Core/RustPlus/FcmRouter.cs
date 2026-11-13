using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

public sealed record FcmResolution(PairedServer? Server, string How);

/// <summary>
/// Привязка push-уведомления к сопряжённому серверу. Раньше событие приписывалось «подключённому или выбранному» серверу, из-за чего устройства
/// попадали не на тот сервер или терялись. Теперь решает адрес из самого уведомления; запасные варианты применяются только если адреса в уведомлении нет.
/// </summary>
public static class FcmServerResolver
{
    public static FcmResolution Resolve(IReadOnlyList<PairedServer> servers, string? ip, int? port, PairedServer? connected,
        string? serverUid = null, ulong? playerId = null)
    {
        if (servers.Count == 0) return new(null, "нет сопряжённых серверов");
        var candidates = playerId is null ? servers.ToList() : servers.Where(s => s.PlayerId == playerId.Value).ToList();
        if (candidates.Count == 0) return new(null, "Player ID уведомления не совпадает ни с одним сопряжённым сервером");

        if (!string.IsNullOrWhiteSpace(serverUid))
        {
            var exactUid = candidates.Where(s => string.Equals(s.RustPlusId, serverUid.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return exactUid.Count == 1
                ? new(exactUid[0], "по Rust+ Server ID из уведомления")
                : new(null, "Rust+ Server ID из уведомления не совпадает однозначно с сопряжённым сервером; сначала сопрягите сервер заново");
        }
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var sameIp = candidates.Where(s => string.Equals(s.Ip.Trim(), ip.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (port is not null)
            {
                var exact = sameIp.Where(s => s.Port == port).ToList();
                if (exact.Count == 1) return new(exact[0], "по адресу, порту и Player ID из уведомления");
                if (exact.Count > 1) return new(null, "адрес и порт уведомления соответствуют нескольким записям сервера");
            }
            if (sameIp.Count == 1) return new(sameIp[0], "по адресу из уведомления");
            return new(null, $"сервер {ip}:{port?.ToString() ?? "?"} из уведомления не сопряжён с этим приложением");
        }
        // Без Server ID и адреса допустим только однозначный вариант для того же игрока; активный/последний сервер не угадываем.
        if (candidates.Count == 1) return new(candidates[0], "единственный сопряжённый сервер этого Player ID (Server ID и адрес в уведомлении не указаны)");
        return new(null, "в уведомлении нет Rust+ Server ID/адреса, а подходящих сопряжённых серверов несколько");
    }
}

public sealed record FcmRouteResult(FcmEventKind Kind, string Description, bool DeviceAdded = false, bool ServerAdded = false, bool AlarmDelivered = false);

/// <summary>
/// Обработка push-уведомлений Rust+ (источник — слушатель FCM в приложении): сопряжение сервера, сопряжение устройства (Smart Switch / Smart Alarm / Storage Monitor)
/// и срабатывание сигнализации. Результат записывается в общий <see cref="RustPlusHub"/>, поэтому обновляются обе вкладки.
/// </summary>
public sealed class FcmEventRouter
{
    private readonly PairedServerRepository _servers; private readonly RpDeviceRepository _devices; private readonly RpDevicePairingRepository _pairings; private readonly RustPlusDeviceEvents _events;
    private readonly RustPlusManager _mgr; private readonly RustPlusHub _hub; private readonly IClock _clock; private readonly DiagnosticsService? _diag;

    private readonly Dictionary<string, DateTimeOffset> _alarmSeen = new();
    public int ReceivedCount { get; private set; }
    public DateTimeOffset? LastReceivedUtc { get; private set; }
    public string LastEventText { get; private set; } = "Событий от Rust+ ещё не получено";
    public event Action? Received;

    public FcmEventRouter(PairedServerRepository servers, RpDeviceRepository devices, RpDevicePairingRepository pairings, RustPlusDeviceEvents events, RustPlusManager mgr, RustPlusHub hub, IClock clock, DiagnosticsService? diag = null)
    { _servers = servers; _devices = devices; _pairings = pairings; _events = events; _mgr = mgr; _hub = hub; _clock = clock; _diag = diag; }

    public async Task<FcmRouteResult> HandleAsync(string eventName, IReadOnlyDictionary<string, string?> props, CancellationToken ct = default)
    {
        var expanded = FcmMapper.Expand(props);
        var ev = FcmMapper.Map(eventName, expanded);
        ReceivedCount++; LastReceivedUtc = _clock.UtcNow;
        // в диагностику попадают только имена полей: токены и учётные данные в журнал не пишутся
        _diag?.Record("Rust+ push", SourceState.Ok, $"{eventName} → {ev.Kind} (поля: {string.Join(", ", expanded.Keys.Where(k => !k.Contains("token", StringComparison.OrdinalIgnoreCase)).Take(12))})");
        var result = await RouteAsync(ev, ct);
        LastEventText = $"{_clock.UtcNow.ToLocalTime():HH:mm:ss} · {result.Description}";
        Received?.Invoke();
        return result;
    }

    private async Task<FcmRouteResult> RouteAsync(FcmEvent ev, CancellationToken ct)
    {
        switch (ev.Kind)
        {
            case FcmEventKind.ServerPairing:
            {
                var hasAddr = RustPlusRoutes.HasDirectAddress(ev.Ip, ev.Port ?? 0);
                if (ev.PlayerId is null || ev.PlayerToken is null || (!hasAddr && !RustPlusRoutes.TryGetServerId(ev.ServerUid, out _)))
                    return new(ev.Kind, "Получено сопряжение сервера без полного набора данных (адрес или ID сервера / ID игрока / токен) — сервер не добавлен.");
                var server = await _servers.UpsertAsync(ev.Name ?? (hasAddr ? $"{ev.Ip}:{ev.Port}" : "Сервер Rust"), hasAddr ? ev.Ip! : "", hasAddr ? ev.Port!.Value : 0, ev.PlayerId.Value, ev.PlayerToken.Value, _clock.UtcNow, ct, ev.ServerUid);
                await _hub.ReloadServersAsync(_hub.Selected?.Id ?? server.Id, ct);
                return new(ev.Kind, $"Сервер «{server.Name}» сопряжён.", ServerAdded: true);
            }
            case FcmEventKind.DevicePairing:
            {
                var servers = await _servers.ListAsync(ct);
                var res = FcmServerResolver.Resolve(servers, ev.Ip, ev.Port, _mgr.Server, ev.ServerUid, ev.PlayerId);
                if (res.Server is null || ev.EntityId is null || ev.DeviceKind is null)
                {
                    _diag?.Record("Rust+ сопряжение устройства", SourceState.Error, $"Сервер не определён: {res.How}; Entity ID: {ev.EntityId?.ToString() ?? "отсутствует"}");
                    return new(ev.Kind, $"Получено уведомление о сопряжении устройства, но карточка не создана: {res.How}.");
                }
                var request = await _pairings.UpsertPendingAsync(res.Server, ev, _clock.UtcNow, ct);
                if (request is null)
                    return new(ev.Kind, $"Повторное или уже обработанное событие для устройства {ev.EntityId}; новая карточка не создана.");
                _hub.NotifyPairingRequestsChanged();
                return new(ev.Kind, $"Rust+ сообщил о сопряжении «{request.Name}» на сервере «{res.Server.Name}» ({res.How}). Нажмите «Проверить и добавить»: протокол не имеет отдельной команды подтверждения.");
            }
            case FcmEventKind.AlarmTriggered:
            {
                var servers = await _servers.ListAsync(ct);
                var res = FcmServerResolver.Resolve(servers, ev.Ip, ev.Port, _mgr.Server, ev.ServerUid, ev.PlayerId);
                // Одно срабатывание приходит и типизированным событием, и сырым сообщением сокета.
                var dupKey = !string.IsNullOrWhiteSpace(ev.Name) ? ev.Name!.Trim().ToLowerInvariant() : ev.EntityId?.ToString() ?? "";
                lock (_alarmSeen)
                {
                    if (_alarmSeen.TryGetValue(dupKey, out var prev) && _clock.UtcNow - prev < RustPlusDeviceEvents.DedupeWindow) return new(ev.Kind, $"Повтор того же срабатывания «{ev.Name}» пропущен.");
                    _alarmSeen[dupKey] = _clock.UtcNow;
                }
                _diag?.Record("Rust+ Alarm", SourceState.Ok, $"FCM AlarmTriggered: заголовок «{ev.Name}», entity={ev.EntityId?.ToString() ?? "—"}, сервер: {(res.Server is null ? "не определён (" + res.How + ")" : res.Server.Name + " — " + res.How)}");
                RpDevice? device = null; PairedServer? server = res.Server;
                if (server is not null) device = FindAlarm(await _devices.ListAsync(server.Id, ct), ev);
                if (device is null)
                {
                    // Тревога реального устройства не должна теряться из-за неоднозначного адреса сервера: ищем устройство по всем сопряжённым серверам.
                    var found = new List<(PairedServer S, RpDevice D)>();
                    foreach (var s in servers) { var d = FindAlarm(await _devices.ListAsync(s.Id, ct), ev); if (d is not null) found.Add((s, d)); }
                    if (found.Count == 1) { server = found[0].S; device = found[0].D; _diag?.Record("Rust+ Alarm", SourceState.Ok, $"устройство найдено по всем серверам: «{device.Name}» на «{server.Name}»"); }
                }
                if (device is null || server is null)
                {
                    // Устройство неизвестно, но сигнализация действительно сработала: показываем её по названию из уведомления.
                    var title = string.IsNullOrWhiteSpace(ev.Name) ? "Smart Alarm" : ev.Name!;
                    var srvName = server?.Name ?? _mgr.Server?.Name ?? "неизвестный сервер";
                    var shown = await _events.NotifyAlarmAsync(title, srvName, server?.Id, 0, _clock.UtcNow, ct);
                    return new(ev.Kind, $"Сработала сигнализация «{title}» на «{srvName}»: устройство не найдено среди сопряжённых, показано по названию из уведомления" + (shown ? "." : " (уведомление не доставлено, см. журнал доставки)."), AlarmDelivered: shown);
                }
                var delivered = await _events.HandleAsync(server, new RpDeviceChange(device.EntityId, new RpDeviceState(RpDeviceKind.SmartAlarm, true), _clock.UtcNow), ct);
                device = (await _devices.ListAsync(server.Id, ct)).FirstOrDefault(d => d.Id == device.Id) ?? device;
                return new(ev.Kind, $"Сработала сигнализация «{device.Name}» на «{server.Name}» ({res.How}); " + (delivered ? "уведомление доставлено." : device.AlarmNotify ? "уведомление не доставлено или это повтор (см. журнал доставки)." : "колокольчик выключен — уведомление не отправлялось."),
                    AlarmDelivered: delivered);
            }
            default:
                return new(ev.Kind, "Получено нераспознанное уведомление Rust+ (подробности — в «Диагностике»).");
        }
    }

    private static RpDevice? FindAlarm(IReadOnlyList<RpDevice> list, FcmEvent ev)
    {
        if (ev.EntityId is { } id) { var byId = list.FirstOrDefault(d => d.EntityId == id); if (byId is not null) return byId; }
        if (string.IsNullOrWhiteSpace(ev.Name)) return null;
        var name = ev.Name.Trim();
        var alarms = list.Where(d => d.Kind == RpDeviceKind.SmartAlarm).ToList();
        var exact = alarms.Where(d => string.Equals(d.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        return exact.Count > 1 ? null : alarms.Where(d => name.Contains(d.Name.Trim(), StringComparison.OrdinalIgnoreCase) || d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList() is { Count: 1 } one ? one[0] : null;
    }

    private async Task TryReadAsync(PairedServer server, ulong entityId, RpDeviceKind kind, CancellationToken ct)
    {
        if (!_mgr.IsConnected || _mgr.Server?.Id != server.Id) return;
        var r = await _mgr.ReadDeviceAsync(entityId, kind, ct);
        if (r.Ok && r.Value is not null) _hub.SetState(server.Id, entityId, r.Value);
    }
}
