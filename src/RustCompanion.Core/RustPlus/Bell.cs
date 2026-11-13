namespace RustCompanion.Core.RustPlus;

public enum RpBellState { Green, Red, Gray }

/// <summary>Состояние колокольчика Smart Alarm. Серый приоритетнее: если доступность устройства не подтверждена (нет связи/недоступно/неизвестно), цвет серый независимо от настройки.</summary>
public static class RpBell
{
    public static RpBellState Resolve(bool notifyEnabled, DeviceLive? live) =>
        live is null || live.Availability != DeviceAvailability.Available ? RpBellState.Gray
        : notifyEnabled ? RpBellState.Green : RpBellState.Red;

    public static string Tip(RpBellState s, bool notifyEnabled) => s switch
    {
        RpBellState.Green => "Уведомления включены, устройство доступно. Нажмите, чтобы выключить.",
        RpBellState.Red => "Уведомления выключены, устройство доступно. Нажмите, чтобы включить.",
        _ => "Устройство недоступно или связь потеряна — состояние неизвестно. Уведомления сейчас " + (notifyEnabled ? "включены" : "выключены") + "; нажмите, чтобы переключить."
    };
}
