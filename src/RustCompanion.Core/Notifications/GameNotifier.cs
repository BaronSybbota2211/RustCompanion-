using System.Net;
using RustCompanion.Core.Models;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Notifications;

/// <summary>Канал доставки игрового уведомления. Значение хранится в настройках и переживает перезапуск.</summary>
public enum NotifyChannel { Off = 0, TeamChat = 1, Overlay = 2, Both = 3 }

public static class NotifyChannelExt
{
    public static bool WantsChat(this NotifyChannel c) => c is NotifyChannel.TeamChat or NotifyChannel.Both;
    public static bool WantsOverlay(this NotifyChannel c) => c is NotifyChannel.Overlay or NotifyChannel.Both;
    public static NotifyChannel FromInt(int v) => Enum.IsDefined(typeof(NotifyChannel), v) ? (NotifyChannel)v : NotifyChannel.Off;
    public static string Ru(this NotifyChannel c) => c switch
    {
        NotifyChannel.TeamChat => "Rust+ Team Chat",
        NotifyChannel.Overlay => "Overlay поверх игры",
        NotifyChannel.Both => "Team Chat и Overlay",
        _ => "Отключено"
    };
}

/// <summary>Типы игровых уведомлений, для каждого из которых выбирается свой канал доставки.</summary>
public static class GameNotifyKind
{
    public const string Alarm = "game.alarm";
    public const string DeviceUnavailable = "game.device_unavailable";
    public const string DeviceRestored = "game.device_restored";
    public const string PlayerExit = "game.player_exit";
    public static readonly IReadOnlyList<string> All = new[] { Alarm, DeviceUnavailable, DeviceRestored, PlayerExit };

    public static string Title(string kind) => kind switch
    {
        Alarm => "Срабатывание Smart Alarm",
        DeviceUnavailable => "Устройство недоступно",
        DeviceRestored => "Доступность устройства восстановлена",
        PlayerExit => "Выход наблюдаемого игрока",
        _ => kind
    };

    public static NotifyChannel ChannelFor(AppSettings s, string kind) => NotifyChannelExt.FromInt(kind switch
    {
        Alarm => s.AlarmNotifyChannel,
        DeviceUnavailable => s.DeviceUnavailableNotifyChannel,
        DeviceRestored => s.DeviceRestoredNotifyChannel,
        PlayerExit => s.PlayerExitNotifyChannel,
        _ => 0
    });

    public static void SetChannel(AppSettings s, string kind, NotifyChannel c)
    {
        switch (kind)
        {
            case Alarm: s.AlarmNotifyChannel = (int)c; break;
            case DeviceUnavailable: s.DeviceUnavailableNotifyChannel = (int)c; break;
            case DeviceRestored: s.DeviceRestoredNotifyChannel = (int)c; break;
            case PlayerExit: s.PlayerExitNotifyChannel = (int)c; break;
        }
    }
}

/// <summary>
/// Игровое уведомление. ChatText — одна строка для Team Chat, OverlayText — может быть многострочным.
/// RustPlusServerId — сопряжённый сервер Rust+, к которому относится событие (для устройств). ServerHost — адрес A2S-сервера (для игроков):
/// Team Chat уходит только если активное соединение Rust+ относится именно к этому серверу.
/// </summary>
public sealed record GameNotification(string Kind, string ChatText, string OverlayText, long? RustPlusServerId = null, string? ServerHost = null, int? ChannelOverride = null);

public sealed record DeliveryOutcome(bool Attempted, bool Success, string Detail)
{
    public static readonly DeliveryOutcome NotRequested = new(false, false, "не запрошено");
}

public sealed record GameNotifyResult(NotifyChannel Channel, DeliveryOutcome Chat, DeliveryOutcome Overlay)
{
    public bool Delivered => Chat.Success || Overlay.Success;
    public string Summary
    {
        get
        {
            if (Channel == NotifyChannel.Off) return "канал отключён — уведомление не отправлялось";
            var parts = new List<string>();
            if (Chat.Attempted) parts.Add("Team Chat: " + Chat.Detail);
            if (Overlay.Attempted) parts.Add("Overlay: " + Overlay.Detail);
            return string.Join("; ", parts);
        }
    }
}

public sealed record GameNotifyLogEntry(DateTimeOffset AtUtc, string Kind, string Text, bool Delivered, string Summary)
{
    public string Line => $"{AtUtc.ToLocalTime():HH:mm:ss} · {GameNotifyKind.Title(Kind)} · {(Delivered ? "доставлено" : "НЕ доставлено")} — {Summary}";
}

