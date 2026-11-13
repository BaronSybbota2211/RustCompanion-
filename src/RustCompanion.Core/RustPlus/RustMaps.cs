using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RustCompanion.Core.RustPlus;

/// <summary>Монумент из ответа RustMaps (координаты в метрах от центра карты, если сайт их отдал).</summary>
public sealed record RustMapsMonument(string Name, double? X, double? Y, string? IconUrl = null);

/// <summary>Данные карты с rustmaps.com: ссылки на картинки, страницу интерактивного просмотра (3D, зоны, монументы) и список монументов.</summary>
public sealed record RustMapsInfo(
    string? Id, long Seed, int Size, bool IsStaging, bool IsCustom,
    string? PageUrl, string? ImageUrl, string? ImageIconUrl, string? RawImageUrl, string? ThumbnailUrl,
    IReadOnlyList<RustMapsMonument> Monuments, IReadOnlyDictionary<string, string> Details, IReadOnlyList<RustMapsLayer>? Layers = null);

/// <summary>Дополнительная картинка-слой карты из ответа RustMaps (тепловые карты животных и ресурсов, зоны запрета строительства и т. п.). Состав слоёв определяет сам сервис.</summary>
public sealed record RustMapsLayer(string Key, string Url)
{
    /// <summary>Понятное русское название по ключу из ответа; неизвестные ключи показываются как есть.</summary>
    public string Title
    {
        get
        {
            var k = Key.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
            foreach (var (frag, title) in Names) if (k.Contains(frag)) return $"{title} ({Key})";
            return Key;
        }
    }
    private static readonly (string, string)[] Names =
    {
        ("nobuild", "Зона запрета строительства"), ("buildblock", "Зона запрета строительства"), ("buildable", "Зона строительства"),
        ("bear", "Медведи"), ("boar", "Кабаны"), ("horse", "Лошади"), ("wolf", "Волки"), ("stag", "Олени"), ("deer", "Олени"), ("chicken", "Куры"), ("animal", "Животные"),
        ("sulfur", "Серная руда"), ("metal", "Металлическая руда"), ("stone", "Каменная руда"), ("node", "Ресурсные узлы"), ("ore", "Руды"),
        ("hemp", "Конопля"), ("berr", "Ягоды"), ("tree", "Деревья"), ("wood", "Деревья"), ("resource", "Ресурсы"), ("playerspawn", "Спавны игроков"), ("heat", "Тепловая карта"), ("overlay", "Оверлей")
    };
}

public enum RustMapsStatus { Ok, NoKey, BadKey, NotGenerated, RateLimited, Error }

/// <summary>Результат поиска карты сервера: данные RustMaps (если есть ключ и карта найдена) и адрес страницы карты на сайте.</summary>
public sealed record RustMapsLookup(RustMapsResult Result, string? ViewerUrl, bool FromBinding = false);

public sealed record RustMapsResult(RustMapsStatus Status, string Message, RustMapsInfo? Info = null)
{
    public bool Ok => Status == RustMapsStatus.Ok;
}

/// <summary>
/// Клиент публичного API RustMaps v4 (https://api.rustmaps.com, заголовок X-API-Key). Ключ берётся из ISecretStore (DPAPI), в логи и ошибки не попадает.
/// Карту ищем по размеру и seed, которые сообщает сервер Rust+ (getInfo). Если карты ещё нет на сайте (404) — генерацию не запускаем,
/// чтобы не тратить лимиты ключа; страница интерактивного просмотра <see cref="ViewerUrl"/> создаёт её при открытии.
/// </summary>
public sealed class RustMapsClient
{
    public const string DashboardUrl = "https://rustmaps.com/dashboard";
    public const string BaseUrl = "https://api.rustmaps.com";
    private readonly HttpClient _http; private readonly ISecretStore _secrets; private readonly RustMapsLocator _locator;
    public RustMapsBindingStore Bindings { get; }
    public RustMapsLayerStore Layers { get; }

