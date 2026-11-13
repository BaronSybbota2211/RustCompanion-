using System.Reflection;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.RustPlusAdapter;

public sealed record PairingResult(string Name, string Ip, int Port, ulong PlayerId, int PlayerToken, string? ServerUid = null);
public sealed record FcmNotification(string EventName, IReadOnlyDictionary<string, string?> Props);

/// <summary>
/// Сопряжение по штатной процедуре Rust+: вход в Steam (в браузере по умолчанию, любом) → регистрация в FCM → ожидание кнопки «Pair» в игре.
/// Steam Guard и защита сервера не обходятся: логин выполняет сам пользователь в окне браузера. Учётные данные FCM хранятся только в защищённом хранилище (JSON через колбэки).
/// </summary>
public sealed class RustPlusPairing
{
    private const string CompanionRegisterUrl = "https://companion-rust.facepunch.com/api/push/register";
    private readonly Func<string?> _load; private readonly Action<string?> _save; private readonly ISteamLoginProvider _login; private readonly HttpClient _http;
    private readonly Action<string>? _saveCompanionToken;
    /// <param name="saveCompanionToken">Сохраняет токен входа Facepunch (нужен новому каналу подключения /socket/{ServerId}). Токен не входит в учётные данные FCM, поэтому без этого колбэка он терялся сразу после входа.</param>
    public RustPlusPairing(Func<string?> loadCredentialsJson, Action<string?> saveCredentialsJson, ISteamLoginProvider login, HttpClient? http = null, Action<string>? saveCompanionToken = null)
    { _load = loadCredentialsJson; _save = saveCredentialsJson; _login = login; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; _saveCompanionToken = saveCompanionToken; }
    public bool HasCredentials => !string.IsNullOrWhiteSpace(_load());

    private static string Msg(Exception ex) => (ex is TargetInvocationException { InnerException: { } i } ? i : ex).Message;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N") + ".tmp");
    private static void Wipe(string path) { try { if (File.Exists(path)) { File.WriteAllBytes(path, new byte[Math.Max(1, (int)new FileInfo(path).Length)]); File.Delete(path); } } catch (Exception) { } }

    private static object? Create(Type t, params object?[] args) => Reflect.CreateInstance(t, args);

    /// <summary>Шаги 1–2: получить учётные данные FCM и зарегистрировать устройство в Rust+ (откроется окно браузера для входа в Steam).</summary>
    public async Task<RpResult> RegisterAsync(CancellationToken ct)
    {
        string? path = null;
        try
        {
            var regType = Reflect.FindType("FcmRegistration");
            if (regType is null) return RpResult.Fail("library_missing", "Библиотека RustPlusApi.Fcm.Registration не найдена.");
            var inst = Create(regType);
            var (f1, acq) = await Reflect.InvokeAsync(inst, regType, "AcquireCredentialsAsync", ct);
            if (!f1) return RpResult.Fail("api_mismatch", "Нет метода AcquireCredentialsAsync.");
            var r1 = Reflect.Response(acq); if (!r1.Ok) return RpResult.Fail(r1.Code, r1.Message);
            var creds = r1.Data ?? acq;
            if (creds is null) return RpResult.Fail("no_data", "Не удалось получить учётные данные FCM.");
            var expo = FindExpoToken(creds);
            if (expo is null) return RpResult.Fail("no_expo_token", "В учётных данных FCM нет Expo push-токена (изменился формат библиотеки).");

            // Вход в Steam выполняем сами: библиотека ждёт старый редирект на localhost, который Facepunch больше не делает.
            var login = await _login.LoginAsync(ct);
            // Токен входа сохраняем сразу и независимо от регистрации push: он нужен для подключения к серверу, даже если push-регистрация не удастся.
            try { _saveCompanionToken?.Invoke(login.Token); } catch (Exception) { /* хранилище недоступно — вход всё равно продолжится, а подключение сообщит, что токена нет */ }
            var reg = await RegisterPushAsync(expo, login.Token, ct);
            if (!reg.Ok) return reg;
            SetAuthToken(creds, login.Token);
            var updated = creds;

            var store = Reflect.FindType("CredentialsStore");
            if (store is null) return RpResult.Fail("library_missing", "Нет типа CredentialsStore для сохранения.");
            path = TempPath();
            var (f3, _) = await Reflect.InvokeAsync(null, store, "Save", ct, updated, path);
            if (!f3 || !File.Exists(path)) return RpResult.Fail("api_mismatch", "Не удалось сохранить учётные данные (метод CredentialsStore.Save).");
            _save(await File.ReadAllTextAsync(path, ct));
            return RpResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return RpResult.Fail("timeout", "Вход не завершён за 5 минут. Нажмите кнопку входа ещё раз."); }
        catch (Exception ex)
        {
            return RpResult.Fail(ex.GetType().Name, Msg(ex));
        }
        finally { if (path is not null) Wipe(path); }
    }


