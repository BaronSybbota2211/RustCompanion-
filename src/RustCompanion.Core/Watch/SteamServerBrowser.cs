using System.Text.Json;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;

namespace RustCompanion.Core.Watch;

/// <summary>
/// Поиск серверов Rust через официальный Steam Web API: IGameServersService/GetServerList (фильтры master-сервера Valve: \appid\, \name_match\ с «*»).
/// Нужен ключ Steam Web API (тот же, что для статуса игроков). Даёт название, число игроков, карту и адрес; списка игроков не даёт.
/// Идентификатор результата — «ip:queryport» (адрес запросов A2S).
/// </summary>
public sealed class SteamServerBrowser
{
    public const string RustAppId = "252490";
    private readonly HttpClient _http; private readonly ISecretStore _secrets; private readonly ISettingsProvider _settings; private readonly RateLimiter _limiter;
    public string LastRequestInfo { get; private set; } = "";

    public SteamServerBrowser(HttpClient http, ISecretStore secrets, ISettingsProvider settings, RateLimiter limiter)
    { _http = http; _secrets = secrets; _settings = settings; _limiter = limiter; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.SteamApiKey));

    /// <summary>Фильтр master-сервера. Спецсимволы фильтра (\ и управляющие) из запроса удаляются, чтобы пользователь не мог подмешать свои условия.</summary>
    public static string BuildFilter(string query)
    {
        var clean = new string(query.Where(c => c != '\\' && c != '*' && !char.IsControl(c)).ToArray()).Trim();
        return $"\\appid\\{RustAppId}\\name_match\\*{clean}*";
    }

    public async Task<SourceResult<IReadOnlyList<FoundServer>>> SearchAsync(string query, CancellationToken ct)
    {
        var key = _secrets.Get(SecretNames.SteamApiKey);
        if (string.IsNullOrWhiteSpace(key))
            return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.NotConfigured, "Ключ Steam Web API не задан (Настройки).");
        var s = _settings.Current;
        await _limiter.WaitAsync("steam", TimeSpan.FromMilliseconds(s.SteamMinIntervalMs), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(s.RequestTimeoutSeconds, 15)));   // master-сервер отвечает дольше обычного API
        var url = $"{s.SteamApiBaseUrl.TrimEnd('/')}/IGameServersService/GetServerList/v1/?key={Uri.EscapeDataString(key)}&limit=30&filter={Uri.EscapeDataString(BuildFilter(query))}";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("RustCompanion/1.0");
            using var resp = await _http.SendAsync(req, cts.Token);
            var code = (int)resp.StatusCode;
            LastRequestInfo = $"Steam GetServerList → HTTP {code}, {sw.ElapsedMilliseconds} мс";
            if (code is 401 or 403) return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.AccessDenied, $"Steam отклонил ключ API (HTTP {code}).");
            if (code == 429) return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.RateLimited, "Превышен лимит запросов Steam.");
            if (!resp.IsSuccessStatusCode) return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.Error, $"Steam вернул HTTP {code}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return SourceResult<IReadOnlyList<FoundServer>>.Ok(ParseServers(doc.RootElement));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { LastRequestInfo = $"Steam GetServerList → нет ответа за {sw.ElapsedMilliseconds} мс"; return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.NoResponse, "Steam не ответил вовремя."); }
        catch (HttpRequestException ex)
        { LastRequestInfo = "Steam GetServerList → сетевая ошибка: " + (ex.InnerException?.Message ?? ex.Message); return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.NoResponse, "Сетевая ошибка при обращении к Steam."); }
        catch (JsonException)
        { return SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.Error, "Не удалось разобрать ответ Steam."); }
    }

    public static IReadOnlyList<FoundServer> ParseServers(JsonElement root)
    {
        var list = new List<FoundServer>();
        if (!root.TryGetProperty("response", out var r) || !r.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var d in arr.EnumerateArray())
        {
            string? Str(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
            var addr = Str("addr"); var name = Str("name");
            if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(name)) continue;
            var host = addr.Contains(':') ? addr[..addr.LastIndexOf(':')] : addr;
            // addr — адрес и ПОРТ ЗАПРОСОВ; для net.connect нужен игровой порт: берём только если Steam его вернул, ничего не угадываем
            list.Add(new FoundServer(addr, name.Trim(), host, Int("gameport"), Int("players"), Int("max_players"), "Steam", Str("map"), null));
        }
        return list;
    }
}
