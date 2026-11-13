namespace RustCompanion.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface INotifier { void Notify(string title, string message); }

/// <summary>Хранилище секретов (API-ключи, RCON-пароли). Реализация в App использует Windows DPAPI.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

public static class SecretNames
{
    public const string SteamApiKey = "steam_api_key";
    public const string TelegramBotToken = "telegram_bot_token";
    public const string DiscordWebhookUrl = "discord_webhook_url";
    public const string RustMapsApiKey = "rustmaps_api_key";                    // ключ API rustmaps.com (https://rustmaps.com/dashboard), только в DPAPI
    public const string RustPlusFcmCredentials = "rustplus_fcm_credentials";   // FCM/GCM/Expo-данные сопряжения (JSON), только в DPAPI
    public const string RustPlusCompanionToken = "rustplus_companion_token";   // токен входа Facepunch (новый канал /socket/), только в DPAPI
    public static string RustPlusToken(long serverId) => $"rustplus_token_{serverId}";
}