    /// <summary>Регистрирует Expo push-токен этого устройства за аккаунтом Steam (тот же запрос, что делает мобильное приложение Rust+).</summary>
    private async Task<RpResult> RegisterPushAsync(string expoToken, string authToken, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { AuthToken = authToken, DeviceId = "rustplus.js", PushKind = 3, PushToken = expoToken });
        using var req = new HttpRequestMessage(HttpMethod.Post, CompanionRegisterUrl) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return RpResult.Success;
        var text = await resp.Content.ReadAsStringAsync(ct);
        return RpResult.Fail("register_failed", $"Facepunch отклонил регистрацию устройства (HTTP {(int)resp.StatusCode}). {(text.Length > 200 ? text[..200] : text)}".Trim());
    }

    private static string? FindExpoToken(object? o, int depth = 0)
    {
        if (o is null || depth > 3) return null;
        if (o is string s) return s.StartsWith("ExponentPushToken", StringComparison.Ordinal) ? s : null;
        var t = o.GetType();
        if (t.IsPrimitive || t.IsEnum || o is System.Collections.IEnumerable) return null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? v; try { v = p.GetValue(o); } catch (Exception) { continue; }
            var found = FindExpoToken(v, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Если в модели учётных данных есть поле для токена Rust+ — сохраняем (для совместимости с rustplus.config.json); для приёма уведомлений он не нужен.</summary>
    private static void SetAuthToken(object creds, string token)
    {
        foreach (var p in creds.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.PropertyType == typeof(string) && p.SetMethod is not null && p.Name.Contains("AuthToken", StringComparison.OrdinalIgnoreCase))
            { try { p.SetValue(creds, token); } catch (Exception) { } }
    }

    private async Task<object?> LoadCredentialsAsync(CancellationToken ct)
    {
        var json = _load(); if (string.IsNullOrWhiteSpace(json)) return null;
        var store = Reflect.FindType("CredentialsStore"); if (store is null) return null;
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, json, ct);
            foreach (var n in new[] { "Load", "LoadAsync" })
            {
                var (found, res) = await Reflect.InvokeAsync(null, store, n, ct, path);
                if (found) { var r = Reflect.Response(res); return r.Data ?? res; }
            }
            return null;
        }
        finally { Wipe(path); }
    }

    /// <summary>Первое непустое значение свойства из списка имён; ищет на верхнем уровне и во вложенных Data/Body/Notification.</summary>
    private static object? Pick(object? o, params string[] names)
    {
        foreach (var holder in new[] { o, Reflect.P(o, "Data"), Reflect.P(o, "Body"), Reflect.P(Reflect.P(o, "Data"), "Body"), Reflect.P(o, "Notification") })
        {
            if (holder is null) continue;
            foreach (var n in names)
            {
                var v = Reflect.P(holder, n);
                if (v is not null && !(v is string sv && string.IsNullOrWhiteSpace(sv))) return v;
            }
        }
        return null;
    }

    /// <summary>Список «имя=значение» публичных свойств для диагностики; значения токенов скрываются.</summary>
    private static string DescribeFields(object? o)
    {
        if (o is null) return "(нет данных)";
        var parts = o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p =>
        {
            object? v; try { v = p.GetValue(o); } catch (Exception) { v = "?"; }
            var shown = p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ? (v is null ? "null" : "***") : v is string or ValueType or null ? v?.ToString() ?? "null" : v.GetType().Name;
            return $"{p.Name}={shown}";
        });
        return string.Join(", ", parts);
    }

    /// <summary>Шаг 3: ждать нажатия «Pair» в игре (Esc → Rust+ → Pair with server). Возвращает данные сервера и токен игрока.</summary>
    public async Task<RpResult<PairingResult>> WaitForServerPairingAsync(CancellationToken ct)
    {
        try
        {
            var creds = await LoadCredentialsAsync(ct);
            if (creds is null) return RpResult<PairingResult>.Fail("no_credentials", "Сначала выполните вход через Steam (шаг 1).");
            var lt = Reflect.FindType("PairingListener");
            if (lt is null) return RpResult<PairingResult>.Fail("library_missing", "Нет типа PairingListener.");
            var listener = Create(lt, creds);
            var (found, res) = await Reflect.InvokeAsync(listener, lt, "WaitForServerPairingAsync", ct);
            if (!found) return RpResult<PairingResult>.Fail("api_mismatch", "Нет метода WaitForServerPairingAsync.");
            var r = Reflect.Response(res); if (!r.Ok) return RpResult<PairingResult>.Fail(r.Code, r.Message);
            var d = r.Data ?? res;
            // Данные могут лежать как на верхнем уровне, так и во вложенных Data/Body; пустые значения пропускаем.
            var ip = Reflect.S(Pick(d, "Ip", "Server", "Address", "Host"));
            var portObj = Pick(d, "Port", "AppPort");
            var port = Reflect.I(portObj);
            if (ip is not null)
            {
                ip = ip.Trim();
                // «host:port» в одном поле (например, если порт не пришёл отдельно)
                var colon = ip.LastIndexOf(':');
                if (colon > 0 && ip.IndexOf(':') == colon && int.TryParse(ip[(colon + 1)..], out var embedded)) { if (port is null or < 1 or > 65535) port = embedded; ip = ip[..colon]; }
            }
            if (port is null && int.TryParse(Reflect.S(portObj)?.Trim(), out var parsedPort)) port = parsedPort;
            var pid = Reflect.U(Pick(d, "PlayerId", "SteamId")); var tok = Reflect.I(Pick(d, "PlayerToken", "Token"));
            var sidText = Reflect.S(Pick(d, "Id", "ServerId"));
            if (string.IsNullOrWhiteSpace(ip) || port is null or < 1 or > 65535)
            {
                // Facepunch больше не передаёт IP/порт в уведомлении: достаточно Server ID, подключение идёт через новый канал.
                if (Guid.TryParse(sidText?.Trim(), out var g) && g != Guid.Empty) { ip = ""; port = 0; }
                else
                return RpResult<PairingResult>.Fail("bad_pairing_address",
                    $"Уведомление о сопряжении пришло без корректного адреса сервера (ip='{ip}', port='{Reflect.S(portObj)}'). Поля уведомления: {DescribeFields(d)}. Повторите «Pair with server» в игре; если повторяется — пришлите этот текст разработчику.");
            }
            if (pid is null || tok is null) return RpResult<PairingResult>.Fail("bad_pairing", $"Уведомление о сопряжении не содержит ID игрока/токен. Поля уведомления: {DescribeFields(d)}.");
            return RpResult<PairingResult>.Success(new PairingResult(Reflect.S(Pick(d, "Name", "ServerName", "Title")) ?? (port > 0 ? $"{ip}:{port}" : "Сервер Rust"), ip ?? "", port ?? 0, pid.Value, tok.Value, sidText));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return RpResult<PairingResult>.Fail(ex.GetType().Name, Msg(ex)); }
    }

    /// <summary>Постоянный приём push-уведомлений Rust+ (сопряжение устройств, срабатывание сигнализации) — работает и без websocket-соединения с сервером.</summary>
    public async Task<(RpResult Result, IAsyncDisposable? Handle)> StartListeningAsync(Action<FcmNotification> onNotification, CancellationToken ct)
    {
        try
        {
            var creds = await LoadCredentialsAsync(ct);
            if (creds is null) return (RpResult.Fail("no_credentials", "Нет учётных данных FCM — выполните вход через Steam."), null);
            var t = Reflect.FindType("RustPlusFcm") ?? Reflect.FindType("RustPlusFcmListener");
            if (t is null) return (RpResult.Fail("library_missing", "Нет типа RustPlusFcm."), null);
            var inst = Create(t, creds)!;
            foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = ev.Name;
                Reflect.Subscribe(inst, ev, (_, a) =>
                {
                    // Сырое сообщение сокета (JSON-строка) несёт Entity ID и адрес сервера, которых нет в типизированном OnAlarmTriggered.
                    if (a is string raw) { var alarm = ParseRawAlarm(raw); if (alarm is not null) onNotification(new FcmNotification("OnAlarmTriggered", alarm)); return; }
                    onNotification(new FcmNotification(name, Flatten(a)));
                });
            }
            var (found, connRes) = await Reflect.InvokeAsync(inst, t, "ConnectAsync", ct);
            if (!found) return (RpResult.Fail("api_mismatch", "Нет метода ConnectAsync у слушателя FCM."), null);
            var cr = Reflect.Response(connRes);                       // «запущено» только если библиотека не сообщила об ошибке подключения
            if (!cr.Ok) return (RpResult.Fail(cr.Code, cr.Message ?? "Слушатель FCM не смог подключиться."), null);
            return (RpResult.Success, inst as IAsyncDisposable ?? (inst is IDisposable d ? new DisposableAdapter(d) : null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (RpResult.Fail(ex.GetType().Name, Msg(ex)), null); }
    }

    private sealed class DisposableAdapter(IDisposable d) : IAsyncDisposable { public ValueTask DisposeAsync() { d.Dispose(); return ValueTask.CompletedTask; } }

    /// <summary>Разбор сериализованного FcmMessage: возвращает поля только для канала alarm (остальные каналы приходят типизированными событиями).</summary>
    internal static IReadOnlyDictionary<string, string?>? ParseRawAlarm(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            string? Str(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined ? null : v.GetRawText()) : null;
            if (!string.Equals(Str(data, "ChannelId"), "alarm", StringComparison.OrdinalIgnoreCase)) return null;
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ChannelId"] = "alarm", ["Title"] = Str(data, "Title"), ["Message"] = Str(data, "Message"), ["PersistentId"] = Str(doc.RootElement, "PersistentId") };
            if (data.TryGetProperty("Body", out var body) && body.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                d["ServerId"] = Str(body, "Id"); d["Ip"] = Str(body, "Ip"); d["Port"] = Str(body, "Port");
                var eid = Str(body, "EntityId"); if (!string.IsNullOrWhiteSpace(eid) && eid != "0") d["EntityId"] = eid;
                var en = Str(body, "EntityName"); if (!string.IsNullOrWhiteSpace(en)) d["EntityName"] = en;   // Body.Name — имя сервера, оно не подставляется как название устройства
            }
            return d;
        }
        catch (Exception) { return null; }
    }

    private static IReadOnlyDictionary<string, string?> Flatten(object? args)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Add(object? o, string prefix)
        {
            if (o is null) return;
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                object? v; try { v = p.GetValue(o); } catch (Exception) { continue; }
                if (v is null) continue;
                if (v is string or ValueType) dict[prefix + p.Name] = v.ToString();
                else if (prefix.Length == 0 && p.Name is "Data" or "Body" or "Notification") Add(v, "");   // вложенные данные поднимаем на верхний уровень
            }
        }
        Add(args, ""); return dict;
    }
}
