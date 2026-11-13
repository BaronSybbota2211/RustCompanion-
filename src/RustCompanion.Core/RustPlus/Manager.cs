using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

/// <summary>
/// Управление соединением Rust+: подключение, опрос состояния, переподключение с нарастающей паузой, остановка при недействительной авторизации.
/// Ничего не имитирует: состояние «Подключено» выставляется только после успешного соединения и ответа сервера.
/// </summary>
public sealed class RustPlusManager : IAsyncDisposable
{
    public const int MaxMessageLength = 128;                     // консервативное ограничение длины сообщения командного чата (точный лимит не подтверждён)
    /// <summary>Пауза между попытками, когда Facepunch ответил HTTP 418 (прямое подключение закрыто): быстрые повторы бесполезны и только нагружают сервер.</summary>
    public static readonly TimeSpan DirectRemovedPause = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    private readonly Func<IRustPlusGateway> _factory; private readonly PairedServerRepository _servers; private readonly IClock _clock;
    private readonly IEventNotifier _notifier; private readonly ISettingsProvider _settings; private readonly Func<TimeSpan, CancellationToken, Task> _delay; private readonly DiagnosticsService? _diag;
    private CancellationTokenSource? _cts; private Task? _loop; private IRustPlusGateway? _gw; private TaskCompletionSource? _lost;
    private readonly HashSet<string> _seenChat = new(); private readonly Queue<string> _seenOrder = new(); private readonly object _lock = new();

    public RustPlusState State { get; private set; } = RustPlusState.Disconnected;
    public string StatusMessage { get; private set; } = "Не подключено";
    public PairedServer? Server { get; private set; }
    public int Attempt { get; private set; }
    public RustPlusSnapshot Snapshot { get; } = new();
    public event Action? Changed;
    public event Action<RpChatMessage>? ChatMessage;
    public event Action<PairedServer, RpDeviceChange>? DeviceChanged;
    public bool IsConnected => State == RustPlusState.Connected;

    public RustPlusManager(Func<IRustPlusGateway> factory, PairedServerRepository servers, IClock clock, IEventNotifier notifier, ISettingsProvider settings,
        DiagnosticsService? diag = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _factory = factory; _servers = servers; _clock = clock; _notifier = notifier; _settings = settings; _diag = diag; _delay = delay ?? ((t, ct) => Task.Delay(t, ct)); }

    private void Set(RustPlusState s, string msg) { State = s; StatusMessage = msg; Changed?.Invoke(); }

    public async Task<RpResult> ConnectAsync(long serverId)
    {
        await DisconnectAsync();
        var server = (await _servers.ListAsync()).FirstOrDefault(s => s.Id == serverId);
        if (server is null) return RpResult.Fail("not_found", "Сервер не найден (возможно, удалён).");
        var token = _servers.GetToken(serverId);
        if (token is null) { Set(RustPlusState.AuthFailed, "Нет сохранённого токена — требуется повторное сопряжение."); return RpResult.Fail("no_token", StatusMessage); }
        Server = server; Attempt = 0; lock (_lock) { _seenChat.Clear(); _seenOrder.Clear(); }
        _cts = new CancellationTokenSource();
        var cred = _servers.BuildCredentials(server) ?? new RpCredentials(server.Ip, server.Port, server.PlayerId, token.Value);
        _loop = Task.Run(() => RunAsync(server, cred, _cts.Token));
        return RpResult.Success;
    }

    public async Task DisconnectAsync()
    {
        var cts = _cts; var loop = _loop; _cts = null; _loop = null;
        if (cts is not null) { cts.Cancel(); }
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        cts?.Dispose();
        var gw = _gw; _gw = null;
        if (gw is not null) { try { await gw.DisposeAsync(); } catch (Exception) { } }
        if (State != RustPlusState.Disconnected) Set(RustPlusState.Disconnected, "Отключено");
    }

    private void Wire(IRustPlusGateway gw, PairedServer server)
    {
        gw.Trace += t => Log(t);
        gw.ChatReceived += m => OnChat(m);
        gw.DeviceChanged += (id, st) => DeviceChanged?.Invoke(server, new RpDeviceChange(id, st, _clock.UtcNow));
        gw.ConnectionLost += reason => { _diag?.Record("Rust+", SourceState.NoResponse, "Соединение потеряно: " + reason); _lost?.TrySetResult(); };
    }

    /// <summary>Диагностическая запись Rust+ (секреты вычищаются DiagnosticsService).</summary>
    public void Log(string message) => _diag?.Record("Rust+", SourceState.Ok, message);