    public RustMapsClient(HttpClient http, ISecretStore secrets, RustMapsBindingStore bindings, RustMapsLayerStore layers) { _http = http; _secrets = secrets; _locator = new RustMapsLocator(http); Bindings = bindings; Layers = layers; }

    public bool HasKey => !string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.RustMapsApiKey));

    /// <summary>Страница карты на сайте: интерактивный просмотр с 3D, монументами, зонами, биомами и тепловыми картами. Работает без ключа.</summary>
    public static string ViewerUrl(int size, long seed) => $"https://rustmaps.com/map/{size.ToString(CultureInfo.InvariantCulture)}_{seed.ToString(CultureInfo.InvariantCulture)}";

    public async Task<RustMapsResult> GetMapAsync(int size, long seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.RustMapsApiKey))) return new(RustMapsStatus.NoKey, "Ключ RustMaps API не задан (Настройки → RustMaps).");
        if (size <= 0 || !RustMapsLocator.IsValidSeed(seed)) return new(RustMapsStatus.Error, "Seed этого сервера RustMaps не принимает (допустимы 0…2147483647), карта ищется по названию сервера.");
        return await FetchAsync($"/v4/maps/{size.ToString(CultureInfo.InvariantCulture)}/{seed.ToString(CultureInfo.InvariantCulture)}", size, seed, ct).ConfigureAwait(false);
    }

    /// <summary>Карта по её идентификатору на RustMaps (так адресуются кастомные карты): GET /v4/maps/{mapId}.</summary>
    public async Task<RustMapsResult> GetMapByIdAsync(string mapId, int size, long seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.RustMapsApiKey))) return new(RustMapsStatus.NoKey, "Ключ RustMaps API не задан (Настройки → RustMaps).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(mapId, "^[A-Za-z0-9_-]{6,64}$")) return new(RustMapsStatus.Error, "Некорректный идентификатор карты RustMaps.");
        return await FetchAsync("/v4/maps/" + mapId, size, seed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Находит карту сервера: 1) выбранную вручную (привязка к серверу, если размер и seed не менялись), 2) по размеру и seed (если seed допустим и карта не кастомная),
    /// 3) по названию сервера через BattleMetrics. Адрес страницы карты возвращается даже без ключа API.
    /// </summary>
    public async Task<RustMapsLookup> LookupAsync(long serverId, string? serverName, int size, long seed, bool customMap, CancellationToken ct)
    {
        string? viewer = null;
        if (serverId > 0 && await Bindings.GetAsync(serverId, ct).ConfigureAwait(false) is { } b && b.Size == size && b.Seed == seed
            && RustMapsLocator.FromUrl(b.Url, size, seed) is { } bt)
        {
            viewer = bt.ViewerUrl;
            return new(await ResolveAsync(bt, size, seed, "по вашей привязке", ct).ConfigureAwait(false), viewer, true);
        }
        if (!customMap && RustMapsLocator.IsValidSeed(seed))
        {
            viewer = ViewerUrl(size, seed);
            var direct = await GetMapAsync(size, seed, ct).ConfigureAwait(false);
            if (direct.Status is not (RustMapsStatus.NotGenerated or RustMapsStatus.Error)) return new(direct, viewer);
        }
        var found = await _locator.FindAsync(serverName, size, seed, ct).ConfigureAwait(false);
        if (found.Target is not { } t)
            return new(new(RustMapsStatus.Error, (customMap || !RustMapsLocator.IsValidSeed(seed)
                ? "Сервер использует кастомную карту или нестандартный seed, и автоматически карта не найдена."
                : "Карта не найдена на RustMaps.") + " Найдите сервер через Server Search на сайте (вкладка «Интерактивная») и нажмите «Запомнить эту карту».\n" + found.Report), viewer);
        viewer = t.ViewerUrl;
        return new(await ResolveAsync(t, size, seed, "по названию сервера", ct).ConfigureAwait(false), viewer);
    }

    private async Task<RustMapsResult> ResolveAsync(RustMapsTarget t, int size, long seed, string how, CancellationToken ct)
    {
        if (!HasKey) return new(RustMapsStatus.NoKey, $"Карта найдена {how}. Для данных и слоёв нужен ключ RustMaps API (Настройки → RustMaps).");
        return t.MapId is not null ? await GetMapByIdAsync(t.MapId, size, seed, ct).ConfigureAwait(false)
            : t.Seed is { } s && t.Size is { } z ? await GetMapAsync(z, s, ct).ConfigureAwait(false)
            : new RustMapsResult(RustMapsStatus.Error, "Карта найдена, но без идентификатора.");
    }

    private async Task<RustMapsResult> FetchAsync(string path, int size, long seed, CancellationToken ct)
    {
        var key = _secrets.Get(SecretNames.RustMapsApiKey)?.Trim();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            req.Headers.Add("X-API-Key", key);
            req.Headers.Add("Accept", "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden: return new(RustMapsStatus.BadKey, "RustMaps отклонил ключ API. Проверьте ключ на rustmaps.com/dashboard.");
                case HttpStatusCode.NotFound: return new(RustMapsStatus.NotGenerated, "Этой карты ещё нет на RustMaps. Откройте интерактивную карту на сайте — она будет создана.");
                case HttpStatusCode.TooManyRequests: return new(RustMapsStatus.RateLimited, "Превышен лимит запросов RustMaps, повторите позже.");
            }
            if (!resp.IsSuccessStatusCode) return new(RustMapsStatus.Error, $"RustMaps ответил {(int)resp.StatusCode}.");
            var info = Parse(body, size, seed);
            return info is null ? new(RustMapsStatus.Error, "Не удалось разобрать ответ RustMaps.") : new(RustMapsStatus.Ok, "Карта получена.", info);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(RustMapsStatus.Error, "Сеть недоступна или ответ не получен: " + ex.Message); }
    }

    /// <summary>Скачивание картинки карты (адреса с content.rustmaps.com). Ключ в такие запросы не передаётся.</summary>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)) return null;
        try { return await _http.GetByteArrayAsync(u, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Разбор ответа v4. Поля читаются терпимо (имена регистронезависимы, ответ может быть обёрнут в data), потому что схема сайта меняется.</summary>
    public static RustMapsInfo? Parse(string json, int size, long seed)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Find(root, "data") is { ValueKind: JsonValueKind.Object } d) root = d;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var mons = new List<RustMapsMonument>();
            if (Find(root, "monuments") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var m in arr.EnumerateArray()) if (ParseMonument(m) is { } mm) mons.Add(mm);
            var details = new Dictionary<string, string>();
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False
                    && !p.Name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    details[p.Name] = p.Value.ToString();
            return new RustMapsInfo(Str(root, "id"), Num(root, "seed") is { } sd ? (long)sd : seed, Num(root, "size") is { } sz ? (int)sz : size,
                Bool(root, "isStaging"), Bool(root, "isCustomMap"), Str(root, "url"), Str(root, "imageUrl"), Str(root, "imageIconUrl"),
                Str(root, "rawImageUrl"), Str(root, "thumbnailUrl"), mons, details, CollectLayers(root));
        }
        catch (JsonException) { return null; }
    }

    private static readonly HashSet<string> MainKeys = new(StringComparer.OrdinalIgnoreCase) { "url", "imageUrl", "imageIconUrl", "rawImageUrl", "thumbnailUrl", "downloadUrl", "mapUrl" };
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly string[] LayerHints = { "heat", "overlay", "nobuild", "buildblock", "resource", "animal", "node", "layer" };

    /// <summary>
    /// Ищет в ответе все ссылки на картинки rustmaps.com, кроме основных (карта, иконки, миниатюра, монументы): слои с животными, ресурсами, зонами запрета строительства.
    /// Структура ответа заранее не известна (объект «имя → ссылка» или массив {name, url}), поэтому обход общий, с ограничением глубины.
    /// </summary>
    public static IReadOnlyList<RustMapsLayer> CollectLayers(JsonElement root)
    {
        var found = new List<RustMapsLayer>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonElement e, string path, int depth)
        {
            if (depth > 5 || found.Count >= 64) return;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (depth == 0 && (p.Name.Equals("monuments", StringComparison.OrdinalIgnoreCase) || MainKeys.Contains(p.Name))) continue;
                        Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, depth + 1);
                    }
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var it in e.EnumerateArray())
                    {
                        var label = it.ValueKind == JsonValueKind.Object ? Str(it, "name") ?? Str(it, "type") ?? Str(it, "id") : null;
                        Walk(it, path + "." + (label ?? i.ToString(CultureInfo.InvariantCulture)), depth + 1); i++;
                    }
                    break;
                case JsonValueKind.String:
                    var v = e.GetString();
                    if (v is null || !Uri.TryCreate(v, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)) return;
                    var isImg = ImageExt.Any(x => u.AbsolutePath.EndsWith(x, StringComparison.OrdinalIgnoreCase));
                    var hinted = LayerHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.Contains(h, StringComparison.OrdinalIgnoreCase));
                    if ((isImg || hinted) && seen.Add(v)) found.Add(new RustMapsLayer(path, v));
                    break;
            }
        }
        Walk(root, "", 0);
        return found;
    }

    private static RustMapsMonument? ParseMonument(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return null;
        var name = Str(m, "displayName") ?? Str(m, "name") ?? Str(m, "type") ?? Str(m, "monument");
        if (string.IsNullOrWhiteSpace(name)) return null;
        double? x = null, y = null;
        var c = Find(m, "coordinates") ?? Find(m, "position") ?? Find(m, "location");
        var src = c is { ValueKind: JsonValueKind.Object } co ? co : m;
        x = Num(src, "x"); y = Num(src, "y") ?? Num(src, "z");
        return new RustMapsMonument(name!, x, y, Str(m, "iconUrl") ?? Str(m, "icon"));
    }

    private static JsonElement? Find(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in o.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
    private static string? Str(JsonElement o, string n) => Find(o, n) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    private static double? Num(JsonElement o, string n) => Find(o, n) is { } v && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d
        : Find(o, n) is { ValueKind: JsonValueKind.String } s && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null;
    private static bool Bool(JsonElement o, string n) => Find(o, n) is { } v && v.ValueKind == JsonValueKind.True;
}

