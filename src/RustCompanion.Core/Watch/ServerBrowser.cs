using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;

namespace RustCompanion.Core.Watch;

/// <summary>Команда подключения Rust. Формируется только из валидного IP/hostname и порта, полученных из данных сервера.</summary>
public static class ConnectCommand
{
    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$", RegexOptions.Compiled);
    public const string Unavailable = "Адрес подключения недоступен";

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3 && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast);
        return HostRx.IsMatch(host);
    }

    public static bool IsValidPort(int? port) => port is >= 1 and <= 65535;

    /// <summary>null — команду показывать нельзя (нет адреса/порта или они не прошли проверку; IPv6 не поддерживается форматом ip:port).</summary>
    public static string? Build(string? host, int? port) =>
        IsValidHost(host) && IsValidPort(port) ? $"net.connect {host!.Trim()}:{port}" : null;
}

public interface IClipboardService { void SetText(string text); }

public sealed class ConnectCopier
{
    private readonly IClipboardService _clipboard;
    public ConnectCopier(IClipboardService clipboard) { _clipboard = clipboard; }

    public string Copy(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return ConnectCommand.Unavailable;
        try { _clipboard.SetText(command); return "Команда скопирована"; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException)
        { return "Не удалось скопировать: буфер обмена занят. Повторите."; }
    }
}

public enum SearchPhase { Idle, Loading, Results, Empty, Error }

public sealed record ServerSearchState(SearchPhase Phase, string Query, IReadOnlyList<FoundServer> Results, string? Message, SourceState? ErrorKind)
{
    public static readonly ServerSearchState Initial = new(SearchPhase.Idle, "", Array.Empty<FoundServer>(), null, null);
}

/// <summary>
/// Поиск серверов по названию с debounce, отменой устаревших запросов и защитой от гонок: ответ на более раннее обращение никогда не перезапишет более новое.
/// «Ничего не найдено» (Empty) и «ошибка API» (Error) — разные состояния.
/// </summary>
public sealed class ServerSearchController
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);
    public const int MinQueryLength = 2;
    private readonly IServerQuerySource _source; private readonly TimeSpan _debounce;
    private readonly object _lock = new();
    private long _version; private CancellationTokenSource? _cts;
    public ServerSearchState State { get; private set; } = ServerSearchState.Initial;
    public event Action<ServerSearchState>? Changed;
    public int RequestsStarted { get; private set; }

    public ServerSearchController(IServerQuerySource source, TimeSpan? debounce = null) { _source = source; _debounce = debounce ?? DefaultDebounce; }

    private void Publish(long version, ServerSearchState s)
    {
        lock (_lock) { if (version != _version) return; State = s; }
        Changed?.Invoke(s);
    }

    public Task SetQueryAsync(string? query)
    {
        var q = (query ?? "").Trim();
        long v; CancellationToken token;
        lock (_lock)
        {
            _version++; v = _version; _cts?.Cancel(); _cts = new CancellationTokenSource(); token = _cts.Token;
        }
        if (q.Length < MinQueryLength) { Publish(v, ServerSearchState.Initial with { Query = q }); return Task.CompletedTask; }
        Publish(v, new ServerSearchState(SearchPhase.Loading, q, Array.Empty<FoundServer>(), null, null));
        return RunAsync(v, q, token);
    }

    public Task RetryAsync() => SetQueryAsync(State.Query);

    private async Task RunAsync(long v, string q, CancellationToken token)
    {
        try
        {
            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, token);          // быстрый ввод: сработает только последнее обращение
            lock (_lock) { if (v != _version) return; RequestsStarted++; }
            var r = await _source.SearchServersAsync(q, token);
            if (r.IsOk && r.Value is not null)
            {
                var list = r.Value.DistinctBy(s => s.Id).ToList();                        // только реальные результаты API
                Publish(v, list.Count == 0
                    ? new ServerSearchState(SearchPhase.Empty, q, list, "Серверы не найдены", null)
                    : new ServerSearchState(SearchPhase.Results, q, list, null, null));
            }
            else Publish(v, new ServerSearchState(SearchPhase.Error, q, Array.Empty<FoundServer>(), r.Message, r.State));
        }
        catch (OperationCanceledException) { /* запрос устарел */ }
    }
}

public enum PlayersPhase { None, Loading, Ready, Empty, Unavailable, Error }

public sealed record ServerPlayersState(string? ServerId, PlayersPhase Phase, IReadOnlyList<OnlinePlayer> Players, int? ReportedPlayers,
    bool Partial, DateTimeOffset? LastSuccessUtc, string? Message, bool Stale)
{
    public static readonly ServerPlayersState None = new(null, PlayersPhase.None, Array.Empty<OnlinePlayer>(), null, false, null, null, false);
}

/// <summary>Игроки выбранного сервера. При смене сервера список сбрасывается сразу; поздний ответ прежнего сервера отбрасывается.</summary>
public sealed class ServerPlayersController
{
    private readonly IServerQuerySource _source; private readonly IClock _clock; private readonly Func<TimeSpan> _staleAfter;
    private readonly object _lock = new();
    private long _version; private CancellationTokenSource? _cts;
    public ServerPlayersState State { get; private set; } = ServerPlayersState.None;
    public FoundServer? Server { get; private set; }
    public event Action<ServerPlayersState>? Changed;

    public ServerPlayersController(IServerQuerySource source, IClock clock, Func<TimeSpan>? staleAfter = null)
    { _source = source; _clock = clock; _staleAfter = staleAfter ?? (() => TimeSpan.FromMinutes(3)); }

