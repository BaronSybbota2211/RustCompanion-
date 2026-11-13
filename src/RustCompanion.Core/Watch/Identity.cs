using RustCompanion.Core.Data;
using RustCompanion.Core.Models;

namespace RustCompanion.Core.Watch;

/// <summary>
/// Три уровня достоверности личности наблюдаемого игрока. Уровни не смешиваются:
/// SteamConfirmed — Steam Web API сейчас сам сообщает сервер, на котором находится этот SteamID64 (не гарантированный источник: данные могут быть скрыты настройками приватности);
/// UserConfirmed — пользователь вручную связал ник со SteamID64 (приложение этого не проверяет);
/// Unconfirmed — игрок найден по нику в списке A2S; ники в Rust не уникальны, это НЕ подтверждённая личность.
/// </summary>
public enum IdentityLevel { Unconfirmed = 0, UserConfirmed = 1, SteamConfirmed = 2 }

public enum PlayerIndicator { Unknown, Present, Absent }

/// <summary>Результат сопоставления данных Steam с A2S-сервером. Inconclusive — Steam что-то сообщил, но однозначно связать с этим сервером нельзя.</summary>
public enum SteamServerMatch { NotChecked, OnThisServer, OnOtherServer, Inconclusive }

public static class IdentityText
{
    public static string Ru(this IdentityLevel l) => l switch
    {
        IdentityLevel.SteamConfirmed => "Подтверждено Steam",
        IdentityLevel.UserConfirmed => "Подтверждено пользователем",
        _ => "Совпадение по нику не подтверждено"
    };
}

public static class SteamMatch
{
    /// <summary>
    /// Сопоставление gameserverip из Steam («ip:игровой порт») с сервером из A2S («хост:порт запросов»). Порт запросов может отличаться от игрового,
    /// поэтому «на этом сервере» только при совпадении IP и порта; другой IP при известной игре Rust — «на другом сервере»; остальное — неопределённо.
    /// </summary>
    public static SteamServerMatch Evaluate(SteamPlayerStatus? st, string serverHostIp, int queryPort)
    {
        if (st is null) return SteamServerMatch.NotChecked;
        if (string.IsNullOrWhiteSpace(st.ReportedServer))
            return SteamServerMatch.Inconclusive;                      // отсутствие данных не доказывает отсутствие на сервере
        var rep = st.ReportedServer.Trim(); var colon = rep.LastIndexOf(':');
        var ip = colon > 0 ? rep[..colon] : rep;
        int? port = colon > 0 && int.TryParse(rep[(colon + 1)..], out var p) ? p : null;
        if (!string.Equals(ip, serverHostIp.Trim(), StringComparison.OrdinalIgnoreCase)) return SteamServerMatch.OnOtherServer;
        return port == queryPort ? SteamServerMatch.OnThisServer : SteamServerMatch.Inconclusive;
    }

    /// <summary>
    /// Точное сопоставление: Steam сообщает «ip:игровой порт», а у сервера в приложении хранится порт запросов; игровой порт берётся из A2S_INFO.
    /// Совпадение IP и любого из двух портов — «на этом сервере»; тот же IP, но другой порт — другой сервер на том же хосте; другой IP — другой сервер.
    /// serverIps — адреса, в которые разрешается имя хоста (если хост задан именем).
    /// </summary>
    public static SteamServerMatch Evaluate(SteamPlayerStatus? st, IReadOnlyCollection<string> serverIps, int queryPort, int? gamePort)
    {
        if (st is null) return SteamServerMatch.NotChecked;
        if (string.IsNullOrWhiteSpace(st.ReportedServer)) return SteamServerMatch.Inconclusive;
        var rep = st.ReportedServer.Trim(); var colon = rep.LastIndexOf(':');
        var ip = colon > 0 ? rep[..colon] : rep;
        int? port = colon > 0 && int.TryParse(rep[(colon + 1)..], out var p) ? p : null;
        if (!serverIps.Any(x => string.Equals(x, ip, StringComparison.OrdinalIgnoreCase))) return SteamServerMatch.OnOtherServer;
        if (port is null) return SteamServerMatch.Inconclusive;
        return port == queryPort || (gamePort is not null && port == gamePort) ? SteamServerMatch.OnThisServer : SteamServerMatch.OnOtherServer;
    }

