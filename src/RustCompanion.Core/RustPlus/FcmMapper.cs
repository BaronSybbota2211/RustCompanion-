using System.Text.Json;

namespace RustCompanion.Core.RustPlus;

public enum FcmEventKind { Unknown, ServerPairing, DevicePairing, AlarmTriggered }
public sealed record FcmEvent(FcmEventKind Kind, ulong? EntityId, RpDeviceKind? DeviceKind, string? Name, string? Ip, int? Port,
    ulong? PlayerId = null, int? PlayerToken = null, string? ServerUid = null, string? PersistentId = null);

/// <summary>
/// Разбор реальных событий RustPlusApi.Fcm: OnEntityPairing получает EntityId/EntityType/EntityName из Data,
/// специализированные OnSmart*Pairing — Entity ID из Data. Дополнительно поддерживается JSON body старых push.
/// Нераспознанное остаётся Unknown и попадает в диагностику, а не угадывается.
/// Официальные push-сообщения Rust+ несут данные JSON-строкой в поле body — <see cref="Expand"/> раскрывает её в плоский набор полей.
/// </summary>
public static class FcmMapper
{
    private static string? Get(IReadOnlyDictionary<string, string?> p, params string[] keys) { foreach (var k in keys) if (p.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v; return null; }

    /// <summary>Поднимает поля JSON из Body/Data/Message/Payload на верхний уровень (поля верхнего уровня не перезаписываются).</summary>
    public static IReadOnlyDictionary<string, string?> Expand(IReadOnlyDictionary<string, string?> props)
    {
        var d = new Dictionary<string, string?>(props, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "Body", "Data", "Message", "Payload", "Body" })
        {
            if (!d.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v) || !v.TrimStart().StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(v);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (d.ContainsKey(p.Name)) continue;
                    d[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => p.Value.GetRawText()
                    };
                }
            }
            catch (JsonException) { /* не JSON — остаётся как есть */ }
        }
        return d;
    }

    public static FcmEvent Map(string eventName, IReadOnlyDictionary<string, string?> props)
    {
        var name = (eventName ?? "").ToLowerInvariant();
        var type = (Get(props, "Type", "ChannelId", "Channel") ?? "").ToLowerInvariant();
        var n = name + " " + type;
        ulong? id = ulong.TryParse(Get(props, "EntityId", "entityId", "Id", "Data"), out var u) && u != 0 ? u : null;
        var title = Get(props, "EntityName", "Name", "Title");
        var ip = Get(props, "Ip", "Server", "Address"); int? port = int.TryParse(Get(props, "Port"), out var pt) ? pt : null;
        ulong? playerId = ulong.TryParse(Get(props, "PlayerId", "SteamId"), out var pl) ? pl : null;
        int? token = int.TryParse(Get(props, "PlayerToken", "Token"), out var tk) ? tk : null;
        var persistentId = Get(props, "PersistentId");
        var serverUid = Get(props, "ServerId");

        var isPairing = n.Contains("pairing") || type is "entity" or "server";
        if (isPairing && (n.Contains("server") || type == "server") && id is null)
            return new FcmEvent(FcmEventKind.ServerPairing, null, null, title, ip, port, playerId, token, serverUid ?? Get(props, "Id"), persistentId);

        RpDeviceKind? kind = n.Contains("alarm") ? RpDeviceKind.SmartAlarm : n.Contains("storage") ? RpDeviceKind.StorageMonitor : n.Contains("switch") ? RpDeviceKind.SmartSwitch : null;
        var entityTypeText = Get(props, "EntityType");
        kind ??= entityTypeText?.ToLowerInvariant() switch { "switch" => RpDeviceKind.SmartSwitch, "alarm" => RpDeviceKind.SmartAlarm, "storagemonitor" => RpDeviceKind.StorageMonitor, _ => null };
        if (kind is null && int.TryParse(entityTypeText ?? (int.TryParse(type, out _) ? type : Get(props, "Type")), out var et))
            kind = et switch { 1 => RpDeviceKind.SmartSwitch, 2 => RpDeviceKind.SmartAlarm, 3 => RpDeviceKind.StorageMonitor, _ => null };

        if (isPairing && id is not null && kind is not null) return new FcmEvent(FcmEventKind.DevicePairing, id, kind, title, ip, port, playerId, token, serverUid, persistentId);
        // срабатывание: идентификатор может отсутствовать в push — тогда устройство ищется по названию (см. FcmEventRouter)
        if (!isPairing && kind == RpDeviceKind.SmartAlarm && (id is not null || title is not null))
            return new FcmEvent(FcmEventKind.AlarmTriggered, id, RpDeviceKind.SmartAlarm, title, ip, port, playerId, token, serverUid, persistentId);
        return new FcmEvent(FcmEventKind.Unknown, id, kind, title, ip, port, playerId, token, serverUid, persistentId);
    }
}