    private void Publish(long v, ServerPlayersState s)
    {
        lock (_lock) { if (v != _version) return; State = s; }
        Changed?.Invoke(s);
    }

    public Task SelectServerAsync(string serverKey, CancellationToken ct = default)
    {
        long v; CancellationToken token;
        lock (_lock)
        {
            _version++; v = _version; _cts?.Cancel(); _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); token = _cts.Token;
            Server = null;
            State = new ServerPlayersState(serverKey, PlayersPhase.Loading, Array.Empty<OnlinePlayer>(), null, false, null, "Загрузка…", false); // игроки прежнего сервера не остаются
        }
        Changed?.Invoke(State);
        return LoadAsync(v, serverKey, token, keepPrevious: false);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        string? id; long v; CancellationToken token;
        lock (_lock) { id = State.ServerId; if (id is null) return Task.CompletedTask; _version++; v = _version; _cts?.Cancel(); _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); token = _cts.Token; }
        return LoadAsync(v, id, token, keepPrevious: true);
    }

    private async Task LoadAsync(long v, string serverId, CancellationToken token, bool keepPrevious)
    {
        var prev = State;
        try
        {
            if (keepPrevious) Publish(v, prev with { Phase = PlayersPhase.Loading });
            var r = await _source.GetServerPlayersAsync(serverId, token);
            if (r.IsOk && r.Value is { } snap)
            {
                lock (_lock) { if (v == _version) Server = snap.Server; }
                var reported = snap.Server.Players;
                if (!snap.ListProvided)
                {
                    Publish(v, new ServerPlayersState(serverId, PlayersPhase.Unavailable, Array.Empty<OnlinePlayer>(), reported, false, _clock.UtcNow,
                        snap.Note ?? "Сервер не отдаёт список игроков по Steam Query. Известно только общее число игроков.", false));
                    return;
                }
                if (snap.Players.Count == 0)
                {
                    Publish(v, new ServerPlayersState(serverId, PlayersPhase.Empty, snap.Players, reported, false, _clock.UtcNow, "На сервере сейчас нет игроков.", false));
                    return;
                }
                var partial = snap.ListStatus == PlayerListStatus.Partial || (reported is int n && snap.Players.Count < n);
                Publish(v, new ServerPlayersState(serverId, PlayersPhase.Ready, snap.Players.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), reported, partial, _clock.UtcNow,
                    partial ? (snap.Note ?? $"Список может быть неполным: получено {snap.Players.Count} из {reported}.") : null, false));
            }
            else
            {
                var keep = keepPrevious && prev.ServerId == serverId ? prev.Players : Array.Empty<OnlinePlayer>();
                Publish(v, new ServerPlayersState(serverId, PlayersPhase.Error, keep, prev.ReportedPlayers, prev.Partial, keepPrevious ? prev.LastSuccessUtc : null, r.Message, keep.Count > 0));
            }
        }
        catch (OperationCanceledException) { /* устарело: выбран другой сервер или новое обновление */ }
    }

    /// <summary>Данные считаются устаревшими, если последнее успешное обновление старше допустимого.</summary>
    public bool IsStale(DateTimeOffset now) => State.LastSuccessUtc is { } t && now - t > _staleAfter();
}

public sealed record SessionRow(DateTimeOffset StartUtc, DateTimeOffset? StopUtc, TimeSpan? Duration, bool NotEndedPerSource);

public static class SessionView
{
    /// <summary>Строки истории сессий. Длительность считается только по реальным start/stop; незавершённая сессия помечается, а не выдумывается.</summary>
    public static IReadOnlyList<SessionRow> Build(IEnumerable<PresenceSession> sessions, DateTimeOffset now) =>
        sessions.OrderByDescending(s => s.Start)
            .Select(s => new SessionRow(s.Start, s.Stop, s.Stop is null ? ActivityStats.ActiveElapsed(s.Start, now) : ActivityStats.Duration(s.Start, s.Stop), s.Stop is null))
            .ToList();
}

public sealed record RecentServer(string SourceKey, string Name, string? Ip, int? Port, string? Map, string? Country, DateTimeOffset SelectedAtUtc);

public sealed class RecentServersRepository
{
    private readonly Database _db;
    public RecentServersRepository(Database db) { _db = db; }

    public Task TouchAsync(FoundServer s, DateTimeOffset at, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO recent_servers(source_key,name,ip,port,map,country,selected_at) VALUES(@i,@n,@ip,@p,@m,@c,@a)
                ON CONFLICT(source_key) DO UPDATE SET name=@n,ip=@ip,port=@p,map=@m,country=@c,selected_at=@a",
                ("@i", s.Id), ("@n", s.Name), ("@ip", s.Ip), ("@p", s.Port), ("@m", s.Map), ("@c", s.Country), ("@a", Sql.Iso(at)));
            cmd.ExecuteNonQuery();
            using var trim = Sql.Cmd(c, "DELETE FROM recent_servers WHERE source_key NOT IN (SELECT source_key FROM recent_servers ORDER BY selected_at DESC LIMIT 20)");
            trim.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<RecentServer>> ListAsync(int limit = 10, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RecentServer>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT source_key,name,ip,port,map,country,selected_at FROM recent_servers ORDER BY selected_at DESC LIMIT @l", ("@l", limit));
            using var r = cmd.ExecuteReader();
            var l = new List<RecentServer>();
            while (r.Read()) l.Add(new RecentServer(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Sql.ParseIso(r.GetValue(6))!.Value));
            return l;
        }, ct);
}