    private readonly Dictionary<string, DateTimeOffset> _recentChat = new();

    private bool MarkSeen(string key)
    {
        lock (_lock)
        {
            if (!_seenChat.Add(key)) return false;
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > 500) _seenChat.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private void OnChat(RpChatMessage m)
    {
        var server = Server;
        // Сообщение, пришедшее событием без времени, и то же сообщение из опроса чата (со временем) — одно и то же: ключ с временем их бы разделил.
        var loose = $"{m.SteamId}|{m.Text}"; var now = _clock.UtcNow;
        lock (_lock)
        {
            if (_recentChat.TryGetValue(loose, out var at) && now - at < TimeSpan.FromSeconds(4) && !_seenChat.Contains(m.Key) && (m.AtUtc is null || _recentNoTime.Contains(loose)))
            { Log($"Team Chat: дубль сообщения «{m.Text}» пропущен"); return; }
            if (_recentChat.Count > 200) { _recentChat.Clear(); _recentNoTime.Clear(); }
            if (m.AtUtc is null) _recentNoTime.Add(loose); else _recentNoTime.Remove(loose);
            _recentChat[loose] = now;
        }
        if (!MarkSeen(m.Key)) { Log($"Team Chat: повтор сообщения от {m.Name} пропущен"); return; }
        Log($"Team Chat: сообщение от {m.Name} принято к обработке (сервер «{server?.Name}», подключено={IsConnected}): «{m.Text}»");
        ChatMessage?.Invoke(m);
    }
    private readonly HashSet<string> _recentNoTime = new();

    private async Task RunAsync(PairedServer server, RpCredentials cred, CancellationToken ct)
    {
        var attempt = 0; var everConnected = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Attempt = attempt;
                Set(attempt == 0 ? RustPlusState.Connecting : RustPlusState.Reconnecting, attempt == 0 ? "Подключение…" : $"Переподключение (попытка {attempt})…");
                var gw = _factory(); _gw = gw; _lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Wire(gw, server);
                // Данные подключения перечитываются на каждой попытке: за паузу пользователь мог войти через Steam заново или повторно сопрячь сервер (появились токен входа / ID сервера).
                try { cred = await _servers.ResolveCredentialsAsync(server.Id, ct) ?? cred; } catch (OperationCanceledException) { throw; } catch (Exception) { }
                var conn = await gw.ConnectAsync(cred, ct);
                if (!conn.Ok)
                {
                    _diag?.Record("Rust+", SourceState.Error, $"{conn.Code} {conn.Message}");
                    await SafeDispose(gw);
                    conn = conn with { Message = global::RustCompanion.Core.Net.Redactor.Redact(conn.Message) };        // в статус для пользователя секреты не попадают, даже если шлюз процитировал их в ответе
                    if (RpErrors.IsCompanionLoginRejected(conn)) { Set(RustPlusState.AuthFailed, conn.Message ?? "Facepunch отклонил вход в Rust+. Выполните вход через Steam на вкладке «Rust+»."); return; }
                    if (RpErrors.IsCompanionServerUnknown(conn)) { Set(RustPlusState.AuthFailed, conn.Message ?? "Facepunch не знает этот сервер. Сопрягите сервер в игре ещё раз."); return; }
                    if (RpErrors.IsDirectRemoved(conn))
                    {
                        // Это не ошибка авторизации: сопряжение в порядке, закрыт сам канал. Повторяем редко, без сброса сопряжения.
                        attempt++;
                        Set(RustPlusState.Reconnecting, $"{conn.Message} Повтор через {DirectRemovedPause.TotalMinutes:0} мин.");
                        await _delay(DirectRemovedPause, ct);
                        continue;
                    }
                    if (conn.Code != RpErrors.CompanionNotReady && RpErrors.IsAuth(conn)) { Set(RustPlusState.AuthFailed, "Сервер отклонил авторизацию. Сопряжение устарело — выполните его заново."); return; }
                    attempt++;
                    Set(RustPlusState.Reconnecting, $"Нет соединения: {conn.Message}. Повтор через {Pause(attempt).TotalSeconds:0} с");
                    await _delay(Pause(attempt), ct);
                    continue;
                }

                var first = true; attempt = 0;
                while (!ct.IsCancellationRequested)
                {
                    var refresh = await RefreshAsync(gw, first, ct);
                    if (!refresh.Ok)
                    {
                        // Соединение открыто, но getInfo вернул «not_found»: сервер не принял токен игрока — нужно повторное сопряжение (повторы бесполезны).
                        if (first && RpErrors.IsPlayerTokenRejected(refresh)) { await SafeDispose(gw); Set(RustPlusState.AuthFailed, "Сервер не принял токен игрока (ответ not_found). Сопрягите сервер в игре заново: Esc → Rust+ → Pair with server."); return; }
                        if (RpErrors.IsAuth(refresh)) { await SafeDispose(gw); Set(RustPlusState.AuthFailed, "Сервер отклонил авторизацию. Сопряжение устарело — выполните его заново."); return; }
                        _diag?.Record("Rust+", SourceState.NoResponse, $"{refresh.Code} {refresh.Message}");
                        break;                                                        // сеть/сервер: выходим во внешний цикл → переподключение
                    }
                    if (first)
                    {
                        first = false; everConnected = true;
                        await _servers.TouchConnectedAsync(server.Id, _clock.UtcNow, ct);
                        Set(RustPlusState.Connected, "Подключено");
                    }
                    var wait = _delay(TimeSpan.FromSeconds(Math.Max(5, _settings.Current.RustPlusPollSeconds)), ct);
                    var done = await Task.WhenAny(wait, _lost!.Task);
                    if (done == _lost.Task) break;
                }
                await SafeDispose(gw);
                if (ct.IsCancellationRequested) break;
                if (everConnected) _notifier.Raise(NotifyEventIds.RustPlusConnection, $"rp-lost:{server.Id}:{_clock.UtcNow.Ticks / TimeSpan.TicksPerMinute}", "Rust+", $"соединение с «{server.Name}» потеряно, выполняется переподключение");
                attempt++;
                Set(RustPlusState.Reconnecting, $"Соединение потеряно. Повтор через {Pause(attempt).TotalSeconds:0} с");
                await _delay(Pause(attempt), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static TimeSpan Pause(int attempt) => Backoff[Math.Min(Math.Max(attempt, 1), Backoff.Length) - 1];

    private async Task SafeDispose(IRustPlusGateway gw) { try { await gw.DisposeAsync(); } catch (Exception) { } if (ReferenceEquals(_gw, gw)) _gw = null; }

    private async Task<RpResult> RefreshAsync(IRustPlusGateway gw, bool first, CancellationToken ct)
    {
        var info = await gw.GetInfoAsync(ct);
        if (!info.Ok) { Snapshot.InfoError = info.Message; return info.AsPlain(); }
        Snapshot.Info = info.Value; Snapshot.InfoError = null;
        var time = await gw.GetTimeAsync(ct); if (time.Ok) Snapshot.Time = time.Value;
        var team = await gw.GetTeamAsync(ct); if (team.Ok) Snapshot.Team = team.Value;
        var chat = await gw.GetTeamChatAsync(ct);
        if (chat.Ok && chat.Value is not null)
            foreach (var m in chat.Value.OrderBy(x => x.AtUtc))
            {
                if (first) MarkSeen(m.Key);                    // история чата на момент подключения не считается новыми сообщениями (бот не исполняет старые команды)
                else OnChat(m);
            }
        Snapshot.UpdatedUtc = _clock.UtcNow;
        Changed?.Invoke();
        return RpResult.Success;
    }

    public async Task<RpResult> SendMessageAsync(string text, CancellationToken ct = default)
    {
        var gw = _gw;
        if (gw is null || !IsConnected) return RpResult.Fail("not_connected", "Нет соединения с Rust+.");
        text = (text ?? "").Trim();
        if (text.Length == 0) return RpResult.Fail("empty", "Пустое сообщение.");
        if (text.Length > MaxMessageLength) text = text[..(MaxMessageLength - 1)] + "…";
        var r = await gw.SendTeamMessageAsync(text, ct);
        if (RpErrors.IsAuth(r)) Set(RustPlusState.AuthFailed, "Сервер отклонил авторизацию. Выполните сопряжение заново.");
        return r;
    }

    /// <summary>Лёгкая проверка связи с сервером (запрос информации о сервере). Нужна, чтобы отличать потерю связи с сервером от недоступности конкретного устройства.</summary>
    public async Task<RpResult> PingAsync(CancellationToken ct = default)
    {
        var gw = _gw;
        if (gw is null || !IsConnected) return RpResult.Fail("not_connected", "Нет соединения с Rust+.");
        var r = await gw.GetInfoAsync(ct);
        return r.AsPlain();
    }

    /// <summary>Свежий состав команды с позициями (для карты); при ошибке — null, остаётся прежний снимок.</summary>
    public async Task<RpTeam?> GetTeamFreshAsync(CancellationToken ct = default)
    {
        var gw = _gw; if (gw is null || !IsConnected) return null;
        var r = await gw.GetTeamAsync(ct);
        if (r.Ok && r.Value is not null) { Snapshot.Team = r.Value; return r.Value; }
        return null;
    }

    private RpMap? _map; private long _mapServerId;
    /// <summary>Карта текущего сервера (картинка не меняется до вайпа, поэтому кэшируется).</summary>
    public async Task<RpResult<RpMap>> GetMapAsync(CancellationToken ct = default)
    {
        var gw = _gw; var srv = Server;
        if (gw is null || srv is null || !IsConnected) return RpResult<RpMap>.Fail("not_connected", "Нет соединения с Rust+.");
        if (_map is not null && _mapServerId == srv.Id) return RpResult<RpMap>.Success(_map);
        var r = await gw.GetMapAsync(ct);
        if (r.Ok && r.Value is not null) { _map = r.Value.MapSize > 0 ? r.Value : r.Value with { MapSize = Snapshot.Info?.MapSize ?? 0 }; _mapServerId = srv.Id; return RpResult<RpMap>.Success(_map); }
        Log("Карта: " + (r.Message ?? r.Code));
        return r;
    }

    public async Task<RpResult<IReadOnlyList<RpMapMarker>>> GetMapMarkersAsync(CancellationToken ct = default)
    {
        var gw = _gw;
        if (gw is null || !IsConnected) return RpResult<IReadOnlyList<RpMapMarker>>.Fail("not_connected", "Нет соединения с Rust+.");
        return await gw.GetMapMarkersAsync(ct);
    }

    public async Task<RpResult<RpDeviceState>> ReadDeviceAsync(ulong entityId, RpDeviceKind kind, CancellationToken ct = default)
    {
        var gw = _gw;
        if (gw is null || !IsConnected) return RpResult<RpDeviceState>.Fail("not_connected", "Нет соединения с Rust+.");
        return await gw.GetDeviceAsync(entityId, kind, ct);
    }

    /// <summary>Переключение с проверкой: успех только если повторное чтение показало нужное состояние. serverId — сервер, которому принадлежит устройство: чужому подключению команда не отправляется.</summary>
    public async Task<RpResult> SetSwitchVerifiedAsync(ulong entityId, bool on, CancellationToken ct = default, long? serverId = null)
    {
        var gw = _gw;
        if (gw is null || !IsConnected) { Log($"Switch: entity={entityId} — нет соединения, команда не отправлена"); return RpResult.Fail("not_connected", "Нет соединения с Rust+."); }
        if (serverId is { } sid && Server?.Id != sid)
        { Log($"Switch: entity={entityId} принадлежит серверу #{sid}, активное подключение — #{Server?.Id}; команда не отправлена"); return RpResult.Fail("wrong_server", "Устройство принадлежит другому серверу, а подключение активно к «" + Server?.Name + "». Подключитесь к его серверу."); }
        Log($"Switch: команда {(on ? "ВКЛ" : "ВЫКЛ")} entity={entityId} сервер=«{Server?.Name}»");
        var sent = await gw.SetSwitchAsync(entityId, on, ct);
        if (!sent.Ok) { Log($"Switch: entity={entityId} ошибка {sent.Code}: {sent.Message}"); return RpResult.Fail(sent.Code, "Команда не выполнена: " + sent.Message); }
        for (var i = 0; i < 3; i++)
        {
            await _delay(TimeSpan.FromMilliseconds(400), ct);
            var read = await gw.GetDeviceAsync(entityId, RpDeviceKind.SmartSwitch, ct);
            if (!read.Ok || read.Value?.IsOn is null) { if (i == 2) { Log($"Switch: entity={entityId} состояние не подтверждено ({read.Code} {read.Message})"); return RpResult.Fail("verify_failed", "Команда отправлена, но состояние подтвердить не удалось."); } continue; }
            if (read.Value.IsOn == on) { Log($"Switch: entity={entityId} подтверждено: {(on ? "вкл" : "выкл")}"); return RpResult.Success with { Message = on ? "Включено (подтверждено)" : "Выключено (подтверждено)" }; }
        }
        Log($"Switch: entity={entityId} устройство не изменило состояние");
        return RpResult.Fail("unconfirmed", "Команда отправлена, но устройство не изменило состояние.");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
