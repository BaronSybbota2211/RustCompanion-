using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustCompanion.Core.Models;

namespace RustCompanion.Core.Net;

public static class Redactor
{
    // Имя параметра может быть с префиксом (AuthToken=, PlayerToken=, api_key=): ловим любое имя, оканчивающееся на key/password/pwd/token/secret.
    private static readonly Regex Secret = new(@"(?i)\b([\w-]*(?:key|password|pwd|token|secret))=([^&\s]+)", RegexOptions.Compiled);
    private static readonly object Gate = new();
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal);

    /// <summary>Запоминает конкретное значение секрета (например, токен входа Facepunch): где бы оно ни встретилось в тексте — в исходном виде или как base64url — оно заменяется на ***.</summary>
    public static void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8) return;           // короткие значения (числа) легко совпадут со случайным текстом
        lock (Gate)
        {
            Known.Add(secret);
            Known.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));   // форма в субпротоколе WebSocket
            Known.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)));
        }
    }

    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var r = Secret.Replace(s, "$1=***");
        string[] known; lock (Gate) known = Known.ToArray();
        foreach (var k in known) if (k.Length > 0 && r.Contains(k, StringComparison.Ordinal)) r = r.Replace(k, "***", StringComparison.Ordinal);
        return r;
    }
}

/// <summary>Минимальная пауза между запросами к одному источнику.</summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _next = new();

    public async Task WaitAsync(string key, TimeSpan minInterval, CancellationToken ct)
    {
        TimeSpan delay;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var slot = _next.TryGetValue(key, out var n) && n > now ? n : now;
            _next[key] = slot + minInterval;
            delay = slot - now;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}

public interface IServerInfoSource
{
    Task<SourceResult<ServerInfo>> QueryAsync(string host, int port, CancellationToken ct);
}

public interface ISteamStatusSource
{
    Task<SourceResult<SteamPlayerStatus>> GetStatusAsync(string steamId, CancellationToken ct);
    /// <summary>Профиль по SteamID64, ссылке steamcommunity.com (/profiles/… или /id/…) или имени из ссылки.</summary>
    /// <summary>Общее время игрока в Rust (минуты) по данным Steam. Недоступно, если игрок скрыл сведения об играх.</summary>
    /// <summary>Статусы нескольких игроков одним запросом (Steam принимает до 100 SteamID за раз). Игроки, которых Steam не вернул, в результате отсутствуют.</summary>
    async Task<SourceResult<IReadOnlyList<SteamPlayerStatus>>> GetStatusesAsync(IReadOnlyList<string> steamIds, CancellationToken ct)
    {
        var list = new List<SteamPlayerStatus>();
        foreach (var id in steamIds)
        {
            var r = await GetStatusAsync(id, ct);
            if (!r.IsOk || r.Value is null) { if (r.State is SourceState.NotConfigured or SourceState.AccessDenied or SourceState.RateLimited) return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(r.State, r.Message); continue; }
            list.Add(r.Value);
        }
        return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Ok(list);
    }
    Task<SourceResult<int>> GetRustPlaytimeMinutesAsync(string steamId, CancellationToken ct) =>
        Task.FromResult(SourceResult<int>.Fail(SourceState.NotConfigured, "Время в игре недоступно."));
    Task<SourceResult<SteamPlayerStatus>> ResolveAsync(string input, CancellationToken ct) =>
        Task.FromResult(SourceResult<SteamPlayerStatus>.Fail(SourceState.NotConfigured, "Поиск по профилю недоступен."));
}

/// <summary>Разбор ответа Steam Query (A2S_INFO). Документация: Valve Developer Community, Server queries.</summary>
public static class A2sParser
{
    private static readonly byte[] Header = { 0xFF, 0xFF, 0xFF, 0xFF };
    private const string Payload = "Source Engine Query\0";

    public static byte[] BuildRequest(int? challenge)
    {
        var list = new List<byte>(Header) { 0x54 };
        list.AddRange(Encoding.ASCII.GetBytes(Payload));
        if (challenge is not null) list.AddRange(BitConverter.GetBytes(challenge.Value));
        return list.ToArray();
    }

    public static bool IsChallenge(byte[] b, out int challenge)
    {
        challenge = 0;
        if (b.Length < 9 || !b.AsSpan(0, 4).SequenceEqual(Header) || b[4] != 0x41) return false;
        challenge = BitConverter.ToInt32(b, 5);
        return true;
    }

