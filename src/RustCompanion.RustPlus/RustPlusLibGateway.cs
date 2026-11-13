using System.Reflection;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.RustPlusAdapter;

/// <summary>Реализация IRustPlusGateway поверх библиотеки RustPlusApi (MIT). Все обращения — через отражение, с понятными сообщениями при несовпадении API.</summary>
public sealed class RustPlusLibGateway : IRustPlusGateway
{
    private object? _client; private Type? _type;
    private readonly List<string> _raw = new();
    public event Action<RpChatMessage>? ChatReceived;
    public event Action<ulong, RpDeviceState>? DeviceChanged;
    public event Action<string>? ConnectionLost;

    /// <summary>Последние необработанные события библиотеки (имя + поля) — для диагностики несовпадений API.</summary>
    public IReadOnlyList<string> RawEvents { get { lock (_raw) return _raw.ToArray(); } }
    public bool IsConnected => _client is not null && Reflect.B(Reflect.P(_client, "IsConnected")) == true;

    private static string Msg(Exception ex) => (ex is TargetInvocationException { InnerException: { } i } ? i : ex).Message;

    private string Probe(params string[] contains) =>
        _type is null ? "" : " Доступные методы библиотеки: " + string.Join(", ", Reflect.MethodNames(_type, contains).Take(25)) + ".";

