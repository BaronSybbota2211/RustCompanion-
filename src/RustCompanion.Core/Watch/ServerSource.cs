using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Watch;

public interface IServerQuerySource
{
    Task<SourceResult<IReadOnlyList<FoundServer>>> SearchServersAsync(string query, CancellationToken ct);
    Task<SourceResult<IReadOnlyList<FoundPlayer>>> SearchPlayersAsync(string query, string? serverKey, CancellationToken ct);
    Task<SourceResult<PresenceObservation>> CheckPresenceAsync(string playerKey, string serverKey, CancellationToken ct);
    Task<SourceResult<IReadOnlyList<PresenceSession>>> GetSessionsAsync(string playerKey, string? serverKey, CancellationToken ct);
    Task<SourceResult<ServerSnapshot>> GetServerPlayersAsync(string serverKey, CancellationToken ct);
    /// <summary>Сведения о сервере (онлайн, вайп, пинг, теги) без списка игроков.</summary>
    Task<SourceResult<ServerDetails>> GetServerDetailsAsync(string serverKey, CancellationToken ct) =>
        Task.FromResult(SourceResult<ServerDetails>.Fail(SourceState.Error, "Не поддерживается."));
}

/// <summary>Источник, умеющий кратко описать последний сетевой запрос (адрес без секретов, результат, время) — для диагностики в интерфейсе.</summary>
public interface IDiagnosableSource { string LastRequestInfo { get; } }

/// <summary>Ключ сервера: «хост:порт запросов A2S». Порт запросов может отличаться от игрового.</summary>
public static class ServerKey
{
    private static readonly Regex Rx = new(@"^(?<host>[A-Za-z0-9.\-]{1,253}):(?<port>\d{1,5})$", RegexOptions.Compiled);

    public static string Make(string host, int port) => $"{host.Trim()}:{port}";

    public static bool TryParse(string? key, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var m = Rx.Match(key.Trim());
        if (!m.Success || !int.TryParse(m.Groups["port"].Value, out port) || !ConnectCommand.IsValidPort(port)) return false;
        host = m.Groups["host"].Value;
        return ConnectCommand.IsValidHost(host);
    }
}

/// <summary>
/// Источник данных о серверах и игроках на основе Steam: поиск (Steam Web API при наличии ключа → мастер-сервер Valve по UDP → прямой адрес ip:порт)
/// и прямые запросы A2S_INFO/A2S_PLAYER к самому серверу. Платных сервисов и сторонних агрегаторов нет.
/// Игрок определяется по нику (Steam Query не передаёт SteamID). Снимки сервера кэшируются на короткое время, чтобы несколько проверок
/// (панель сервера и наблюдение) не слали серверу повторные запросы, и пишутся в локальную историю наблюдений.
/// </summary>
public sealed class A2sServerSource : IServerQuerySource, IDiagnosableSource
{
    public const int MaxSearchResults = 30;
    public const int SearchConcurrency = 6;
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(8);

    private readonly A2sQueryClient _a2s;
    private readonly SteamServerBrowser _steam;
    private readonly MasterServerClient _master;
    private readonly PresenceHistoryRepository _history;
    private readonly IClock _clock;
    private readonly DiagnosticsService? _diag;
    private readonly ConcurrentDictionary<string, (ServerSnapshot Snapshot, DateTimeOffset At)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public string LastRequestInfo { get; private set; } = "запросов ещё не было";

    public A2sServerSource(A2sQueryClient a2s, SteamServerBrowser steam, MasterServerClient master, PresenceHistoryRepository history, IClock clock, DiagnosticsService? diag = null)
    { _a2s = a2s; _steam = steam; _master = master; _history = history; _clock = clock; _diag = diag; }

    // ---------- поиск серверов ----------
    public async Task<SourceResult<IReadOnlyList<FoundServer>>> SearchServersAsync(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0) return SourceResult<IReadOnlyList<FoundServer>>.Ok(new List<FoundServer>());

        if (ServerKey.TryParse(q, out var host, out var port))              // введён адрес «ip:порт» — прямой запрос A2S_INFO
        {
            var direct = await _a2s.GetInfoAsync(host, port, ct);
            LastRequestInfo = $"A2S_INFO {host}:{port} → {(direct.IsOk ? "ответ получен" : direct.State.ToString())}";
            if (!direct.IsOk || direct.Value is null)
                return SourceResult<IReadOnlyList<FoundServer>>.Fail(direct.State, direct.Message + " Для Rust укажите адрес и порт ЗАПРОСОВ (query), он может отличаться от игрового.");
            return SourceResult<IReadOnlyList<FoundServer>>.Ok(new List<FoundServer> { ToFound(host, port, direct.Value) });
        }

