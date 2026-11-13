using Microsoft.Data.Sqlite;
using RustCompanion.Core.Data;

namespace RustCompanion.Core.Watch;

public sealed class WatchRepository
{
    private readonly Database _db;
    public WatchRepository(Database db) { _db = db; }

    // ----- серверы под наблюдением (ключ хост:порт запросов) -----
    public Task<long> AddTrackedServerAsync(string name, string host, int port, string sourceKey, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var find = Sql.Cmd(c, "SELECT id FROM servers WHERE source_key=@b", ("@b", sourceKey));
            if (find.ExecuteScalar() is long existing) return existing;
            using var cmd = Sql.Cmd(c, "INSERT OR IGNORE INTO servers(name,host,port,source_key) VALUES(@n,@h,@p,@b); SELECT id FROM servers WHERE host=@h AND port=@p",
                ("@n", name), ("@h", host), ("@p", port), ("@b", sourceKey));
            var id = (long)cmd.ExecuteScalar()!;
            using var upd = Sql.Cmd(c, "UPDATE servers SET source_key=@b WHERE id=@id AND source_key IS NULL", ("@b", sourceKey), ("@id", id));
            upd.ExecuteNonQuery();
            return id;
        }, ct);

    public Task<IReadOnlyList<(long Id, string Name, string? SourceKey)>> ListTrackedServersAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<(long, string, string?)>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,name,source_key FROM servers WHERE source_key IS NOT NULL ORDER BY name");
            using var r = cmd.ExecuteReader();
            var l = new List<(long, string, string?)>();
            while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return l;
        }, ct);

    /// <summary>Удаляет сервер целиком (он общий для вкладок «Серверы» и «Наблюдение»). Наблюдаемые игроки, их состояние и история активности удаляются каскадом (FK ON DELETE CASCADE);
    /// состояние порогов групп не имеет внешнего ключа на сервер, поэтому чистится явно. Возвращает, сколько наблюдаемых игроков было удалено.</summary>
    public Task<int> RemoveTrackedServerAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            int removed;
            using (var cnt = Sql.Cmd(c, "SELECT COUNT(*) FROM watch_entries WHERE server_id=@s", ("@s", serverId))) removed = Convert.ToInt32(cnt.ExecuteScalar());
            using (var gs = Sql.Cmd(c, "DELETE FROM group_state WHERE server_id=@s", ("@s", serverId))) gs.ExecuteNonQuery();
            using (var del = Sql.Cmd(c, "DELETE FROM servers WHERE id=@s", ("@s", serverId))) del.ExecuteNonQuery();
            return removed;
        }, ct);

    // ----- группы -----
    public Task<long> AddGroupAsync(string name, string color, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название группы.");
        return _db.RunAsync(c =>
        {
            try
            {
                using var cmd = Sql.Cmd(c, "INSERT INTO groups(name,color) VALUES(@n,@c); SELECT last_insert_rowid();", ("@n", name.Trim()), ("@c", color));
                return (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Группа с таким названием уже есть."); }
        }, ct);
    }

    public Task<IReadOnlyList<WatchGroup>> ListGroupsAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<WatchGroup>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,name,color,notify_join,notify_leave,threshold FROM groups ORDER BY name");
            using var r = cmd.ExecuteReader();
            var l = new List<WatchGroup>();
            while (r.Read()) l.Add(new WatchGroup { Id = r.GetInt64(0), Name = r.GetString(1), Color = r.GetString(2), NotifyJoin = r.GetInt32(3) != 0, NotifyLeave = r.GetInt32(4) != 0, Threshold = r.GetInt32(5) });
            return l;
        }, ct);

    public Task UpdateGroupAsync(WatchGroup g, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE groups SET name=@n,color=@c,notify_join=@j,notify_leave=@l,threshold=@t WHERE id=@id",
                ("@n", g.Name.Trim()), ("@c", g.Color), ("@j", g.NotifyJoin ? 1 : 0), ("@l", g.NotifyLeave ? 1 : 0), ("@t", g.Threshold), ("@id", g.Id));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteGroupAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM groups WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    // ----- отслеживаемые игроки (привязка к серверу) -----
    public Task<long> AddWatchAsync(long serverId, string playerKey, string? steamId, string name, long? groupId = null, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            try
            {
                using var cmd = Sql.Cmd(c, "INSERT INTO watch_entries(server_id,player_key,steam_id,name,group_id) VALUES(@s,@p,@st,@n,@g); SELECT last_insert_rowid();",
                    ("@s", serverId), ("@p", playerKey), ("@st", steamId), ("@n", name), ("@g", groupId));
                return (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Этот игрок уже отслеживается на выбранном сервере."); }
        }, ct);

    public Task<IReadOnlyList<WatchEntry>> ListWatchAsync(long? serverId = null, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<WatchEntry>>(c =>
        {
            var sql = "SELECT id,server_id,player_key,steam_id,name,group_id,color,notify_join,notify_leave,identity_level FROM watch_entries" + (serverId is null ? "" : " WHERE server_id=@s") + " ORDER BY name";
            using var cmd = serverId is null ? Sql.Cmd(c, sql) : Sql.Cmd(c, sql, ("@s", serverId));
            using var r = cmd.ExecuteReader();
            var l = new List<WatchEntry>();
            while (r.Read()) l.Add(new WatchEntry
            {
                Id = r.GetInt64(0), ServerId = r.GetInt64(1), PlayerKey = r.GetString(2), SteamId = r.IsDBNull(3) ? null : r.GetString(3), Name = r.GetString(4),
                GroupId = r.IsDBNull(5) ? null : r.GetInt64(5), Color = r.IsDBNull(6) ? null : r.GetString(6), NotifyJoin = r.GetInt32(7) != 0, NotifyLeave = r.GetInt32(8) != 0,
                IdentityLevel = (IdentityLevel)Math.Clamp(r.GetInt32(9), 0, 1)
            });
            return l;
        }, ct);

    public Task UpdateWatchAsync(WatchEntry w, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE watch_entries SET name=@n,group_id=@g,color=@col,notify_join=@j,notify_leave=@l WHERE id=@id",
                ("@n", w.Name), ("@g", w.GroupId), ("@col", w.Color), ("@j", w.NotifyJoin ? 1 : 0), ("@l", w.NotifyLeave ? 1 : 0), ("@id", w.Id));
            cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>Ручная связь ника со SteamID64 (уровень «подтверждено пользователем») или её снятие (steamId = null). Формат SteamID64 проверяет вызывающий.</summary>
    public Task SetIdentityAsync(long watchId, string? steamId, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE watch_entries SET steam_id=@s, identity_level=@l WHERE id=@id",
                ("@s", steamId), ("@l", steamId is null ? 0 : 1), ("@id", watchId));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task RemoveWatchAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM watch_entries WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    // ----- состояние мониторинга (переживает перезапуск) -----
    public Task<MonitorState> GetStateAsync(long watchId, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT status,absence_count,last_present,last_checked,last_counted_source_ts,exit_notified,episode,last_error FROM monitor_state WHERE watch_id=@w", ("@w", watchId));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new MonitorState { WatchId = watchId };
            return new MonitorState
            {
                WatchId = watchId, Status = (PresenceStatus)r.GetInt32(0), AbsenceCount = r.GetInt32(1),
                LastPresentUtc = Sql.ParseIso(r.GetValue(2)), LastCheckedUtc = Sql.ParseIso(r.GetValue(3)), LastCountedSourceTs = Sql.ParseIso(r.GetValue(4)),
                ExitNotified = r.GetInt32(5) != 0, Episode = r.GetInt32(6), LastError = r.IsDBNull(7) ? null : r.GetString(7)
            };
        }, ct);

    public Task SaveStateAsync(MonitorState s, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO monitor_state(watch_id,status,absence_count,last_present,last_checked,last_counted_source_ts,exit_notified,episode,last_error)
                VALUES(@w,@s,@a,@lp,@lc,@ls,@e,@ep,@err)
                ON CONFLICT(watch_id) DO UPDATE SET status=@s,absence_count=@a,last_present=@lp,last_checked=@lc,last_counted_source_ts=@ls,exit_notified=@e,episode=@ep,last_error=@err",
                ("@w", s.WatchId), ("@s", (int)s.Status), ("@a", s.AbsenceCount),
                ("@lp", s.LastPresentUtc is null ? null : Sql.Iso(s.LastPresentUtc.Value)), ("@lc", s.LastCheckedUtc is null ? null : Sql.Iso(s.LastCheckedUtc.Value)),
                ("@ls", s.LastCountedSourceTs is null ? null : Sql.Iso(s.LastCountedSourceTs.Value)), ("@e", s.ExitNotified ? 1 : 0), ("@ep", s.Episode), ("@err", s.LastError));
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { /* запись наблюдения (или её сервер) удалена во время цикла опроса — сохранять нечего */ }
        }, ct);

    // ----- дедупликация и история -----
    /// <summary>true — событие новое (ключ записан); false — уже обработано ранее, в т.ч. до перезапуска.</summary>
    public Task<bool> TryMarkNotifiedAsync(string key, DateTimeOffset at, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT OR IGNORE INTO notified_events(event_key,at) VALUES(@k,@a)", ("@k", key), ("@a", Sql.Iso(at)));
            return cmd.ExecuteNonQuery() > 0;
        }, ct);

    public Task AddActivityAsync(long watchId, DateTimeOffset at, string kind, string detail, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO activity_events(watch_id,at,kind,detail) VALUES(@w,@a,@k,@d)", ("@w", watchId), ("@a", Sql.Iso(at)), ("@k", kind), ("@d", detail));
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { /* запись наблюдения удалена во время цикла опроса */ }
        }, ct);

    public Task<IReadOnlyList<ActivityEvent>> ListActivityAsync(long? watchId, string? kind, DateTimeOffset? fromUtc, int limit, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ActivityEvent>>(c =>
        {
            var where = new List<string>(); var ps = new List<(string, object?)>();
            if (watchId is not null) { where.Add("watch_id=@w"); ps.Add(("@w", watchId)); }
            if (kind is not null) { where.Add("kind=@k"); ps.Add(("@k", kind)); }
            if (fromUtc is not null) { where.Add("at>=@f"); ps.Add(("@f", Sql.Iso(fromUtc.Value))); }
            var sql = "SELECT watch_id,at,kind,detail FROM activity_events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC LIMIT @l";
            ps.Add(("@l", limit));
            using var cmd = Sql.Cmd(c, sql, ps.ToArray());
            using var r = cmd.ExecuteReader();
            var l = new List<ActivityEvent>();
            while (r.Read()) l.Add(new ActivityEvent(r.GetInt64(0), Sql.ParseIso(r.GetValue(1))!.Value, r.GetString(2), r.GetString(3)));
            return l;
        }, ct);

    // ----- состояние порогов групп -----
    public Task<(bool Above, int Episode)> GetGroupStateAsync(long groupId, long serverId, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT above,episode FROM group_state WHERE group_id=@g AND server_id=@s", ("@g", groupId), ("@s", serverId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0) != 0, r.GetInt32(1)) : (false, 0);
        }, ct);

    public Task SaveGroupStateAsync(long groupId, long serverId, bool above, int episode, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO group_state(group_id,server_id,above,episode) VALUES(@g,@s,@a,@e)
                ON CONFLICT(group_id,server_id) DO UPDATE SET above=@a,episode=@e", ("@g", groupId), ("@s", serverId), ("@a", above ? 1 : 0), ("@e", episode));
            cmd.ExecuteNonQuery();
        }, ct);
}
