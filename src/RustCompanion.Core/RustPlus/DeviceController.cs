using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

/// <summary>Результат команды устройству: Ok — только если состояние подтверждено повторным чтением сервера.</summary>
public sealed record RpControlResult(bool Ok, string Message, RpDeviceState? State);

/// <summary>
/// Единая точка управления Smart Switch для обеих вкладок («Rust+» и «Rust+ Устройства») и Team Chat-команд:
/// проверка сервера/подключения → команда → подтверждение чтением → запись в общий RustPlusHub и SQLite.
/// Состояние меняется только по подтверждённым данным Rust+.
/// </summary>
public sealed class RustPlusDeviceController
{
    private readonly RustPlusManager _manager; private readonly RustPlusHub _hub; private readonly RpDeviceRepository _devices; private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RustPlusDeviceController(RustPlusManager manager, RustPlusHub hub, RpDeviceRepository devices, IClock clock)
    { _manager = manager; _hub = hub; _devices = devices; _clock = clock; }

    public async Task<RpControlResult> SetSwitchAsync(RpDevice device, bool on, string source, CancellationToken ct = default)
    {
        _manager.Log($"Switch: нажатие «{(on ? "Вкл" : "Выкл")}» ({source}) устройство «{device.Name}» entity={device.EntityId} сервер=#{device.ServerId}");
        if (device.Kind != RpDeviceKind.SmartSwitch) return new(false, "Это устройство не является Smart Switch.", null);
        if (!_manager.IsConnected) { _manager.Log("Switch: нет подключения Rust+"); return new(false, "Команда не отправлена: нет подключения к Rust+.", null); }
        if (_manager.Server?.Id != device.ServerId)
            return new(false, $"Команда не отправлена: подключение активно к «{_manager.Server?.Name}», а устройство принадлежит другому серверу.", null);

        await _gate.WaitAsync(ct);
        try
        {
            var r = await _manager.SetSwitchVerifiedAsync(device.EntityId, on, ct, device.ServerId);
            if (r.Ok)
            {
                var state = new RpDeviceState(RpDeviceKind.SmartSwitch, on);
                _hub.SetState(device.ServerId, device.EntityId, state);
                _hub.SetAvailability(device.ServerId, device.EntityId, DeviceAvailability.Available, null);
                await _devices.MarkSynchronizedAsync(device.Id, state, _clock.UtcNow, ct);
                return new(true, r.Message ?? (on ? "Включено (подтверждено)" : "Выключено (подтверждено)"), state);
            }
            // Ошибка команды: фактическое состояние читаем заново, ничего не подставляя.
            var read = await _manager.ReadDeviceAsync(device.EntityId, RpDeviceKind.SmartSwitch, ct);
            if (read.Ok && read.Value is not null)
            {
                _hub.SetState(device.ServerId, device.EntityId, read.Value);
                _hub.SetAvailability(device.ServerId, device.EntityId, DeviceAvailability.Available, null);
                await _devices.MarkSynchronizedAsync(device.Id, read.Value, _clock.UtcNow, ct);
            }
            else _hub.SetAvailability(device.ServerId, device.EntityId, DeviceAvailability.Unknown, "результат команды не подтверждён");
            return new(false, "Ошибка: " + r.Message, read.Value);
        }
        catch (OperationCanceledException) { return new(false, "Команда отменена.", null); }
        catch (Exception ex) { _manager.Log("Switch: исключение " + ex.GetType().Name + ": " + ex.Message); return new(false, "Ошибка: " + ex.Message, null); }
        finally { _gate.Release(); }
    }
}