    public static IdentityLevel Effective(IdentityLevel stored, SteamPlayerStatus? st) =>
        stored != IdentityLevel.Unconfirmed && st is { ReportedServer: { Length: > 0 } } ? IdentityLevel.SteamConfirmed : stored;
}

public static class PlayerIndicatorRules
{
    /// <summary>
    /// Зелёный/красный — только при достоверной идентификации (подтверждено Steam или пользователем). Совпадение по нику — всегда серый.
    /// Красный — только после подтверждённого выхода (несколько успешных проверок), а не при единичном «не найден» (NotDetected).
    /// </summary>
    public static PlayerIndicator Compute(IdentityLevel effective, PresenceStatus status, SteamServerMatch steam)
    {
        if (effective == IdentityLevel.Unconfirmed) return PlayerIndicator.Unknown;
        if (effective == IdentityLevel.SteamConfirmed)
        {
            if (steam == SteamServerMatch.OnThisServer) return PlayerIndicator.Present;
            if (steam == SteamServerMatch.OnOtherServer) return PlayerIndicator.Absent;
        }
        return status switch
        {
            PresenceStatus.OnServer => PlayerIndicator.Present,
            PresenceStatus.ExitConfirmed => PlayerIndicator.Absent,
            _ => PlayerIndicator.Unknown
        };
    }
}