    public static ServerInfo? ParseInfo(byte[] b)
    {
        try
        {
            if (b.Length < 6 || !b.AsSpan(0, 4).SequenceEqual(Header) || b[4] != 0x49) return null;
            var pos = 6; // пропускаем заголовок и байт протокола
            string Str() { var s = pos; while (b[pos] != 0) pos++; var r = Encoding.UTF8.GetString(b, s, pos - s); pos++; return r; }
            var name = Str(); var map = Str(); Str(); Str(); // folder, game
            pos += 2; // app id
            int players = b[pos++], max = b[pos++], bots = b[pos++];
            return new ServerInfo(name, map, players, max, bots);
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentException) { return null; }
    }
}

public sealed class A2sClient : IServerInfoSource
{
    private readonly ISettingsProvider _settings;
    private readonly RateLimiter _limiter;
    public A2sClient(ISettingsProvider settings, RateLimiter limiter) { _settings = settings; _limiter = limiter; }

    public async Task<SourceResult<ServerInfo>> QueryAsync(string host, int port, CancellationToken ct)
    {
        var s = _settings.Current;
        await _limiter.WaitAsync("a2s", TimeSpan.FromMilliseconds(s.A2sMinIntervalMs), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, port);
            await udp.SendAsync(A2sParser.BuildRequest(null).AsMemory(), cts.Token);
            var buf = (await udp.ReceiveAsync(cts.Token)).Buffer;
            if (A2sParser.IsChallenge(buf, out var ch))
            {
                await udp.SendAsync(A2sParser.BuildRequest(ch).AsMemory(), cts.Token);
                buf = (await udp.ReceiveAsync(cts.Token)).Buffer;
            }
            var info = A2sParser.ParseInfo(buf);
            return info is null
                ? SourceResult<ServerInfo>.Fail(SourceState.Error, "Ответ сервера не распознан (возможно, неподдерживаемый формат).")
                : SourceResult<ServerInfo>.Ok(info);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SourceResult<ServerInfo>.Fail(SourceState.NoResponse,
                "Нет ответа на запрос. Это не означает, что сервер выключен: порт запросов может быть закрыт или указан неверно.");
        }
        catch (SocketException)
        {
            return SourceResult<ServerInfo>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к серверу (адрес не найден или порт недоступен).");
        }
    }
}

public sealed class SteamClient : ISteamStatusSource
{
    public const string RustAppId = "252490";
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly ISettingsProvider _settings;
    private readonly RateLimiter _limiter;

    public SteamClient(HttpClient http, ISecretStore secrets, ISettingsProvider settings, RateLimiter limiter)
    { _http = http; _secrets = secrets; _settings = settings; _limiter = limiter; }

    public static bool IsValidSteamId64(string? id) => id is not null && Regex.IsMatch(id, @"^7656119\d{10}$");