        var notes = new List<string>();
        if (_steam.IsConfigured)
        {
            var web = await _steam.SearchAsync(q, ct);
            LastRequestInfo = "Steam Web API: " + _steam.LastRequestInfo;
            if (web.IsOk && web.Value is { Count: > 0 }) return web;
            notes.Add(web.IsOk ? "Steam Web API: ничего не найдено" : "Steam Web API: " + web.Message);
        }

        var masterResult = await SearchViaMasterAsync(q, ct);
        if (masterResult.IsOk && masterResult.Value is { Count: > 0 }) return masterResult;
        if (masterResult.IsOk) return masterResult;                         // пусто: корректный ответ «не найдено»
        notes.Add(masterResult.Message);
        return SourceResult<IReadOnlyList<FoundServer>>.Fail(masterResult.State, string.Join(" ", notes) +
            (_steam.IsConfigured ? "" : " Ключ Steam Web API (бесплатный, в «Настройках») даёт более надёжный поиск по названию."));
    }

    private async Task<SourceResult<IReadOnlyList<FoundServer>>> SearchViaMasterAsync(string q, CancellationToken ct)
    {
        var addresses = await _master.QueryAsync(MasterServerClient.BuildNameFilter(q), MaxSearchResults, ct);
        LastRequestInfo = $"Мастер-сервер Steam {_master.Host}:{_master.Port} → {(addresses.IsOk ? $"адресов: {addresses.Value!.Count}" : addresses.State.ToString())}";
        if (!addresses.IsOk || addresses.Value is null) return SourceResult<IReadOnlyList<FoundServer>>.Fail(addresses.State, addresses.Message);

        using var gate = new SemaphoreSlim(SearchConcurrency);
        var tasks = addresses.Value.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var info = await _a2s.GetInfoAsync(a.Host, a.Port, ct, rateLimited: false);
                return info.IsOk && info.Value is not null ? ToFound(a.Host, a.Port, info.Value) : null;
            }
            finally { gate.Release(); }
        }).ToList();
        var found = (await Task.WhenAll(tasks)).Where(s => s is not null).Select(s => s!).OrderByDescending(s => s.Players ?? 0).ToList();
        return SourceResult<IReadOnlyList<FoundServer>>.Ok(found);
    }

    private static FoundServer ToFound(string host, int queryPort, A2sInfoResponse info) =>
        new(ServerKey.Make(host, queryPort), info.Name, host, info.GamePort, info.ReportedPlayers, info.ReportedMaxPlayers, "Steam Query", info.Map, null);

    // ---------- сервер и игроки ----------
    public async Task<SourceResult<ServerDetails>> GetServerDetailsAsync(string serverKey, CancellationToken ct)
    {
        if (!ServerKey.TryParse(serverKey, out var host, out var port))
            return SourceResult<ServerDetails>.Fail(SourceState.Error, "Некорректный адрес сервера (ожидается хост:порт запросов).");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await _a2s.GetInfoAsync(host, port, ct);
        sw.Stop();
        LastRequestInfo = $"A2S_INFO {host}:{port} → {(r.IsOk ? "ответ получен" : r.State.ToString())}";
        if (!r.IsOk || r.Value is null) return SourceResult<ServerDetails>.Fail(r.State, r.Message);
        return SourceResult<ServerDetails>.Ok(ServerDetails.From(r.Value, (int)sw.ElapsedMilliseconds));
    }

    public async Task<SourceResult<ServerSnapshot>> GetServerPlayersAsync(string serverKey, CancellationToken ct)
    {
        if (!ServerKey.TryParse(serverKey, out var host, out var port))
            return SourceResult<ServerSnapshot>.Fail(SourceState.Error, "Некорректный адрес сервера (ожидается хост:порт запросов).");

        var gate = _locks.GetOrAdd(serverKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(serverKey, out var hit) && _clock.UtcNow - hit.At < SnapshotTtl)
                return SourceResult<ServerSnapshot>.Ok(hit.Snapshot);

            var r = await _a2s.GetSnapshotAsync(host, port, ct);
            LastRequestInfo = $"A2S {host}:{port} → {(r.IsOk ? r.Value!.ListStatus.Ru() : r.State.ToString())}";
            if (!r.IsOk || r.Value is null) return SourceResult<ServerSnapshot>.Fail(r.State, r.Message);

            var snap = ToSnapshot(serverKey, host, port, r.Value);
            _cache[serverKey] = (snap, _clock.UtcNow);
            await RecordHistoryAsync(serverKey, snap, ct);
            return SourceResult<ServerSnapshot>.Ok(snap);
        }
        finally { gate.Release(); }
    }

    public static ServerSnapshot ToSnapshot(string serverKey, string host, int port, A2sSnapshot s)
    {
        var info = s.Info;
        var server = info is null
            ? new FoundServer(serverKey, serverKey, host, null, s.Players.Count > 0 ? s.Players.Count : null, null, "Steam Query")
            : ToFound(host, port, info);
        var players = s.Players.Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new OnlinePlayer(p.Name, p.Name, p.HasValidDuration ? p.DurationSeconds : null)).DistinctBy(p => p.Id).ToList();
        var provided = s.ListStatus is PlayerListStatus.Provided or PlayerListStatus.Empty or PlayerListStatus.Partial;
        return new ServerSnapshot(server, provided ? players : Array.Empty<OnlinePlayer>(), provided, s.ListStatus, s.Note);
    }

    private async Task RecordHistoryAsync(string serverKey, ServerSnapshot snap, CancellationToken ct)
    {
        if (!snap.ListProvided || snap.Players.Count == 0) return;           // история пишется только по реально полученным спискам
        try { await _history.RecordSnapshotAsync(serverKey, snap.Players.Select(p => (p.Name, p.SessionSeconds)), _clock.UtcNow, PresenceHistoryRepository.DefaultMaxGap, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _diag?.Record("История наблюдений", SourceState.Error, ex.GetType().Name + ": " + ex.Message); }   // сбой записи не должен ломать опрос
    }

    public async Task<SourceResult<IReadOnlyList<FoundPlayer>>> SearchPlayersAsync(string query, string? serverKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverKey)) return SourceResult<IReadOnlyList<FoundPlayer>>.Fail(SourceState.NoData, "Steam Query показывает игроков только выбранного сервера: сначала выберите сервер.");
        var r = await GetServerPlayersAsync(serverKey, ct);
        if (!r.IsOk || r.Value is null) return SourceResult<IReadOnlyList<FoundPlayer>>.Fail(r.State, r.Message);
        if (!r.Value.ListProvided) return SourceResult<IReadOnlyList<FoundPlayer>>.Fail(SourceState.NoData, r.Value.Note ?? "Сервер не отдаёт список игроков.");
        var q = query.Trim();
        var list = r.Value.Players.Where(p => p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(20).Select(p => new FoundPlayer(p.Id, p.Name)).ToList();
        return SourceResult<IReadOnlyList<FoundPlayer>>.Ok(list);
    }

    /// <summary>Есть в списке → Present. Нет в списке → Absent только если список получен полностью (или сервер пуст); неполный/скрытый список даёт Unknown.</summary>
    public async Task<SourceResult<PresenceObservation>> CheckPresenceAsync(string playerKey, string serverKey, CancellationToken ct)
    {
        var r = await GetServerPlayersAsync(serverKey, ct);
        if (!r.IsOk || r.Value is null) return SourceResult<PresenceObservation>.Fail(r.State, r.Message);
        return SourceResult<PresenceObservation>.Ok(Evaluate(r.Value, playerKey, _clock.UtcNow));
    }

    public static PresenceObservation Evaluate(ServerSnapshot snap, string playerKey, DateTimeOffset now)
    {
        if (snap.ListProvided && snap.Players.Any(p => p.Name == playerKey)) return new PresenceObservation(PresenceKind.Present, now);
        var complete = snap.ListProvided && snap.ListStatus is PlayerListStatus.Provided or PlayerListStatus.Empty;
        return new PresenceObservation(complete ? PresenceKind.Absent : PresenceKind.Unknown, now);
    }

    /// <summary>Интервалы присутствия из локальной истории наблюдений (расчётные, по моментам опроса). Stop = null, если игрок виден в последнем опросе.</summary>
    public async Task<SourceResult<IReadOnlyList<PresenceSession>>> GetSessionsAsync(string playerKey, string? serverKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverKey)) return SourceResult<IReadOnlyList<PresenceSession>>.Fail(SourceState.NoData, "Нужен сервер, на котором наблюдался игрок.");
        var records = await _history.ListRecordsAsync(serverKey, playerKey, 50, ct);
        var now = _clock.UtcNow;
        var list = records.Select(x => new PresenceSession(x.FirstSeen, now - x.LastSeen <= PresenceHistoryRepository.DefaultMaxGap ? null : x.LastSeen, serverKey)).ToList();
        return SourceResult<IReadOnlyList<PresenceSession>>.Ok(list);
    }
}
