using RustCompanion.Core.Models;

namespace RustCompanion.Core.Notifications;

public static class NotifyEventIds
{
    public const string TimerMonument = "timer.monument";
    public const string TimerDeepSea = "timer.deepsea";
    public const string TimerHeli = "timer.heli";
    public const string TimerBradley = "timer.bradley";
    public const string Container = "container";
    public const string ForecastChinook = "forecast.chinook";
    public const string ForecastHeli = "forecast.heli";
    public const string ForecastAirdrop = "forecast.airdrop";
    public const string ForecastMonument = "forecast.monument";
    public const string ForecastBradleyAppear = "forecast.bradley_appear";
    public const string ForecastCargo = "forecast.cargo";
    public const string RustPlusAlarm = "rustplus.alarm";
    public const string RustPlusConnection = "rustplus.connection";
    public const string BradleyRespawn = "bradley.respawn";
    public const string Decay = "decay";
    public const string WatchExit = "watch.exit";

    public static string ForTimer(TrackerKind k) => k switch
    {
        TrackerKind.Monument => TimerMonument, TrackerKind.DeepSea => TimerDeepSea,
        TrackerKind.PatrolHelicopter => TimerHeli, _ => TimerBradley
    };
}

/// <summary>Каталог всех событий, способных отправлять уведомления. Для каждого — собственные независимые переключатели.</summary>
public sealed record NotifyEventInfo(string Id, string Title, string Section, bool HasExternal);

public static class NotifyCatalog
{
    public static readonly IReadOnlyList<NotifyEventInfo> All = new[]
    {
        new NotifyEventInfo(NotifyEventIds.TimerMonument, "Ручной таймер РТ: «за N минут» и «время пришло»", "Ручные таймеры", false),
        new NotifyEventInfo(NotifyEventIds.TimerDeepSea, "Ручной таймер Deep Sea", "Ручные таймеры", false),
        new NotifyEventInfo(NotifyEventIds.TimerHeli, "Ручной таймер патрульного вертолёта", "Ручные таймеры", false),
        new NotifyEventInfo(NotifyEventIds.TimerBradley, "Ручной таймер Bradley", "Ручные таймеры", false),
        new NotifyEventInfo(NotifyEventIds.Container, "Закрытый контейнер/событие: за 1 минуту до открытия", "Таймеры событий", false),
        new NotifyEventInfo(NotifyEventIds.ForecastChinook, "Прогноз Чинука: за 10 минут (приблизительно)", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.ForecastHeli, "Прогноз патрульного вертолёта: за 10 минут (приблизительно)", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.ForecastAirdrop, "Прогноз аэрдропа: за 10 минут (только при обоснованных данных)", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.ForecastBradleyAppear, "Bradley (по вашим отметкам появления): приближение ожидаемого появления", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.ForecastCargo, "Грузовой корабль (по вашим отметкам): приближение ожидаемого появления", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.RustPlusAlarm, "Rust+: сработала умная сигнализация", "Rust+", false),
        new NotifyEventInfo(NotifyEventIds.RustPlusConnection, "Rust+: потеря и восстановление соединения", "Rust+", false),
        new NotifyEventInfo(NotifyEventIds.ForecastMonument, "Респавн РТ: за 10 минут (по интервалу между вашими нажатиями)", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.BradleyRespawn, "Bradley: расчётное время респавна через 10 минут", "Прогнозы по наблюдениям", false),
        new NotifyEventInfo(NotifyEventIds.Decay, "Гниение: за 5 минут до расчётного разрушения (дополнительно включается у каждой записи)", "Гниение", false),
        // Вход, возвращение, смена сервера и порог групп больше не уведомляются. Для выхода игрока Overlay/Team Chat выбираются в блоке «Игровые уведомления»;
        // этот переключатель управляет только Windows-уведомлением и внешними каналами (Telegram/Discord).
        new NotifyEventInfo(NotifyEventIds.WatchExit, "Игрок: выход — Windows-уведомление и внешние каналы (Overlay и Team Chat — в «Игровых уведомлениях»)", "Наблюдение за игроками", true),
    };
}

public sealed record DeliveryChoice(bool Overlay, bool System, bool External)
{
    public static readonly DeliveryChoice Off = new(false, false, false);
    public bool Any => Overlay || System || External;
}

public interface IEventNotifier
{
    /// <summary>Показывает уведомление, если оно включено для события. Возвращает фактически применённые способы (Off — ничего не показано).</summary>
    DeliveryChoice Raise(string eventId, string key, string title, string text);
}

public interface IOverlaySink
{
    void Show(string eventId, string text);
    /// <summary>Уведомление с отдельными заголовком и текстом (для карточки Overlay). Реализации без отдельной поддержки получают прежнюю склеенную строку.</summary>
    void Show(string eventId, string title, string text) => Show(eventId, $"Rust Companion — {title}: {text}");
}

/// <summary>Группа оформления карточки Overlay: определяет цвет акцентной линии.</summary>
public enum ToastCategory { Alarm, Timer, Warning, Forecast, Restored, Info }

public static class OverlayToastKinds
{
    public const string ProductPrefix = "Rust Companion — ";

    /// <summary>Группа оформления по идентификатору события. Неизвестные события — нейтральные (серые).</summary>
    public static ToastCategory CategoryOf(string eventId) => eventId switch
    {
        NotifyEventIds.RustPlusAlarm or GameNotifyKind.Alarm => ToastCategory.Alarm,
        NotifyEventIds.Container => ToastCategory.Timer,
        NotifyEventIds.RustPlusConnection or GameNotifyKind.DeviceUnavailable or NotifyEventIds.WatchExit or GameNotifyKind.PlayerExit => ToastCategory.Warning,
        GameNotifyKind.DeviceRestored => ToastCategory.Restored,
        NotifyEventIds.BradleyRespawn or NotifyEventIds.Decay => ToastCategory.Forecast,
        _ when eventId.StartsWith("timer.", StringComparison.Ordinal) => ToastCategory.Timer,
        _ when eventId.StartsWith("forecast.", StringComparison.Ordinal) => ToastCategory.Forecast,
        _ => ToastCategory.Info
    };

