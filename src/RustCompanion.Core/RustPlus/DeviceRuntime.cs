using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

/// <summary>
/// Фоновая обработка изменений устройств, пришедших по websocket Rust+: не зависит от открытой вкладки и UI-потока.
/// Тип устройства берётся из сохранённой записи (событие сервера типа не содержит), состояние пишется в общий RustPlusHub,
/// тревога Smart Alarm уходит в RustPlusDeviceEvents (колокольчик → канал уведомления → Overlay/Team Chat).
/// </summary>
public sealed class RustPlusDeviceRuntime
{
    private readonly RustPlusManager _manager; private readonly RpDeviceRepository _devices; private readonly RustPlusDeviceEvents _events; private readonly RustPlusHub _hub; private readonly DiagnosticsService? _diag;

    public RustPlusDeviceRuntime(RustPlusManager manager, RpDeviceRepository devices, RustPlusDeviceEvents events, RustPlusHub hub, DiagnosticsService? diag = null)
    {
        _manager = manager; _devices = devices; _events = events; _hub = hub; _diag = diag;
        _manager.DeviceChanged += (srv, ch) => _ = HandleSafelyAsync(srv, ch);
    }

    private async Task HandleSafelyAsync(PairedServer server, RpDeviceChange change)
    {
        try
        {
            var device = (await _devices.ListAsync(server.Id)).FirstOrDefault(d => d.EntityId == change.EntityId);
            if (device is null) return;                                                      // чужая сущность: не наше устройство
            var state = change.State with { Kind = device.Kind };
            if (state.IsOn is null && device.Kind != RpDeviceKind.StorageMonitor) return;     // широковещание без значения не меняет состояние
            _hub.SetState(server.Id, change.EntityId, state);
            _hub.SetAvailability(server.Id, change.EntityId, DeviceAvailability.Available, null);
            await _events.HandleAsync(server, change with { State = state });
        }
        catch (Exception ex) { _diag?.Record("Rust+ событие устройства", SourceState.Error, ex.GetType().Name + ": " + ex.Message); }
    }
}