/// <summary>Карта сервера на RustMaps: идентификатор (кастомные карты), размер и seed, если известны, и адрес страницы.</summary>
public sealed record RustMapsTarget(string? MapId, int? Size, long? Seed, string ViewerUrl);

/// <summary>Результат поиска: найденная карта (или null) и подробный отчёт о том, что именно вернул BattleMetrics (для диагностики в окне).</summary>
public sealed record RustMapsLocateResult(RustMapsTarget? Target, string Report);

/// <summary>
/// Ищет карту сервера по его названию через публичный API BattleMetrics (ключ не нужен): у серверов Rust там указана ссылка на карту RustMaps.
/// Порт Rust+ отличается от игрового, поэтому поиск по адресу из Rust+ не подходит.
/// </summary>
public sealed class RustMapsLocator
{
    private readonly HttpClient _http;
    public RustMapsLocator(HttpClient http) { _http = http; }

    /// <summary>RustMaps принимает seed только в диапазоне Int32; у серверов с кастомной картой в Rust+ может быть любое значение.</summary>
    public static bool IsValidSeed(long seed) => seed >= 0 && seed <= int.MaxValue;

    public async Task<RustMapsLocateResult> FindAsync(string? serverName, int size, long seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return new(null, "Rust+ не сообщил название сервера.");
        var report = new List<string>();
        foreach (var q in Queries(serverName))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.battlemetrics.com/servers?filter%5Bgame%5D=rust&filter%5Bsearch%5D=" + Uri.EscapeDataString(q) + "&page%5Bsize%5D=20");
                req.Headers.TryAddWithoutValidation("User-Agent", "RustCompanion");
                req.Headers.Add("Accept", "application/json");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { report.Add($"BattleMetrics «{q}»: HTTP {(int)resp.StatusCode}"); continue; }
                var (t, note) = PickDetailed(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), serverName, size, seed);
                report.Add($"BattleMetrics «{q}»: {note}");
                if (t is not null) return new(t, string.Join("\n", report));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report.Add($"BattleMetrics «{q}»: {ex.Message}"); }
        }
        return new(null, string.Join("\n", report));
    }

    private static string[] Words(string name) => name.Split('|')[0].Split(new[] { ' ', '—', '-', '–', '#', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> Queries(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var w = Words(name);
        foreach (var q in new[] { name.Trim(), name.Split('|')[0].Trim(), string.Join(' ', w.Take(4)), string.Join(' ', w.Take(3)) })
            if (q.Length >= 3 && seen.Add(q)) yield return q;
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Адрес страницы карты RustMaps → цель: либо /map/{размер}_{seed}, либо /map/{идентификатор} (кастомные карты). Чужие адреса отвергаются.</summary>
    public static RustMapsTarget? FromUrl(string? url, int? size, long? seed)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.EndsWith("rustmaps.com", StringComparison.OrdinalIgnoreCase)) return null;
        var seg = u.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(seg, @"^(\d+)_(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var sz) && long.TryParse(m.Groups[2].Value, out var sd) && IsValidSeed(sd)) return new RustMapsTarget(null, sz, sd, url);
        var byId = seg.Length > 0 && u.AbsolutePath.Contains("/map/", StringComparison.OrdinalIgnoreCase) && !m.Success;
        return new RustMapsTarget(byId ? seg : null, size, seed, url);
    }

    public static RustMapsTarget? Pick(string json, string serverName, int size, long seed) => PickDetailed(json, serverName, size, seed).Target;

    /// <summary>Выбирает из ответа BattleMetrics подходящий сервер (название, размер карты, seed) и берёт у него ссылку на RustMaps; note — что именно найдено.</summary>
    public static (RustMapsTarget? Target, string Note) PickDetailed(string json, string serverName, int size, long seed)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (null, "неожиданный ответ");
            var count = 0; RustMapsTarget? best = null; var bestScore = int.MinValue; var anyScore = int.MinValue; var anyNote = "";
            var want = Norm(serverName); var core = Norm(string.Join("", Words(serverName).Take(3)));
            foreach (var it in data.EnumerateArray())
            {
                count++;
                if (!it.TryGetProperty("attributes", out var at)) continue;
                var name = at.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var d = at.TryGetProperty("details", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : default;
                var score = 0; var nn = Norm(name);
                if (nn == want) score += 4; else if (nn.StartsWith(want) || want.StartsWith(nn) && nn.Length > 6) score += 2;
                if (core.Length >= 5 && nn.Contains(core)) score += 2;
                long? wSeed = null, mSeed = null; int? wSize = null, mSize = null; string? url = null;
                if (d.ValueKind == JsonValueKind.Object)
                {
                    wSeed = Num(d, "rust_world_seed"); wSize = Num(d, "rust_world_size") is { } ws ? (int)ws : null;
                    if (d.TryGetProperty("rust_maps", out var rm) && rm.ValueKind == JsonValueKind.Object)
                    {
                        url = rm.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        mSeed = Num(rm, "seed"); mSize = Num(rm, "size") is { } z ? (int)z : null;
                    }
                }
                var cSize = mSize ?? wSize; var cSeed = mSeed ?? wSeed;
                if (size > 0 && cSize is { } cs) { if (cs == size) score += 2; else score -= 6; }
                if (seed >= 0 && cSeed == seed) score += 3;
                if (score > anyScore) { anyScore = score; anyNote = $"ближайший сервер «{name}»: ссылка RustMaps {(url ?? "нет")}, world seed {(cSeed?.ToString() ?? "нет")}, размер {(cSize?.ToString() ?? "нет")}"; }
                var t = FromUrl(url, cSize, cSeed);
                if (t is null && cSize is { } sz2 && cSeed is { } sd2 && IsValidSeed(sd2)) t = new RustMapsTarget(null, sz2, sd2, RustMapsClient.ViewerUrl(sz2, sd2));
                if (t is null) continue;
                if (score > bestScore) { best = t; bestScore = score; }
            }
            if (count == 0) return (null, "серверов не найдено");
            return bestScore >= 2 ? (best, $"найдено серверов {count}, карта выбрана") : (null, $"найдено серверов {count}; " + (anyNote.Length > 0 ? anyNote : "данных о карте нет"));
        }
        catch (JsonException) { return (null, "ответ не разобран"); }
    }

    private static long? Num(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}

/// <summary>Карта, которую пользователь выбрал вручную на сайте RustMaps для сервера Rust+. Хранится в таблице settings; действует, пока совпадают размер и seed сервера (после вайпа карта другая).</summary>
public sealed record RustMapsBinding(string Url, int Size, long Seed);

public sealed class RustMapsBindingStore
{
    private readonly Data.Database _db;
    public RustMapsBindingStore(Data.Database db) { _db = db; }
    private static string K(long serverId) => "rustmaps_binding:" + serverId.ToString(CultureInfo.InvariantCulture);

    public Task<RustMapsBinding?> GetAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Data.Sql.Cmd(c, "SELECT value FROM settings WHERE key=@k", ("@k", K(serverId)));
            if (cmd.ExecuteScalar() is not string v) return null;
            try { return JsonSerializer.Deserialize<RustMapsBinding>(v); } catch (JsonException) { return null; }
        }, ct);

    public Task SetAsync(long serverId, RustMapsBinding b, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Data.Sql.Cmd(c, "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@k", K(serverId)), ("@v", JsonSerializer.Serialize(b)));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task ClearAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Data.Sql.Cmd(c, "DELETE FROM settings WHERE key=@k", ("@k", K(serverId))); cmd.ExecuteNonQuery(); }, ct);
}

