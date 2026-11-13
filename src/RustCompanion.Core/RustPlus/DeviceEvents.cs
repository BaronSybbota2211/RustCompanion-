using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

/// <summary>Обработка изменений устройств: история событий и тревога умной сигнализации (с защитой от дублей и индивидуальным включением «колокольчиком»).</summary>
public sealed class RustPlusDeviceEvents
{
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(10);
    private readonly RpDeviceRepository _devices; private readonly IEventNotifier _notifier; private readonly IClock _clock; private readonly IGameNotifier? _game;
    private readonly Dictionary<(long, ulong), DateTimeOffset> _lastAlarm = new(); private readonly Dictionary<(long, ulong), bool?> _lastSwitch = new();
    private readonly object _lock = new();
    private readonly DiagnosticsService? _diag;
    public RustPlusDeviceEvents(RpDeviceRepository devices, IEventNotifier notifier, IClock clock, IGameNotifier? game = null, DiagnosticsService? diag = null)
    { _devices = devices; _notifier = notifier; _clock = clock; _game = game; _diag = diag; }

    private void Log(string m) => _diag?.Record("Rust+ Alarm", SourceState.Ok, m);

    /// <summary>true — уведомление о тревоге фактически доставлено хотя бы одним каналом.</summary>
    public async Task<bool> HandleAsync(PairedServer server, RpDeviceChange change, CancellationToken ct = default)
    {
        var device = (await _devices.ListAsync(server.Id, ct)).FirstOrDefault(d => d.EntityId == change.EntityId);
        if (device is null) { Log($"событие устройства entity={change.EntityId} на «{server.Name}»: устройство не сопряжено, пропущено"); return false; }   // неизвестное устройство в историю не попадает
        var key = (server.Id, change.EntityId);
        // Тип берём из сохранённой записи устройства: событие websocket не содержит типа сущности (шлюз по умолчанию считает её переключателем).
        change = change with { State = change.State with { Kind = device.Kind } };

        if (change.State.Kind == RpDeviceKind.SmartSwitch)
        {
            bool changed; lock (_lock) { changed = !_lastSwitch.TryGetValue(key, out var prev) || prev != change.State.IsOn; _lastSwitch[key] = change.State.IsOn; }
            if (changed && change.State.IsOn is { } on) await _devices.AddEventAsync(device.Id, change.AtUtc, on ? "switch_on" : "switch_off", "", ct);
            return false;
        }
        if (change.State.Kind != RpDeviceKind.SmartAlarm || change.State.IsOn != true) return false;

        Log($"AlarmTriggered получен: «{device.Name}» entity={change.EntityId} сервер=«{server.Name}»");
        lock (_lock)
        {
            if (_lastAlarm.TryGetValue(key, out var last) && change.AtUtc - last < DedupeWindow) { Log("повтор срабатывания в пределах окна дедупликации — пропущен"); return false; }   // одно срабатывание — одно событие (websocket и FCM дают одно и то же)
            _lastAlarm[key] = change.AtUtc;
        }
        await _devices.AddEventAsync(device.Id, change.AtUtc, "alarm", device.AlarmNotify ? "" : "уведомление отключено", ct);
        if (!device.AlarmNotify) { Log($"«{device.Name}»: колокольчик выключен — уведомление не показывается"); return false; }   // колокольчик выключен: событие в истории есть, уведомления нет

        return await NotifyAlarmAsync(device.Name, server.Name, server.Id, device.Id, change.AtUtc, ct, device.AlarmText, device.AlarmChannel);
    }

    /// <summary>Показ тревоги по каналу из настроек. Возвращает true только если хотя бы один канал реально принял сообщение.</summary>
    public async Task<bool> NotifyAlarmAsync(string deviceName, string serverName, long? serverId, long deviceId, DateTimeOffset atUtc, CancellationToken ct = default, string? template = null, int? channel = null)
    {
        if (_game is not null)
        {
            var text = AlarmText(deviceName, template);
            Log($"«{deviceName}»: текст уведомления «{text.Overlay.Replace('\n', ' ')}», канал={(channel?.ToString() ?? "из настроек")}");
            var res = await _game.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, text.Chat, text.Overlay, serverId, null, channel), ct);
            Log($"«{deviceName}»: результат уведомления — {(res.Delivered ? "доставлено" : "НЕ доставлено")} ({res.Summary})");
            return res.Delivered;
        }
        var choice = _notifier.Raise(NotifyEventIds.RustPlusAlarm, $"rp-alarm:{deviceId}:{atUtc.ToUnixTimeSeconds() / (long)DedupeWindow.TotalSeconds}",
            $"Сигнализация «{deviceName}»", $"сработала на сервере «{serverName}»");
        return choice.Any;
    }

    public const string DefaultMessage = "СРАБОТАЛА СИГНАЛИЗАЦИЯ!";

    /// <summary>Уведомление: название устройства и сообщение, введённое пользователем для этой сигнализации (пусто — стандартное). Сведений о сервере нет. В Team Chat — одна строка.</summary>
    public static (string Chat, string Overlay) AlarmText(string device, string? message = null)
    {
        var msg = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message.Replace("\\n", "\n").Trim();
        var chat = string.Join(" ", (device + ": " + msg).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (chat, device + "\n\n" + msg);
    }
}
