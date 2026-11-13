namespace RustCompanion.RustPlusAdapter;

public sealed record SteamLoginData(string Token, ulong SteamId);

/// <summary>Вход через Steam в браузере по умолчанию. Реализация живёт в приложении (регистрация протокола rust-plus://, межпроцессная передача ссылки).</summary>
public interface ISteamLoginProvider
{
    /// <summary>Открывает страницу входа в браузере по умолчанию и ждёт ссылку rust-plus://rust-auth-session/?token=…&amp;steamId=….</summary>
    Task<SteamLoginData> LoginAsync(CancellationToken ct);
}

/// <summary>
/// Страница входа Facepunch (с октября 2026) заканчивает вход редиректом 302 на rust-plus://rust-auth-session/?token=…&amp;steamId=… (loopback-адреса и https больше не принимаются).
/// Поэтому приложение временно регистрирует для текущего пользователя обработчик rust-plus:// и получает ссылку от браузера.
/// </summary>
public static class RustAuthLink
{
    public const string Target = "rust-plus://rust-auth-session";
    public static string LoginUrl => "https://companion-rust.facepunch.com/login?returnUrl=" + Uri.EscapeDataString("/app?returnUrl=" + Target);

    public static bool IsAuthLink(string? s) => s is not null && s.StartsWith("rust-plus://rust-auth-session", StringComparison.OrdinalIgnoreCase);

    public static SteamLoginData Parse(string url)
    {
        if (!IsAuthLink(url)) throw new InvalidOperationException("Получена неожиданная ссылка.");
        var q = url.IndexOf('?');
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
            foreach (var part in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
                map[k] = v;
            }
        if (map.TryGetValue("error", out var err) && !string.IsNullOrWhiteSpace(err))
            throw new InvalidOperationException("Facepunch отклонил вход (" + err + "). Попробуйте ещё раз.");
        if (!map.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("В ответе Facepunch нет токена.");
        ulong.TryParse(map.GetValueOrDefault("steamId"), out var sid);
        return new SteamLoginData(token, sid);
    }
}