/// <summary>
/// Слои, которые приложение подсмотрело во встроенном браузере, пока пользователь включал ресурсы, животных и зоны запрета строительства на странице карты RustMaps.
/// Публичный API таких слоёв может не отдавать, а картинки, которые грузит сам сайт, доступны по обычным ссылкам. Хранятся в settings по серверу Rust+.
/// </summary>
public sealed class RustMapsLayerStore
{
    private readonly Data.Database _db;
    public RustMapsLayerStore(Data.Database db) { _db = db; }
    private static string K(long serverId) => "rustmaps_layers:" + serverId.ToString(CultureInfo.InvariantCulture);

    public static string KeyFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var seg = Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
        var dot = seg.LastIndexOf('.'); if (dot > 0) seg = seg[..dot];
        return string.IsNullOrWhiteSpace(seg) ? u.Host : seg;
    }

    public Task<IReadOnlyList<RustMapsLayer>> GetAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RustMapsLayer>>(c =>
        {
            using var cmd = Data.Sql.Cmd(c, "SELECT value FROM settings WHERE key=@k", ("@k", K(serverId)));
            if (cmd.ExecuteScalar() is not string v) return Array.Empty<RustMapsLayer>();
            try { return JsonSerializer.Deserialize<List<RustMapsLayer>>(v) ?? new(); } catch (JsonException) { return Array.Empty<RustMapsLayer>(); }
        }, ct);

    /// <summary>Добавляет слой (одинаковые ссылки не дублируются); true — если список изменился.</summary>
    public async Task<bool> AddAsync(long serverId, string url, CancellationToken ct = default)
    {
        var cur = (await GetAsync(serverId, ct).ConfigureAwait(false)).ToList();
        if (cur.Any(l => l.Url.Equals(url, StringComparison.OrdinalIgnoreCase)) || cur.Count >= 64) return false;
        cur.Add(new RustMapsLayer(KeyFor(url), url));
        await _db.RunAsync(c =>
        {
            using var cmd = Data.Sql.Cmd(c, "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@k", K(serverId)), ("@v", JsonSerializer.Serialize(cur)));
            cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
        return true;
    }

    public Task ClearAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Data.Sql.Cmd(c, "DELETE FROM settings WHERE key=@k", ("@k", K(serverId))); cmd.ExecuteNonQuery(); }, ct);
}