public sealed record KnownServer(string ServerKey, string Name, string Host, int Port, bool PollEnabled, DateTimeOffset? LastPolled, int? LastPlayers);
public sealed record PlayerSighting(string ServerKey, string ServerName, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Visits, double EstimatedSeconds);
public sealed record VisitedServer(string ServerKey, string ServerName, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Visits, double EstimatedSeconds);
public sealed record PlayerNameRecord(string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>Собственная база серверов и игроков приложения: известные серверы для опроса, история ников SteamID64, поиск по всем наблюдавшимся серверам.</summary>
public sealed class PlayerDirectoryRepository
{
    private readonly Database _db;
    public PlayerDirectoryRepository(Database db) { _db = db; }

    // ----- известные серверы -----
    public Task UpsertKnownServerAsync(string serverKey, string name, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!ServerKey.TryParse(serverKey, out var host, out var port)) return Task.CompletedTask;
        return _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO known_servers(server_key,name,host,port,added_at) VALUES(@k,@n,@h,@p,@a)
                ON CONFLICT(server_key) DO UPDATE SET name=CASE WHEN @n<>@k THEN @n ELSE name END",
                ("@k", serverKey), ("@n", string.IsNullOrWhiteSpace(name) ? serverKey : name.Trim()), ("@h", host), ("@p", port), ("@a", Sql.Iso(now)));
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task<IReadOnlyList<KnownServer>> ListKnownServersAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<KnownServer>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT server_key,name,host,port,poll_enabled,last_polled,last_players FROM known_servers ORDER BY name");
            using var r = cmd.ExecuteReader();
            var l = new List<KnownServer>();
            while (r.Read()) l.Add(new KnownServer(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) != 0, Sql.ParseIso(r.GetValue(5)), r.IsDBNull(6) ? null : r.GetInt32(6)));
            return l;
        }, ct);

    /// <summary>Серверы, которые пора опросить: включённые, самые давно опрошенные первыми, не чаще minInterval на сервер, не более limit за цикл.</summary>
    public Task<IReadOnlyList<KnownServer>> DueServersAsync(DateTimeOffset now, TimeSpan minInterval, int limit, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<KnownServer>>(c =>
        {
            using var cmd = Sql.Cmd(c, @"SELECT server_key,name,host,port,poll_enabled,last_polled,last_players FROM known_servers
                WHERE poll_enabled=1 AND (last_polled IS NULL OR last_polled<=@due) ORDER BY last_polled IS NOT NULL, last_polled LIMIT @l",
                ("@due", Sql.Iso(now - minInterval)), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var l = new List<KnownServer>();
            while (r.Read()) l.Add(new KnownServer(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) != 0, Sql.ParseIso(r.GetValue(5)), r.IsDBNull(6) ? null : r.GetInt32(6)));
            return l;
        }, ct);

    public Task MarkPolledAsync(string serverKey, DateTimeOffset now, int? players, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE known_servers SET last_polled=@t, last_players=COALESCE(@p,last_players) WHERE server_key=@k", ("@t", Sql.Iso(now)), ("@p", players), ("@k", serverKey));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task SetPollEnabledAsync(string serverKey, bool enabled, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE known_servers SET poll_enabled=@e WHERE server_key=@k", ("@e", enabled ? 1 : 0), ("@k", serverKey)); cmd.ExecuteNonQuery(); }, ct);

    // ----- история ников SteamID64 -----
    public Task RecordNameAsync(string steamId, string name, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamId) || string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        return _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO player_names(steam_id,name,first_seen,last_seen) VALUES(@s,@n,@t,@t)
                ON CONFLICT(steam_id,name) DO UPDATE SET last_seen=@t", ("@s", steamId), ("@n", name.Trim()), ("@t", Sql.Iso(now)));
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task<IReadOnlyList<PlayerNameRecord>> NamesAsync(string steamId, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<PlayerNameRecord>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT name,first_seen,last_seen FROM player_names WHERE steam_id=@s ORDER BY last_seen DESC", ("@s", steamId));
            using var r = cmd.ExecuteReader();
            var l = new List<PlayerNameRecord>();
            while (r.Read()) l.Add(new PlayerNameRecord(r.GetString(0), Sql.ParseIso(r.GetValue(1))!.Value, Sql.ParseIso(r.GetValue(2))!.Value));
            return l;
        }, ct);

    // ----- поиск по всей базе наблюдений -----
    /// <summary>Где наблюдался ник (точное совпадение без учёта регистра) на всех известных серверах. Это совпадение по нику, а не подтверждённая личность.</summary>
    public Task<IReadOnlyList<PlayerSighting>> SearchByNameAsync(IReadOnlyCollection<string> names, int limit = 200, CancellationToken ct = default)
    {
        var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return Task.FromResult<IReadOnlyList<PlayerSighting>>(Array.Empty<PlayerSighting>());
        return _db.RunAsync<IReadOnlyList<PlayerSighting>>(c =>
        {
            var l = new List<PlayerSighting>();
            foreach (var n in distinct)
            {
                using var cmd = Sql.Cmd(c, @"SELECT p.server_key, COALESCE(k.name,p.server_key), p.name, MIN(p.first_seen), MAX(p.last_seen), COUNT(*), SUM(p.est_seconds)
                    FROM presence_history p LEFT JOIN known_servers k ON k.server_key=p.server_key
                    WHERE lower(p.name)=lower(@n) GROUP BY p.server_key, p.name ORDER BY MAX(p.last_seen) DESC LIMIT @l", ("@n", n), ("@l", limit));
                using var r = cmd.ExecuteReader();
                while (r.Read()) l.Add(new PlayerSighting(r.GetString(0), r.GetString(1), r.GetString(2), Sql.ParseIso(r.GetValue(3))!.Value, Sql.ParseIso(r.GetValue(4))!.Value, r.GetInt32(5), r.GetDouble(6)));
            }
            return l.OrderByDescending(x => x.LastSeen).Take(limit).ToList();
        }, ct);
    }

    /// <summary>Посещённые серверы по нику: первое/последнее обнаружение, число отдельных заходов, расчётное суммарное время (по моментам опроса, не точное игровое).</summary>
    public async Task<IReadOnlyList<VisitedServer>> VisitedServersAsync(string name, CancellationToken ct = default) =>
        (await SearchByNameAsync(new[] { name }, 500, ct)).Select(s => new VisitedServer(s.ServerKey, s.ServerName, s.FirstSeen, s.LastSeen, s.Visits, s.EstimatedSeconds)).ToList();
}
