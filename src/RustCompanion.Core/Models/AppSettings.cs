namespace RustCompanion.Core.Models;

public sealed class AppSettings
{
    public int ServerPollSeconds { get; set; } = 60;
    public int SteamPollSeconds { get; set; } = 120;
    public int RequestTimeoutSeconds { get; set; } = 8;
    public int A2sMinIntervalMs { get; set; } = 1000;
    public int SteamMinIntervalMs { get; set; } = 1000;
    public bool CloseToTray { get; set; } = true;
    public bool ServerMonitoringEnabled { get; set; } = true;
    public bool SteamMonitoringEnabled { get; set; } = true;
    public string SteamApiBaseUrl { get; set; } = "https://api.steampowered.com";

    // Наблюдение за игроками (Steam Query)
    public int WatchPollSeconds { get; set; } = 60;
    public int AbsenceConfirmations { get; set; } = 3;
    public bool AllowUntimestampedAbsence { get; set; } = true;
    public int NotifyMinIntervalSeconds { get; set; } = 60;

    // Каналы уведомлений (секреты — токен Telegram и URL вебхука — хранятся только в ISecretStore)
    // Оверлей поверх игры (включение каждого события — в «Уведомлениях»; здесь только способ показа)
    public bool OverlayEnabled { get; set; } = true;
    public int OverlayCorner { get; set; } = 1; // 0 TL, 1 TR, 2 BL, 3 BR
    public int OverlayDurationSeconds { get; set; } = 8;
    public int OverlayMargin { get; set; } = 24;
    public int OverlayMaxVisible { get; set; } = 3;

    // Оверлей-HUD: постоянная компактная панель с выбранными таймерами/прогнозами (позиция хранится, пока пользователь её не сбросит)
    public bool OverlayHudVisible { get; set; }
    public bool OverlayFollowRust { get; set; } = true;   // показывать оверлей на мониторе, где открыто окно Rust
    public bool OverlayLocked { get; set; } = true;       // true — клики проходят сквозь оверлей; false — режим перемещения/изменения размера
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayWidth { get; set; } = 320;
    public string OverlayHudItems { get; set; } = "";

    // Горячие клавиши и прогнозы по ручным отметкам
    public long HotkeyServerId { get; set; }
    public bool HotkeyConfirmOverlay { get; set; } = true;
    public int ForecastLeadMinutes { get; set; } = 10;
    public int ServerPanelRefreshSeconds { get; set; } = 30;

    // Rust+ и командный бот (бот по умолчанию выключен)
    public int RustPlusPollSeconds { get; set; } = 15;
    public bool BotEnabled { get; set; }
    public string BotPrefix { get; set; } = "!";
    public string BotLanguage { get; set; } = "ru";
    public string BotDisabledCommands { get; set; } = "";

    // Гниение: справочные значения из сторонних источников (не подтверждены по исходному коду игры) — пользователь может изменить
    public double DecayHoursWood { get; set; } = 3;
    public double DecayHoursStone { get; set; } = 5;
    public double DecayHoursMetal { get; set; } = 8;
    public double DecayHoursHqm { get; set; } = 12;
    public double DecayScale { get; set; } = 1.0;

    // Игровые уведомления: канал доставки для каждого типа (0 — отключено, 1 — Team Chat, 2 — Overlay, 3 — оба)
    public int AlarmNotifyChannel { get; set; } = 2;
    public int DeviceUnavailableNotifyChannel { get; set; } = 2;
    public int DeviceRestoredNotifyChannel { get; set; } = 2;
    public int PlayerExitNotifyChannel { get; set; } = 2;

    // Доступность устройств Rust+ (Smart Alarm, Smart Switch): опрос и число подряд неудачных чтений до состояния «недоступно»
    public int DeviceProbeSeconds { get; set; } = 30;
    public int DeviceProbeFailures { get; set; } = 3;

    // Собственная база серверов и игроков: периодический опрос известных серверов (A2S)
    public bool CrawlerEnabled { get; set; } = true;
    public int CrawlerPollSeconds { get; set; } = 120;
    public int CrawlerMaxServersPerCycle { get; set; } = 20;
    public int PresenceRetentionDays { get; set; } = 90;

