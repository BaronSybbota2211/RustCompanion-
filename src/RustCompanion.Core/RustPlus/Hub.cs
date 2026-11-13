namespace RustCompanion.Core.RustPlus;

public enum DeviceAvailability { Unknown, Available, Unavailable }

/// <summary>Последнее известное состояние устройства. Availability == Unknown — доступность не проверялась или нет соединения с сервером.</summary>
public sealed record DeviceLive(RpDeviceState? State, DeviceAvailability Availability, string? Note, DateTimeOffset UpdatedUtc);

/// <summary>
/// Единый источник состояния Rust+ для всех вкладок: выбранный сервер, список сопряжённых серверов, события изменения списка устройств и
/// их последнее известное состояние/доступность. Вкладки «Rust+» и «Rust+ Устройства» не хранят собственных копий — они читают и меняют только это.
/// События вызываются из любого потока; интерфейс сам переводит их в UI-поток.
/// </summary>
public sealed class RustPlusHub
{
    private readonly PairedServerRepository _servers; private readonly RustPlusManager _manager; private readonly IClock _clock;
    private readonly Dictionary<(long, ulong), DeviceLive> _live = new(); private readonly object _lock = new();
    private long? _lastManagerServerId;

    public IReadOnlyList<PairedServer> Servers { get; private set; } = Array.Empty<PairedServer>();
    public PairedServer? Selected { get; private set; }

    public event Action? ServersChanged;
    public event Action<PairedServer?>? SelectedChanged;
    public event Action<long>? DevicesChanged;                 // список устройств сервера изменился (добавлено/удалено/переименовано)
    public event Action<long, ulong>? DeviceStateChanged;      // состояние или доступность устройства изменились
    public event Action? PairingRequestsChanged;               // обнаружение/проверка/скрытие карточки сопряжения

    public RustPlusHub(PairedServerRepository servers, RustPlusManager manager, IClock clock)
    {
        _servers = servers; _manager = manager; _clock = clock;
        _manager.Changed += OnManagerChanged;
    }

    /// <summary>Подключение к другому серверу делает его выбранным (один раз при смене), но не отбирает выбор у пользователя при обычных обновлениях.</summary>
    private void OnManagerChanged()
    {
        var id = _manager.Server?.Id;
        if (id is null || id == _lastManagerServerId) return;
        _lastManagerServerId = id;
        Select(id);
    }

    public async Task ReloadServersAsync(long? preferId = null, CancellationToken ct = default)
    {
        var list = await _servers.ListAsync(ct);
        Servers = list;
        var keep = preferId ?? Selected?.Id;
        var selected = list.FirstOrDefault(s => s.Id == keep) ?? list.FirstOrDefault(s => s.Id == _manager.Server?.Id) ?? list.FirstOrDefault();
        var changed = selected?.Id != Selected?.Id;
        Selected = selected;
        ServersChanged?.Invoke();
        if (changed) SelectedChanged?.Invoke(Selected);
    }

    /// <summary>Выбор сервера. Возвращает false, если такого сопряжённого сервера нет.</summary>
    public bool Select(long? serverId)
    {
        var target = serverId is null ? null : Servers.FirstOrDefault(s => s.Id == serverId);
        if (serverId is not null && target is null) return false;
        if (target?.Id == Selected?.Id) return true;
        Selected = target;
        SelectedChanged?.Invoke(Selected);
        return true;
    }

    public void NotifyDevicesChanged(long serverId) => DevicesChanged?.Invoke(serverId);
    public void NotifyPairingRequestsChanged() => PairingRequestsChanged?.Invoke();

    public DeviceLive? GetLive(long serverId, ulong entityId) { lock (_lock) return _live.TryGetValue((serverId, entityId), out var v) ? v : null; }

    public void SetState(long serverId, ulong entityId, RpDeviceState state)
    {
        lock (_lock)
        {
            var prev = _live.TryGetValue((serverId, entityId), out var p) ? p : null;
            _live[(serverId, entityId)] = new DeviceLive(state, prev?.Availability ?? DeviceAvailability.Unknown, prev?.Note, _clock.UtcNow);
        }
        DeviceStateChanged?.Invoke(serverId, entityId);
    }

    public void SetAvailability(long serverId, ulong entityId, DeviceAvailability availability, string? note)
    {
        lock (_lock)
        {
            var prev = _live.TryGetValue((serverId, entityId), out var p) ? p : null;
            _live[(serverId, entityId)] = new DeviceLive(prev?.State, availability, note, _clock.UtcNow);
        }
        DeviceStateChanged?.Invoke(serverId, entityId);
    }

    /// <summary>Сброс доступности всех устройств сервера в «неизвестно» (например, при потере связи с сервером — это не недоступность устройств).</summary>
    public void ResetAvailability(long serverId, string note)
    {
        List<ulong> ids;
        lock (_lock)
        {
            ids = _live.Keys.Where(k => k.Item1 == serverId).Select(k => k.Item2).ToList();
            foreach (var id in ids) _live[(serverId, id)] = _live[(serverId, id)] with { Availability = DeviceAvailability.Unknown, Note = note, UpdatedUtc = _clock.UtcNow };
        }
        foreach (var id in ids) DeviceStateChanged?.Invoke(serverId, id);
    }

    public void Forget(long serverId, ulong entityId) { lock (_lock) _live.Remove((serverId, entityId)); }
}