    /// <summary>Создаёт клиента библиотеки. Для нового канала (/socket/{ServerId}) задаёт ServerId и AuthToken подключения; токены не попадают в тексты ошибок.</summary>
    private object? Build(Type rpType, Type connType, RpCredentials c, RpRoute route, out string? error)
    {
        error = null;
        {
            object? conn = null; string? connErr = null;
            try { conn = Reflect.CreateInstance(connType, c.Ip, c.Port, c.PlayerId, c.PlayerToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { connErr = Msg(ex); }
            if (route.Kind == RpRouteKind.CompanionSocket)
            {
                if (conn is null || !RustPlusRoutes.TryGetServerId(c.ServerId, out var serverId) || string.IsNullOrWhiteSpace(c.CompanionToken))
                { error = "Нет данных для нового канала (ID сервера и токен входа)."; return null; }
                // Свойства init-only: при создании через отражение их выставляем до первого использования подключения.
                if (!SetProp(conn, "ServerId", serverId) || !SetProp(conn, "AuthToken", c.CompanionToken))
                { error = "Подключённая библиотека RustPlusApi не поддерживает маршрут /socket/{ServerId} (нет свойств ServerId/AuthToken). Нужна сборка из исходников в src/ThirdParty/RustPlusApi."; return null; }
            }
            try { return conn is not null ? Reflect.CreateInstance(rpType, conn) : Reflect.CreateInstance(rpType, c.Ip, c.Port, c.PlayerId, c.PlayerToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = (connErr is null ? "" : connErr + " | ") + Msg(ex); return null; }
        }
    }

    private static bool SetProp(object target, string name, object value)
    {
        var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || p.SetMethod is null) return false;
        try
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            p.SetValue(target, t.IsInstanceOfType(value) ? value : Convert.ChangeType(value, t));
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task<RpResult> AttemptAsync(CancellationToken ct)
    {
        try
        {
            SubscribeAll();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
            var (found, res) = await Reflect.InvokeAsync(_client, _type!, "ConnectAsync", cts.Token);
            if (!found) return RpResult.Fail("api_mismatch", "В библиотеке нет метода ConnectAsync." + Probe("Connect"));
            var r = Reflect.Response(res);
            return r.Ok ? RpResult.Success : RpResult.Fail(r.Code, r.Message ?? "Соединение отклонено.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return RpResult.Fail("timeout", "Сервер Rust+ не ответил за 20 с (проверьте адрес, порт и что Rust+ на сервере включён)."); }
        catch (Exception ex) { return RpResult.Fail(ex.GetType().Name, Msg(ex)); }
    }

    /// <summary>TCP-проверка порта Rust+ (app.port) с этого компьютера: отличает «порт недоступен» от ошибок протокола/токена.</summary>
    private static async Task<string?> ProbeAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(6));
            await tcp.ConnectAsync(ip, port, cts.Token);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "нет ответа за 6 с (порт фильтруется брандмауэром/провайдером или сервер выключен)"; }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task CloseClientAsync()
    {
        var c = _client; _client = null;
        try { if (c is IAsyncDisposable ad) await ad.DisposeAsync(); else if (c is IDisposable d) d.Dispose(); } catch (Exception) { }
    }

    /// <summary>
    /// Порядок маршрутов: новый канал Facepunch (wss://companion-rust.facepunch.com/socket/{ServerId}, токен входа в субпротоколах) → прямое подключение к порту Rust+ (запасной путь).
    /// Старый прокси /game/{ip}/{port} не пробуется: он отвечает HTTP 418 для всех серверов (см. RustPlusRoutes). Итоговая ошибка формируется в RustPlusRoutes.Summarize.
    /// </summary>
    public async Task<RpResult> ConnectAsync(RpCredentials c, CancellationToken ct)
    {
        try
        {
            var rpType = Reflect.FindType("RustPlus"); var connType = Reflect.FindType("RustPlusConnection");
            if (rpType is null || connType is null) return RpResult.Fail("library_missing", "Библиотека RustPlusApi не найдена или несовместима (нет типов RustPlus/RustPlusConnection).");

            var plan = RustPlusRoutes.Plan(c);
            if (plan.CompanionMissing is not null) T($"Rust+: новый канал недоступен — {plan.CompanionMissing}");
            var attempts = new List<(RpRoute Route, RpResult Result)>();
            foreach (var route in plan.Routes)
            {
                _client = Build(rpType, connType, c, route, out var err);
                if (_client is null) { attempts.Add((route, RpResult.Fail("api_mismatch", err))); T($"Rust+: маршрут «{route.Name}» не создан: {err}"); continue; }
                _type = _client.GetType();
                var r = await AttemptAsync(ct);
                if (r.Ok) { T($"Rust+: подключено через «{route.Name}»"); return r; }
                attempts.Add((route, r));
                T($"Rust+: маршрут «{route.Name}» не удался: {r.Code} {r.Message}");
                await CloseClientAsync();
            }

            // Проверка TCP-порта нужна только чтобы отличить «порт закрыт» от «сервер не ответил»; когда причина уже ясна (418/403/404), она не нужна.
            var direct = attempts.LastOrDefault(a => a.Route.Kind == RpRouteKind.Direct).Result;
            string? probe = null;
            if (direct is not null && RustPlusRoutes.HandshakeStatus(direct.Message) is null) probe = await ProbeAsync(c.Ip, c.Port, ct);
            return RustPlusRoutes.Summarize(c, plan, attempts, probe);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return RpResult.Fail(ex.GetType().Name, Msg(ex)); }
    }

    private async Task<(bool Found, bool Ok, string? Code, string? Message, object? Data)> CallAsync(string[] names, CancellationToken ct, params object?[] args)
    {
        if (_client is null || _type is null) return (false, false, "not_connected", "Нет соединения.", null);
        try
        {
            foreach (var n in names)
            {
                var (found, res) = await Reflect.InvokeAsync(_client, _type, n, ct, args);
                if (!found) continue;
                var r = Reflect.Response(res);
                return (true, r.Ok, r.Code, r.Message, r.Data);
            }
            return (false, false, "api_mismatch", $"В библиотеке нет метода {string.Join(" / ", names)}." + Probe(names[0][..Math.Min(names[0].Length, 6)]), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (true, false, ex.GetType().Name, Msg(ex), null); }
    }

    private static RpResult<T> Wrap<T>((bool Found, bool Ok, string? Code, string? Message, object? Data) r, Func<object?, T?> map) where T : class
    {
        if (!r.Ok) return RpResult<T>.Fail(r.Code, r.Message);
        var v = map(r.Data);
        return v is null ? RpResult<T>.Fail("no_data", "Сервер не вернул данные.") : RpResult<T>.Success(v);
    }

    public async Task<RpResult<RpServerInfo>> GetInfoAsync(CancellationToken ct) =>
        Wrap(await CallAsync(new[] { "GetInfoAsync" }, ct), d => d is null ? null : new RpServerInfo(
            Reflect.S(Reflect.P(d, "Name")), Reflect.I(Reflect.P(d, "PlayerCount", "Players")), Reflect.I(Reflect.P(d, "MaxPlayerCount", "MaxPlayers")),
            Reflect.I(Reflect.P(d, "QueuePlayerCount", "QueuedPlayers", "Queued")), Reflect.I(Reflect.P(d, "MapSize", "Size")), Reflect.S(Reflect.P(d, "Map")), Reflect.T(Reflect.P(d, "WipeTime", "Wipe")), Reflect.L(Reflect.P(d, "Seed"))));

    public async Task<RpResult<RpTime>> GetTimeAsync(CancellationToken ct) =>
        Wrap(await CallAsync(new[] { "GetTimeAsync" }, ct), d => d is null ? null : new RpTime(
            Reflect.D(Reflect.P(d, "Time", "GameTime")), Reflect.D(Reflect.P(d, "Sunrise")), Reflect.D(Reflect.P(d, "Sunset")),
            Reflect.D(Reflect.P(d, "DayLengthMinutes", "DayLength")), Reflect.D(Reflect.P(d, "TimeScale"))));

    public async Task<RpResult<RpTeam>> GetTeamAsync(CancellationToken ct) =>
        Wrap(await CallAsync(new[] { "GetTeamInfoAsync", "GetTeamAsync" }, ct), d => d is null ? null : new RpTeam(
            Reflect.U(Reflect.P(d, "LeaderSteamId", "Leader")),
            Reflect.Items(Reflect.P(d, "Members", "Players")).Select(m => new RpMember(Reflect.U(Reflect.P(m, "SteamId", "Id")) ?? 0, Reflect.S(Reflect.P(m, "Name")) ?? "?",
                Reflect.B(Reflect.P(m, "IsOnline", "Online")), Reflect.B(Reflect.P(m, "IsAlive", "Alive")), Reflect.D(Reflect.P(m, "X")), Reflect.D(Reflect.P(m, "Y")))).ToList(),
            TeamNotes(d)));

    private static IReadOnlyList<RpTeamNote> TeamNotes(object d)
    {
        var l = new List<RpTeamNote>();
        if (Reflect.P(d, "DeathNote") is { } dn && Reflect.D(Reflect.P(dn, "X")) is { } dx && Reflect.D(Reflect.P(dn, "Y")) is { } dy) l.Add(new RpTeamNote(true, false, dx, dy, null, 0, 0));
        void Add(string prop, bool leader)
        {
            foreach (var n in Reflect.Items(Reflect.P(d, prop)))
                if (Reflect.D(Reflect.P(n, "X")) is { } x && Reflect.D(Reflect.P(n, "Y")) is { } y)
                    l.Add(new RpTeamNote(false, leader, x, y, Reflect.S(Reflect.P(n, "Text", "Label")), Reflect.I(Reflect.P(n, "Icon")) ?? 0, Reflect.I(Reflect.P(n, "Color", "Colour")) ?? 0));
        }
        Add("Notes", false); Add("LeaderNotes", true);
        return l;
    }

    public async Task<RpResult<RpMap>> GetMapAsync(CancellationToken ct)
    {
        var r = await CallAsync(new[] { "GetMapAsync" }, ct);
        if (!r.Ok) return RpResult<RpMap>.Fail(r.Code, r.Message);
        var d = r.Data; var jpg = Reflect.P(d, "JpgImage", "Image") as byte[];
        if (jpg is null || jpg.Length == 0) return RpResult<RpMap>.Fail("no_data", "Сервер не прислал картинку карты.");
        var mons = Reflect.Items(Reflect.P(d, "Monuments")).Select(m => new RpMapMonument(Reflect.S(Reflect.P(m, "Name")) ?? "", Reflect.D(Reflect.P(m, "X")) ?? 0, Reflect.D(Reflect.P(m, "Y")) ?? 0))
            .Where(m => m.Name.Length > 0).ToList();
        return RpResult<RpMap>.Success(new RpMap(jpg, Reflect.I(Reflect.P(d, "Width")) ?? 0, Reflect.I(Reflect.P(d, "Height")) ?? 0, Reflect.I(Reflect.P(d, "OceanMargin")) ?? 0, 0, mons));
    }

    // Маркеры вертолёта, Чинука, взрывов, ящиков, корабля, странствующего торговца и торговых автоматов сервер больше не отправляет в Rust+ (патч Power Trip, 06.08.2026).
    private static readonly (string Prop, RpMarkerKind Kind)[] MarkerGroups =
    {
        ("PlayerMarkers", RpMarkerKind.Player), ("GenericRadiusMarkers", RpMarkerKind.Radius), ("UnknownMarkers", RpMarkerKind.Unknown)
    };

    public async Task<RpResult<IReadOnlyList<RpMapMarker>>> GetMapMarkersAsync(CancellationToken ct)
    {
        var r = await CallAsync(new[] { "GetMapMarkersAsync" }, ct);
        if (!r.Ok) return RpResult<IReadOnlyList<RpMapMarker>>.Fail(r.Code, r.Message);
        var list = new List<RpMapMarker>();
        foreach (var (prop, kind) in MarkerGroups)
            foreach (var kv in Reflect.Items(Reflect.P(r.Data, prop)))
            {
                var m = Reflect.P(kv, "Value") ?? kv;
                var x = Reflect.D(Reflect.P(m, "X")); var y = Reflect.D(Reflect.P(m, "Y"));
                if (x is null || y is null) continue;
                list.Add(new RpMapMarker(Reflect.U(Reflect.P(m, "Id")) ?? Reflect.U(Reflect.P(kv, "Key")) ?? 0, kind, x.Value, y.Value,
                    Reflect.S(Reflect.P(m, "Name")), Reflect.D(Reflect.P(m, "Radius")), Reflect.D(Reflect.P(m, "Rotation")), Reflect.U(Reflect.P(m, "SteamId"))));
            }
        return RpResult<IReadOnlyList<RpMapMarker>>.Success(list);
    }

    private static RpChatMessage? ChatFrom(object? o)
    {
        if (o is null) return null;
        var m = Reflect.P(o, "Data") ?? o;
        var inner = Reflect.P(m, "Message");
        var text = inner as string ?? Reflect.S(Reflect.P(m, "Text"));
        if (inner is not null and not string) { m = inner; text = Reflect.S(Reflect.P(m, "Message", "Text")); }
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new RpChatMessage(Reflect.U(Reflect.P(m, "SteamId", "Id")), Reflect.S(Reflect.P(m, "Name")) ?? "?", text.Trim(), Reflect.T(Reflect.P(m, "Time", "Timestamp", "Date")));
    }

    public async Task<RpResult<IReadOnlyList<RpChatMessage>>> GetTeamChatAsync(CancellationToken ct)
    {
        var r = await CallAsync(new[] { "GetTeamChatAsync", "GetTeamChatInfoAsync" }, ct);
        if (!r.Ok) return RpResult<IReadOnlyList<RpChatMessage>>.Fail(r.Code, r.Message);
        var src = r.Data is System.Collections.IEnumerable and not string ? r.Data : Reflect.P(r.Data, "Messages", "Chat");
        IReadOnlyList<RpChatMessage> list = Reflect.Items(src).Select(ChatFrom).Where(x => x is not null).Select(x => x!).ToList();
        return RpResult<IReadOnlyList<RpChatMessage>>.Success(list);
    }

    public async Task<RpResult> SendTeamMessageAsync(string text, CancellationToken ct)
    {
        var r = await CallAsync(new[] { "SendTeamMessageAsync", "SendTeamChatMessageAsync", "SendTeamChatAsync" }, ct, text);
        return r.Ok ? RpResult.Success : RpResult.Fail(r.Code, r.Message);
    }

    public async Task<RpResult<RpDeviceState>> GetDeviceAsync(ulong entityId, RpDeviceKind kind, CancellationToken ct)
    {
        var names = kind switch
        {
            RpDeviceKind.SmartSwitch => new[] { "GetSmartSwitchInfoAsync", "GetSmartDeviceInfoAsync", "GetEntityInfoAsync" },
            RpDeviceKind.SmartAlarm => new[] { "GetAlarmInfoAsync", "GetSmartAlarmInfoAsync", "GetSmartDeviceInfoAsync", "GetEntityInfoAsync" },
            _ => new[] { "GetStorageMonitorInfoAsync", "GetEntityInfoAsync" }
        };
        var call = await CallAsync(names, ct, entityId);
        // Строгие методы библиотеки проверяют тип сущности (ClientMappingFailed): тогда пробуем типонезависимый запрос.
        if (!call.Ok && call.Found && kind != RpDeviceKind.StorageMonitor)
        {
            var generic = await CallAsync(new[] { "GetSmartDeviceInfoAsync" }, ct, entityId);
            if (generic.Ok) call = generic;
        }
        T($"Rust+: чтение устройства {entityId} ({kind}): {(call.Ok ? "ok" : (call.Code + " " + call.Message))}");
        return Wrap(call, d => d is null ? null : new RpDeviceState(kind, Reflect.B(Reflect.P(d, "IsActive", "Value", "IsOn", "Active")),
            Reflect.Items(Reflect.P(d, "Items")).Select(i => new RpItem(Reflect.I(Reflect.P(i, "ItemId", "Id")) ?? 0, Reflect.I(Reflect.P(i, "Quantity", "Amount")) ?? 0, Reflect.S(Reflect.P(i, "Name", "ShortName")))).ToList(),
            Reflect.B(Reflect.P(d, "HasProtection"))));
    }

    public async Task<RpResult> SetSwitchAsync(ulong entityId, bool on, CancellationToken ct)
    {
        T($"Rust+: отправка SetSmartSwitchValue entity={entityId} value={(on ? "вкл" : "выкл")}");
        var r = await CallAsync(new[] { "SetSmartSwitchValueAsync", "SetSmartSwitchAsync", "SetEntityValueAsync" }, ct, entityId, on);
        if (!r.Found) r = await CallAsync(on ? new[] { "TurnSmartSwitchOnAsync", "TurnOnSmartSwitchAsync" } : new[] { "TurnSmartSwitchOffAsync", "TurnOffSmartSwitchAsync" }, ct, entityId);
        if (!r.Found) return RpResult.Fail("api_mismatch", "В библиотеке не найден метод управления переключателем." + Probe("Switch", "Entity"));
        T($"Rust+: ответ на SetSmartSwitchValue entity={entityId}: {(r.Ok ? "ok" : r.Code + " " + r.Message)}");
        return r.Ok ? RpResult.Success : RpResult.Fail(r.Code, r.Message);
    }

    public event Action<string>? Trace;
    private void T(string m) { try { Trace?.Invoke(m); } catch (Exception) { } }

    private static RpDeviceKind KindOf(object d) =>
        Reflect.P(d, "HasProtection") is not null || Reflect.P(d, "Capacity") is not null || Reflect.Items(Reflect.P(d, "Items")).Any()
            ? RpDeviceKind.StorageMonitor : RpDeviceKind.SmartSwitch;

    private void SubscribeAll()
    {
        var events = _type!.GetEvents(BindingFlags.Public | BindingFlags.Instance);
        // OnEntityChanged — единственный надёжный канал: приходит для каждой широковещательной рассылки с Id и Value.
        // OnSmartDeviceTriggered/OnStorageMonitorTriggered — эвристика поверх него, поэтому при наличии OnEntityChanged они игнорируются (иначе дубли).
        var hasEntityChanged = events.Any(e => e.Name == "OnEntityChanged");
        var owner = _client;
        foreach (var ev in events)
        {
            var name = ev.Name; var lower = name.ToLowerInvariant();
            Reflect.Subscribe(_client!, ev, (sender, args) =>
            {
                try
                {
                    if (!ReferenceEquals(sender, owner) && sender is not null && !ReferenceEquals(_client, sender)) return; // событие старого клиента
                    if (name == "OnTeamChatReceived" || (name != "OnClanChatReceived" && lower.Contains("chat") && !lower.Contains("clan")))
                    {
                        var m = ChatFrom(args);
                        if (m is null) { T($"Team Chat: событие {name} без текста"); Raw(name, args); return; }
                        T($"Team Chat: получено сообщение от {m.Name} ({m.SteamId}): «{m.Text}»");
                        ChatReceived?.Invoke(m);
                    }
                    else if (lower.Contains("clan")) { /* клановый чат не является командным чатом команды */ }
                    else if (name == "OnEntityChanged" || (!hasEntityChanged && (lower.Contains("switch") || lower.Contains("alarm") || lower.Contains("storage") || lower.Contains("entity") || lower.Contains("smartdevice"))))
                    {
                        if (hasEntityChanged && name != "OnEntityChanged") return;
                        var d = Reflect.P(args, "Data") ?? args;
                        var id = Reflect.U(Reflect.P(d, "Id", "EntityId"));
                        if (id is not { } eid) { Raw(name, args); return; }
                        var kind = lower.Contains("alarm") ? RpDeviceKind.SmartAlarm : lower.Contains("storage") ? RpDeviceKind.StorageMonitor : KindOf(d!);
                        var on = Reflect.B(Reflect.P(d, "IsActive", "Value", "IsOn", "Active"));
                        T($"Rust+: EntityChanged id={eid} value={(on is null ? "—" : on.Value ? "вкл" : "выкл")}");
                        DeviceChanged?.Invoke(eid, new RpDeviceState(kind, on,
                            kind == RpDeviceKind.StorageMonitor ? Reflect.Items(Reflect.P(d, "Items")).Select(i => new RpItem(Reflect.I(Reflect.P(i, "ItemId", "Id")) ?? 0, Reflect.I(Reflect.P(i, "Quantity", "Amount")) ?? 0, Reflect.S(Reflect.P(i, "Name", "ShortName")))).ToList() : null,
                            kind == RpDeviceKind.StorageMonitor ? Reflect.B(Reflect.P(d, "HasProtection")) : null));
                    }
                    else if (name is "OnSmartDeviceTriggered" or "OnStorageMonitorTriggered") { /* дубль OnEntityChanged */ }
                    else if (name == "Disconnected" || lower is "closed" or "connectionlost")
                    {
                        T("Rust+: соединение закрыто (" + name + ")");
                        ConnectionLost?.Invoke(name + ": " + Reflect.S(Reflect.P(args, "Message", "Reason", "Exception")));
                    }
                    else if (lower.Contains("error")) T($"Rust+: {name}: {Reflect.S(Reflect.P(args, "Message", "Exception")) ?? args?.ToString()}");
                    else if (lower.Contains("disconnect")) { /* Disconnecting — результат нашего же закрытия */ }
                    else Raw(name, args);
                }
                catch (Exception ex) { T($"Rust+: ошибка обработчика {name}: {ex.Message}"); }
            });
        }
    }

    private void Raw(string name, object? args)
    {
        var props = args?.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Take(12)
            .Select(p => { try { var v = p.GetValue(args); return $"{p.Name}={(v is string or ValueType ? v : v?.GetType().Name)}"; } catch (Exception) { return p.Name; } }) ?? Enumerable.Empty<string>();
        lock (_raw) { _raw.Add($"{name}({string.Join(", ", props)})"); if (_raw.Count > 50) _raw.RemoveAt(0); }
    }

    public async ValueTask DisposeAsync()
    {
        var c = _client; _client = null;
        try { if (c is IAsyncDisposable ad) await ad.DisposeAsync(); else if (c is IDisposable d) d.Dispose(); } catch (Exception) { }
    }
}
