using System.Text;
using System.Text.Json;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Watch;

public enum NotifyKind { Join, Exit, Group }

public sealed record NotificationMessage(NotifyKind Kind, string Title, string Text, string ThrottleKey, long? GroupId = null);

public interface INotificationChannel
{
    string Name { get; }
    bool IsEnabled(AppSettings s, NotifyKind kind);
    Task<SourceResult<bool>> SendAsync(string title, string text, CancellationToken ct);
}

public sealed class TelegramChannel : INotificationChannel
{
    private readonly HttpClient _http; private readonly ISecretStore _secrets; private readonly ISettingsProvider _settings;
    public TelegramChannel(HttpClient http, ISecretStore secrets, ISettingsProvider settings) { _http = http; _secrets = secrets; _settings = settings; }
    public string Name => "Telegram";
    public bool IsEnabled(AppSettings s, NotifyKind k) => s.TelegramEnabled && k switch { NotifyKind.Join => s.TelegramSendJoin, NotifyKind.Exit => s.TelegramSendExit, _ => s.TelegramSendGroup };

    public async Task<SourceResult<bool>> SendAsync(string title, string text, CancellationToken ct)
    {
        var s = _settings.Current;
        var token = _secrets.Get(SecretNames.TelegramBotToken);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(s.TelegramChatId))
            return SourceResult<bool>.Fail(SourceState.NotConfigured, "Telegram: не заданы токен бота или chat id.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
        var body = JsonSerializer.Serialize(new { chat_id = s.TelegramChatId, text = $"{title}\n{text}" });
        try
        {
            using var resp = await _http.PostAsync($"https://api.telegram.org/bot{token.Trim()}/sendMessage",
                new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return SourceResult<bool>.Ok(true);
            return code switch
            {
                401 or 404 => SourceResult<bool>.Fail(SourceState.AccessDenied, "Telegram: неверный токен бота."),
                400 or 403 => SourceResult<bool>.Fail(SourceState.Error, "Telegram: чат не найден или бот не имеет к нему доступа."),
                429 => SourceResult<bool>.Fail(SourceState.RateLimited, "Telegram: превышен лимит сообщений."),
                _ => SourceResult<bool>.Fail(SourceState.Error, $"Telegram: ошибка доставки (HTTP {code}).")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult<bool>.Fail(SourceState.NoResponse, "Telegram: нет ответа."); }
        catch (HttpRequestException) { return SourceResult<bool>.Fail(SourceState.NoResponse, "Telegram: сетевая ошибка."); }
    }
}

public sealed class DiscordChannel : INotificationChannel
{
    private readonly HttpClient _http; private readonly ISecretStore _secrets; private readonly ISettingsProvider _settings;
    public DiscordChannel(HttpClient http, ISecretStore secrets, ISettingsProvider settings) { _http = http; _secrets = secrets; _settings = settings; }
    public string Name => "Discord";
    public bool IsEnabled(AppSettings s, NotifyKind k) => s.DiscordEnabled && k switch { NotifyKind.Join => s.DiscordSendJoin, NotifyKind.Exit => s.DiscordSendExit, _ => s.DiscordSendGroup };

    public static bool IsValidWebhook(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com") && u.AbsolutePath.StartsWith("/api/webhooks/");

    public async Task<SourceResult<bool>> SendAsync(string title, string text, CancellationToken ct)
    {
        var url = _secrets.Get(SecretNames.DiscordWebhookUrl);
        if (string.IsNullOrWhiteSpace(url) || !IsValidWebhook(url.Trim()))
            return SourceResult<bool>.Fail(SourceState.NotConfigured, "Discord: URL вебхука не задан или некорректен.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_settings.Current.RequestTimeoutSeconds));
        var body = JsonSerializer.Serialize(new { content = $"**{title}**\n{text}" });
        try
        {
            using var resp = await _http.PostAsync(url.Trim(), new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return SourceResult<bool>.Ok(true);
            return code switch
            {
                401 or 403 or 404 => SourceResult<bool>.Fail(SourceState.AccessDenied, "Discord: вебхук недействителен или удалён."),
                429 => SourceResult<bool>.Fail(SourceState.RateLimited, "Discord: превышен лимит сообщений."),
                _ => SourceResult<bool>.Fail(SourceState.Error, $"Discord: ошибка доставки (HTTP {code}).")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult<bool>.Fail(SourceState.NoResponse, "Discord: нет ответа."); }
        catch (HttpRequestException) { return SourceResult<bool>.Fail(SourceState.NoResponse, "Discord: сетевая ошибка."); }
    }
}

/// <summary>Рассылает событие по включённым каналам. Windows-уведомление идёт через общий INotifier. Ограничивает частоту по ключу.</summary>
public sealed class NotificationDispatcher
{
    private readonly IEventNotifier _events;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly ISettingsProvider _settings;
    private readonly IClock _clock;
    private readonly Services.DiagnosticsService? _diag;
    private readonly Dictionary<string, DateTimeOffset> _last = new();

    public NotificationDispatcher(IEventNotifier events, IEnumerable<INotificationChannel> channels, ISettingsProvider settings, IClock clock, Services.DiagnosticsService? diag = null)
    { _events = events; _channels = channels; _settings = settings; _clock = clock; _diag = diag; }

    public async Task<int> DispatchAsync(NotificationMessage m, CancellationToken ct)
    {
        var s = _settings.Current;
        var now = _clock.UtcNow;
        if (s.NotifyMinIntervalSeconds > 0 && _last.TryGetValue(m.ThrottleKey, out var prev) && now - prev < TimeSpan.FromSeconds(s.NotifyMinIntervalSeconds))
            return 0; // защита от повторов
        _last[m.ThrottleKey] = now;

        var eventId = NotifyEventIds.WatchExit;   // уведомляется только выход наблюдаемого игрока
        var choice = _events.Raise(eventId, m.ThrottleKey, m.Title, m.Text);   // оверлей/Windows — по переключателям события
        var delivered = (choice.Overlay ? 1 : 0) + (choice.System ? 1 : 0);
        if (choice.External)
            foreach (var c in _channels)
            {
                if (!c.IsEnabled(s, m.Kind)) continue;
                var r = await c.SendAsync(m.Title, m.Text, ct);
                _diag?.Record(c.Name, r.State, r.Message);
                if (r.IsOk) delivered++;
            }
        return delivered;
    }

    public async Task<SourceResult<bool>> SendTestAsync(INotificationChannel channel, CancellationToken ct) =>
        await channel.SendAsync("Rust Companion", "Тестовое сообщение: интеграция настроена.", ct);
}