/// <summary>Журнал реальных результатов отправки (в памяти, последние записи): пользователь видит, дошло ли сообщение.</summary>
public sealed class GameNotifyLog
{
    private readonly List<GameNotifyLogEntry> _items = new(); private readonly object _lock = new();
    public event Action<GameNotifyLogEntry>? Added;
    public void Add(GameNotifyLogEntry e)
    {
        lock (_lock) { _items.Add(e); while (_items.Count > 100) _items.RemoveAt(0); }
        Added?.Invoke(e);
    }
    public IReadOnlyList<GameNotifyLogEntry> Items { get { lock (_lock) return _items.ToList(); } }
}

public interface IGameNotifier
{
    Task<GameNotifyResult> NotifyAsync(GameNotification n, CancellationToken ct = default);
}

/// <summary>Сопоставление адреса A2S-сервера и адреса Rust+ (имя хоста может быть доменным): совпадение по строке или по пересечению IP-адресов.</summary>
public static class ServerHostMatcher
{
    public static bool Same(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var ra = IPAddress.TryParse(a.Trim(), out var ia) ? new[] { ia } : Dns.GetHostAddresses(a.Trim());
            var rb = IPAddress.TryParse(b.Trim(), out var ib) ? new[] { ib } : Dns.GetHostAddresses(b.Trim());
            return ra.Any(x => rb.Any(y => x.Equals(y)));
        }
        catch (Exception) { return false; }
    }
}

public sealed class GameNotifier : IGameNotifier
{
    private readonly ISettingsProvider _settings; private readonly RustPlusManager _manager; private readonly IOverlaySink _overlay;
    private readonly IClock _clock; private readonly GameNotifyLog _log; private readonly DiagnosticsService? _diag; private readonly Func<string, string, bool> _sameHost;

    public GameNotifier(ISettingsProvider settings, RustPlusManager manager, IOverlaySink overlay, IClock clock, GameNotifyLog log,
        DiagnosticsService? diag = null, Func<string, string, bool>? sameHost = null)
    { _settings = settings; _manager = manager; _overlay = overlay; _clock = clock; _log = log; _diag = diag; _sameHost = sameHost ?? ServerHostMatcher.Same; }

    public async Task<GameNotifyResult> NotifyAsync(GameNotification n, CancellationToken ct = default)
    {
        var s = _settings.Current;
        var channel = n.ChannelOverride is { } co ? NotifyChannelExt.FromInt(co) : GameNotifyKind.ChannelFor(s, n.Kind);
        var chat = DeliveryOutcome.NotRequested; var overlay = DeliveryOutcome.NotRequested;

        if (channel.WantsChat()) chat = await SendChatAsync(n, ct);
        if (channel.WantsOverlay())
        {
            if (!s.OverlayEnabled) overlay = new DeliveryOutcome(true, false, "оверлей выключен в настройках, сообщение не показано");
            else
            {
                try { _overlay.Show(n.Kind, n.OverlayText); overlay = new DeliveryOutcome(true, true, "передано в окно оверлея"); }
                catch (Exception ex) { overlay = new DeliveryOutcome(true, false, "ошибка показа: " + ex.Message); }
            }
        }

        var result = new GameNotifyResult(channel, chat, overlay);
        _log.Add(new GameNotifyLogEntry(_clock.UtcNow, n.Kind, n.ChatText, result.Delivered, result.Summary));
        _diag?.Record("Уведомление: " + GameNotifyKind.Title(n.Kind), result.Delivered || channel == NotifyChannel.Off ? SourceState.Ok : SourceState.Error, result.Summary);
        return result;
    }

    private async Task<DeliveryOutcome> SendChatAsync(GameNotification n, CancellationToken ct)
    {
        var srv = _manager.Server;
        if (!_manager.IsConnected || srv is null)
            return new DeliveryOutcome(true, false, "нет активного соединения Rust+ — сообщение не отправлено");
        if (n.RustPlusServerId is { } id && srv.Id != id)
            return new DeliveryOutcome(true, false, $"активное соединение Rust+ относится к другому серверу («{srv.Name}») — сообщение не отправлено");
        if (n.RustPlusServerId is null && n.ServerHost is { } host && !_sameHost(host, srv.Ip))
            return new DeliveryOutcome(true, false, $"активное соединение Rust+ — с другим сервером («{srv.Name}»), а событие относится к {host} — сообщение не отправлено");
        var r = await _manager.SendMessageAsync(n.ChatText, ct);
        return r.Ok ? new DeliveryOutcome(true, true, "отправлено в чат команды") : new DeliveryOutcome(true, false, "не отправлено: " + (r.Message ?? r.Code ?? "ошибка Rust+"));
    }
}