    public async Task<SourceResult<SteamPlayerStatus>> GetStatusAsync(string steamId, CancellationToken ct)
    {
        if (!IsValidSteamId64(steamId))
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, "Некорректный SteamID64.");
        var key = _secrets.Get(SecretNames.SteamApiKey);
        if (string.IsNullOrWhiteSpace(key))
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.NotConfigured, "Ключ Steam Web API не задан (Настройки).");

        var s = _settings.Current;
        await _limiter.WaitAsync("steam", TimeSpan.FromMilliseconds(s.SteamMinIntervalMs), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
        var url = $"{s.SteamApiBaseUrl.TrimEnd('/')}/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId}";
        try
        {
            using var resp = await _http.GetAsync(url, cts.Token);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return SourceResult<SteamPlayerStatus>.Fail(SourceState.AccessDenied, "Steam отклонил ключ API (проверьте ключ).");
            if ((int)resp.StatusCode == 429)
                return SourceResult<SteamPlayerStatus>.Fail(SourceState.RateLimited, "Превышен лимит запросов Steam. Попробуйте позже.");
            if (!resp.IsSuccessStatusCode)
                return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, $"Steam вернул ошибку HTTP {(int)resp.StatusCode}.");

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            return Parse(steamId, json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoResponse, "Steam не ответил вовремя.");
        }
        catch (HttpRequestException)
        {
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к Steam.");
        }
    }

    public async Task<SourceResult<IReadOnlyList<SteamPlayerStatus>>> GetStatusesAsync(IReadOnlyList<string> steamIds, CancellationToken ct)
    {
        var ids = steamIds.Where(IsValidSteamId64).Distinct().ToList();
        if (ids.Count == 0) return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Ok(new List<SteamPlayerStatus>());
        var key = _secrets.Get(SecretNames.SteamApiKey);
        if (string.IsNullOrWhiteSpace(key)) return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.NotConfigured, "Ключ Steam Web API не задан (Настройки).");
        var s = _settings.Current; var all = new List<SteamPlayerStatus>();
        foreach (var chunk in ids.Chunk(100))
        {
            await _limiter.WaitAsync("steam", TimeSpan.FromMilliseconds(s.SteamMinIntervalMs), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
            var url = $"{s.SteamApiBaseUrl.TrimEnd('/')}/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={string.Join(",", chunk)}";
            try
            {
                using var resp = await _http.GetAsync(url, cts.Token);
                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.AccessDenied, "Steam отклонил ключ API (проверьте ключ).");
                if ((int)resp.StatusCode == 429) return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.RateLimited, "Превышен лимит запросов Steam.");
                if (!resp.IsSuccessStatusCode) return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.Error, $"Steam вернул ошибку HTTP {(int)resp.StatusCode}.");
                all.AddRange(ParseMany(await resp.Content.ReadAsStringAsync(cts.Token)));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.NoResponse, "Steam не ответил вовремя."); }
            catch (HttpRequestException) { return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к Steam."); }
            catch (JsonException) { return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Fail(SourceState.Error, "Не удалось разобрать ответ Steam."); }
        }
        return SourceResult<IReadOnlyList<SteamPlayerStatus>>.Ok(all);
    }

    public static IReadOnlyList<SteamPlayerStatus> ParseMany(string json)
    {
        var res = new List<SteamPlayerStatus>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("players", out var arr) || arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var p in arr.EnumerateArray())
        {
            string? Str(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Int(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var id = Str("steamid"); if (id is null) continue;
            var gameId = Str("gameid");
            res.Add(new SteamPlayerStatus(id, Str("personaname"), Int("personastate"), Int("communityvisibilitystate") == 3, gameId is null ? null : gameId == RustAppId, Str("gameserverip"), DateTimeOffset.UtcNow));
        }
        return res;
    }

    private static readonly Regex ProfileIdRx = new(@"steamcommunity\.com/profiles/(\d{17})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VanityRx = new(@"steamcommunity\.com/id/([A-Za-z0-9_\-]{2,64})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareVanityRx = new(@"^[A-Za-z0-9_\-]{2,64}$", RegexOptions.Compiled);

    /// <summary>Разбор ввода пользователя: (SteamID64, vanity). Ровно одно из значений заполнено, либо оба null.</summary>
    public static (string? SteamId, string? Vanity) ParseProfileInput(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return (null, null);
        if (IsValidSteamId64(t)) return (t, null);
        var m = ProfileIdRx.Match(t); if (m.Success && IsValidSteamId64(m.Groups[1].Value)) return (m.Groups[1].Value, null);
        m = VanityRx.Match(t); if (m.Success) return (null, m.Groups[1].Value);
        return !t.Contains('/') && BareVanityRx.IsMatch(t) ? (null, t) : (null, null);
    }

    public static SourceResult<int> ParsePlaytime(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                return SourceResult<int>.Fail(SourceState.NoData, "Steam не отдал время в игре: игрок скрыл сведения об играх (настройки приватности) или Rust нет в библиотеке.");
            foreach (var g in games.EnumerateArray())
                if (g.TryGetProperty("appid", out var a) && a.GetInt32() == int.Parse(RustAppId) && g.TryGetProperty("playtime_forever", out var m))
                    return SourceResult<int>.Ok(m.GetInt32());
            return SourceResult<int>.Fail(SourceState.NoData, "В ответе Steam нет данных по Rust.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return SourceResult<int>.Fail(SourceState.Error, "Не удалось разобрать ответ Steam."); }
    }

    public async Task<SourceResult<int>> GetRustPlaytimeMinutesAsync(string steamId, CancellationToken ct)
    {
        if (!IsValidSteamId64(steamId)) return SourceResult<int>.Fail(SourceState.Error, "Некорректный SteamID64.");
        var key = _secrets.Get(SecretNames.SteamApiKey);
        if (string.IsNullOrWhiteSpace(key)) return SourceResult<int>.Fail(SourceState.NotConfigured, "Ключ Steam Web API не задан (Настройки).");
        var s = _settings.Current;
        await _limiter.WaitAsync("steam", TimeSpan.FromMilliseconds(s.SteamMinIntervalMs), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
        var url = $"{s.SteamApiBaseUrl.TrimEnd('/')}/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(key)}&steamid={steamId}&include_played_free_games=1&appids_filter%5B0%5D={RustAppId}";
        try
        {
            using var resp = await _http.GetAsync(url, cts.Token);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return SourceResult<int>.Fail(SourceState.AccessDenied, "Steam отклонил ключ API или скрыл данные профиля.");
            if ((int)resp.StatusCode == 429) return SourceResult<int>.Fail(SourceState.RateLimited, "Превышен лимит запросов Steam. Попробуйте позже.");
            if (!resp.IsSuccessStatusCode) return SourceResult<int>.Fail(SourceState.Error, $"Steam вернул ошибку HTTP {(int)resp.StatusCode}.");
            return ParsePlaytime(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult<int>.Fail(SourceState.NoResponse, "Steam не ответил вовремя."); }
        catch (HttpRequestException) { return SourceResult<int>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к Steam."); }
    }

    public async Task<SourceResult<SteamPlayerStatus>> ResolveAsync(string input, CancellationToken ct)
    {
        var (id, vanity) = ParseProfileInput(input);
        if (id is null && vanity is null)
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, "Укажите SteamID64 (17 цифр), ссылку на профиль Steam или имя из ссылки steamcommunity.com/id/…");
        if (id is null)
        {
            var key = _secrets.Get(SecretNames.SteamApiKey);
            if (string.IsNullOrWhiteSpace(key)) return SourceResult<SteamPlayerStatus>.Fail(SourceState.NotConfigured, "Ключ Steam Web API не задан (Настройки).");
            var s = _settings.Current;
            await _limiter.WaitAsync("steam", TimeSpan.FromMilliseconds(s.SteamMinIntervalMs), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(s.RequestTimeoutSeconds));
            try
            {
                var url = $"{s.SteamApiBaseUrl.TrimEnd('/')}/ISteamUser/ResolveVanityURL/v1/?key={Uri.EscapeDataString(key)}&vanityurl={Uri.EscapeDataString(vanity!)}";
                using var resp = await _http.GetAsync(url, cts.Token);
                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    return SourceResult<SteamPlayerStatus>.Fail(SourceState.AccessDenied, "Steam отклонил ключ API (проверьте ключ).");
                if (!resp.IsSuccessStatusCode) return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, $"Steam вернул ошибку HTTP {(int)resp.StatusCode}.");
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var r = doc.RootElement.GetProperty("response");
                id = r.TryGetProperty("success", out var ok) && ok.GetInt32() == 1 && r.TryGetProperty("steamid", out var sid) ? sid.GetString() : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoResponse, "Steam не ответил вовремя."); }
            catch (HttpRequestException) { return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к Steam."); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, "Не удалось разобрать ответ Steam."); }
            if (!IsValidSteamId64(id)) return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoData, $"Профиль «{vanity}» не найден.");
        }
        return await GetStatusAsync(id!, ct);
    }

    public static SourceResult<SteamPlayerStatus> Parse(string steamId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r) ||
                !r.TryGetProperty("players", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return SourceResult<SteamPlayerStatus>.Fail(SourceState.NoData, "Steam не вернул данных по этому профилю.");

            var p = arr[0];
            string? Str(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Int(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

            var isPublic = Int("communityvisibilitystate") == 3;
            var gameId = Str("gameid");
            // Отсутствие gameid НЕ доказывает, что игрок не в игре (детали игры могут быть скрыты) — поэтому null, а не false.
            bool? inRust = gameId is null ? null : gameId == RustAppId;
            return SourceResult<SteamPlayerStatus>.Ok(new SteamPlayerStatus(
                steamId, Str("personaname"), Int("personastate"), isPublic, inRust, Str("gameserverip"), DateTimeOffset.UtcNow));
        }
        catch (JsonException)
        {
            return SourceResult<SteamPlayerStatus>.Fail(SourceState.Error, "Не удалось разобрать ответ Steam.");
        }
    }
}