    /// <summary>Разделяет готовую строку уведомления на заголовок и текст: префикс «Rust Companion — » снимается, заголовок берётся из вида события.</summary>
    public static (string Title, string Body) Split(string eventId, string text)
    {
        var body = text.StartsWith(ProductPrefix, StringComparison.Ordinal) ? text[ProductPrefix.Length..] : text;
        string? title = eventId switch
        {
            "hotkey" => "Горячая клавиша",
            "test" => "Тест оверлея",
            _ when GameNotifyKind.All.Contains(eventId) => GameNotifyKind.Title(eventId),
            _ => null
        };
        if (title is not null) return (title, body);
        var i = body.IndexOf(": ", StringComparison.Ordinal);            // формат «заголовок: текст» из роутера
        return i > 0 && i <= 60 ? (body[..i], body[(i + 2)..]) : ("Rust Companion", body);
    }
}

/// <summary>Текущие переключатели. Событие без записи = всё выключено (безопасное значение по умолчанию).</summary>
public sealed class NotificationPrefs
{
    private readonly Dictionary<string, DeliveryChoice> _map = new();
    private readonly object _lock = new();
    public DeliveryChoice Get(string eventId) { lock (_lock) return _map.TryGetValue(eventId, out var c) ? c : DeliveryChoice.Off; }
    public void Set(string eventId, DeliveryChoice c) { lock (_lock) _map[eventId] = c; }
    public void Load(IEnumerable<(string Id, DeliveryChoice Choice)> rows) { lock (_lock) { _map.Clear(); foreach (var (id, c) in rows) _map[id] = c; } }
}

public sealed class NotificationRouter : IEventNotifier
{
    private readonly NotificationPrefs _prefs;
    private readonly IOverlaySink _overlay;
    private readonly INotifier _system;
    private readonly ISettingsProvider _settings;

    public NotificationRouter(NotificationPrefs prefs, IOverlaySink overlay, INotifier system, ISettingsProvider settings)
    { _prefs = prefs; _overlay = overlay; _system = system; _settings = settings; }

    public DeliveryChoice Raise(string eventId, string key, string title, string text)
    {
        var c = _prefs.Get(eventId);
        var overlay = c.Overlay && _settings.Current.OverlayEnabled;
        var effective = c with { Overlay = overlay };
        if (!effective.Any) return DeliveryChoice.Off;       // уведомление выключено — расчёты продолжаются, показ не происходит
        if (overlay) _overlay.Show(eventId, title, text);
        if (c.System) _system.Notify(title, text);
        return effective;
    }
}

public enum OverlayCorner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }

public static class OverlayPlacement
{
    public static (double X, double Y) Compute(OverlayCorner c, double areaLeft, double areaTop, double areaWidth, double areaHeight, double w, double h, double margin) => c switch
    {
        OverlayCorner.TopLeft => (areaLeft + margin, areaTop + margin),
        OverlayCorner.TopRight => (areaLeft + areaWidth - w - margin, areaTop + margin),
        OverlayCorner.BottomLeft => (areaLeft + margin, areaTop + areaHeight - h - margin),
        _ => (areaLeft + areaWidth - w - margin, areaTop + areaHeight - h - margin)
    };
}

/// <summary>Флаги расширенного стиля окна: не активируется, не перехватывает мышь, не попадает в Alt+Tab/панель задач.</summary>
public static class OverlayWindowStyles
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public static int Apply(int existing) => existing | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
}

/// <summary>Данные карточки Overlay: вид события, заголовок, текст и время поступления.</summary>
public sealed record OverlayToastData(string Kind, string Title, string Body, DateTimeOffset At);

/// <summary>Очередь плашек оверлея: повтор того же текста не плодит дубликаты, а продлевает показ.</summary>
public sealed class OverlayQueue
{
    private sealed record Item(string Text, DateTimeOffset Expires, string Kind, string Title, string Body, DateTimeOffset At);
    private readonly List<Item> _items = new();
    private readonly int _max;
    public OverlayQueue(int maxVisible) { _max = Math.Max(1, maxVisible); }

    public void Add(string text, DateTimeOffset now, TimeSpan ttl) => Add(text, now, ttl, "", "", text);

    /// <summary>Повтор того же текста продлевает показ, но не создаёт дубликат; время первого появления сохраняется.</summary>
    public void Add(string text, DateTimeOffset now, TimeSpan ttl, string kind, string title, string body)
    {
        Prune(now);
        var i = _items.FindIndex(x => x.Text == text);
        if (i >= 0) _items[i] = _items[i] with { Expires = now + ttl };
        else _items.Add(new Item(text, now + ttl, kind, title, body, now));
        while (_items.Count > _max) _items.RemoveAt(0);
    }

    public void Prune(DateTimeOffset now) => _items.RemoveAll(x => x.Expires <= now);
    public IReadOnlyList<string> Visible(DateTimeOffset now) { Prune(now); return _items.Select(x => x.Text).ToList(); }
    public IReadOnlyList<OverlayToastData> VisibleItems(DateTimeOffset now) { Prune(now); return _items.Select(x => new OverlayToastData(x.Kind, x.Title, x.Body, x.At)).ToList(); }
    public void Clear() => _items.Clear();
}