    public bool TelegramEnabled { get; set; }
    public string TelegramChatId { get; set; } = "";
    public bool DiscordEnabled { get; set; }
    public bool TelegramSendJoin { get; set; } = true;
    public bool TelegramSendExit { get; set; } = true;
    public bool TelegramSendGroup { get; set; } = true;
    public bool DiscordSendJoin { get; set; } = true;
    public bool DiscordSendExit { get; set; } = true;
    public bool DiscordSendGroup { get; set; } = true;

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        foreach (var ch in new[] { AlarmNotifyChannel, DeviceUnavailableNotifyChannel, DeviceRestoredNotifyChannel, PlayerExitNotifyChannel })
            if (ch is < 0 or > 3) { e.Add("Канал уведомления должен быть: отключено, Team Chat, Overlay или оба."); break; }
        if (DeviceProbeSeconds < 10) e.Add("Опрос доступности устройств — не чаще раза в 10 с.");
        if (DeviceProbeFailures < 2) e.Add("Для состояния «недоступно» нужно не менее 2 неудачных чтений подряд.");
        if (CrawlerPollSeconds < 60) e.Add("Опрос известных серверов — не чаще раза в 60 с (защита от избыточных запросов).");
        if (CrawlerMaxServersPerCycle is < 1 or > 200) e.Add("За один цикл можно опросить от 1 до 200 серверов.");
        if (PresenceRetentionDays < 7) e.Add("История посещений хранится не менее 7 дней.");
        if (ServerPollSeconds < 15) e.Add("Интервал опроса серверов не может быть меньше 15 с.");
        if (SteamPollSeconds < 30) e.Add("Интервал опроса Steam не может быть меньше 30 с.");
        if (RequestTimeoutSeconds is < 2 or > 60) e.Add("Тайм-аут запроса должен быть от 2 до 60 с.");
        if (A2sMinIntervalMs < 200) e.Add("Пауза между A2S-запросами не может быть меньше 200 мс.");
        if (SteamMinIntervalMs < 200) e.Add("Пауза между запросами к Steam не может быть меньше 200 мс.");
        if (!Uri.TryCreate(SteamApiBaseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            e.Add("Адрес Steam API должен быть корректным https-адресом.");
        if (WatchPollSeconds < 30) e.Add("Интервал проверки игроков не может быть меньше 30 с (сервер может ограничивать частоту Steam Query).");
        if (AbsenceConfirmations < 1 || AbsenceConfirmations > 10) e.Add("Число подтверждений отсутствия — от 1 до 10 (по умолчанию 3).");
        if (OverlayWidth is < 160 or > 900) e.Add("Ширина оверлея — от 160 до 900.");
        if (ForecastLeadMinutes is < 1 or > 120) e.Add("Предупреждение о прогнозе — за 1–120 минут.");
        if (ServerPanelRefreshSeconds < 15) e.Add("Автообновление списка игроков — не чаще раза в 15 с (сервер может ограничивать частоту Steam Query).");
        if (RustPlusPollSeconds < 5) e.Add("Опрос Rust+ — не чаще раза в 5 с.");
        if (string.IsNullOrWhiteSpace(BotPrefix) || BotPrefix.Length > 3 || BotPrefix.Any(char.IsWhiteSpace)) e.Add("Префикс бота — 1–3 символа без пробелов.");
        if (BotLanguage is not ("ru" or "en")) e.Add("Язык бота: ru или en.");
        if (OverlayCorner is < 0 or > 3) e.Add("Угол оверлея: от 0 до 3.");
        if (OverlayDurationSeconds is < 2 or > 60) e.Add("Длительность показа оверлея — от 2 до 60 с.");
        if (OverlayMargin is < 0 or > 500) e.Add("Отступ оверлея — от 0 до 500 px.");
        if (OverlayMaxVisible is < 1 or > 5) e.Add("Одновременно на оверлее — от 1 до 5 плашек.");
        foreach (var h in new[] { DecayHoursWood, DecayHoursStone, DecayHoursMetal, DecayHoursHqm })
            if (!(h > 0 && h <= 168)) { e.Add("Время полного гниения материала — от 0 до 168 ч."); break; }
        if (!(DecayScale > 0 && DecayScale <= 100)) e.Add("Множитель decay сервера — от 0 до 100.");
        if (NotifyMinIntervalSeconds < 0) e.Add("Минимальный интервал уведомлений не может быть отрицательным.");
        return e;
    }
}

public interface ISettingsProvider { AppSettings Current { get; } void Update(AppSettings s); }
public sealed class SettingsProvider : ISettingsProvider
{
    public AppSettings Current { get; private set; } = new();
    public void Update(AppSettings s) => Current = s;
}
