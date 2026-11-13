using System.Text.Json;
using Microsoft.Data.Sqlite;
using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Data;

public sealed class SqliteTimerRepository : ITimerRepository
{
    private const string Select = "SELECT id,kind,name,started_at,interval_seconds,lead_seconds,note,notify_enabled,lead_notified,due_notified FROM timers";
    private readonly Database _db;
    public SqliteTimerRepository(Database db) { _db = db; }

    private static TimerEntry Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Kind = (TrackerKind)r.GetInt32(1), Name = r.GetString(2),
        StartedAtUtc = Sql.ParseIso(r.GetValue(3)), IntervalSeconds = r.GetInt32(4), LeadSeconds = r.GetInt32(5),
        Note = r.GetString(6), NotifyEnabled = r.GetInt32(7) != 0, LeadNotified = r.GetInt32(8) != 0, DueNotified = r.GetInt32(9) != 0
    };

    public Task<IReadOnlyList<TimerEntry>> ListAsync(TrackerKind? kind, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<TimerEntry>>(c =>
        {
            using var cmd = kind is null
                ? Sql.Cmd(c, Select + " ORDER BY id")
                : Sql.Cmd(c, Select + " WHERE kind=@k ORDER BY id", ("@k", (int)kind.Value));
            using var r = cmd.ExecuteReader();
            var list = new List<TimerEntry>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }, ct);

    public Task<long> InsertAsync(TimerEntry t, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c,
                @"INSERT INTO timers(kind,name,started_at,interval_seconds,lead_seconds,note,notify_enabled,lead_notified,due_notified)
                  VALUES(@k,@n,@s,@i,@l,@note,@ne,@ln,@dn); SELECT last_insert_rowid();",
                ("@k", (int)t.Kind), ("@n", t.Name), ("@s", t.StartedAtUtc is null ? null : Sql.Iso(t.StartedAtUtc.Value)),
                ("@i", t.IntervalSeconds), ("@l", t.LeadSeconds), ("@note", t.Note),
                ("@ne", t.NotifyEnabled ? 1 : 0), ("@ln", t.LeadNotified ? 1 : 0), ("@dn", t.DueNotified ? 1 : 0));
            return (long)cmd.ExecuteScalar()!;
        }, ct);

    public Task UpdateAsync(TimerEntry t, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c,
                @"UPDATE timers SET name=@n,started_at=@s,interval_seconds=@i,lead_seconds=@l,note=@note,
                  notify_enabled=@ne,lead_notified=@ln,due_notified=@dn WHERE id=@id",
                ("@id", t.Id), ("@n", t.Name), ("@s", t.StartedAtUtc is null ? null : Sql.Iso(t.StartedAtUtc.Value)),
                ("@i", t.IntervalSeconds), ("@l", t.LeadSeconds), ("@note", t.Note),
                ("@ne", t.NotifyEnabled ? 1 : 0), ("@ln", t.LeadNotified ? 1 : 0), ("@dn", t.DueNotified ? 1 : 0));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM timers WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task AddHistoryAsync(long timerId, DateTimeOffset atUtc, string action, string note, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO timer_history(timer_id,at,action,note) VALUES(@t,@a,@ac,@n)",
                ("@t", timerId), ("@a", Sql.Iso(atUtc)), ("@ac", action), ("@n", note));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<HistoryEntry>> HistoryAsync(long timerId, int limit, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<HistoryEntry>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT at,action,note FROM timer_history WHERE timer_id=@t ORDER BY id DESC LIMIT @l",
                ("@t", timerId), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (r.Read()) list.Add(new HistoryEntry(Sql.ParseIso(r.GetValue(0))!.Value, r.GetString(1), r.GetString(2)));
            return list;
        }, ct);
}

public sealed class ServerRepository
{
    private readonly Database _db;
    public ServerRepository(Database db) { _db = db; }

    public Task<IReadOnlyList<ServerEntry>> ListAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ServerEntry>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,name,host,port,favorite FROM servers ORDER BY favorite DESC, name");
            using var r = cmd.ExecuteReader();
            var list = new List<ServerEntry>();
            while (r.Read()) list.Add(new ServerEntry { Id = r.GetInt64(0), Name = r.GetString(1), Host = r.GetString(2), QueryPort = r.GetInt32(3), Favorite = r.GetInt32(4) != 0 });
            return list;
        }, ct);

    public static void Validate(string name, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название сервера.");
        if (string.IsNullOrWhiteSpace(host) || host.Contains(' ') || host.Contains("://")) throw new ArgumentException("Укажите адрес сервера (IP или домен, без http://).");
        if (port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
    }

    public Task<long> AddAsync(string name, string host, int port, CancellationToken ct = default)
    {
        Validate(name, host, port);
        return _db.RunAsync(c =>
        {
            try
            {
                using var cmd = Sql.Cmd(c, "INSERT INTO servers(name,host,port) VALUES(@n,@h,@p); SELECT last_insert_rowid();",
                    ("@n", name.Trim()), ("@h", host.Trim()), ("@p", port));
                return (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { throw new InvalidOperationException("Такой сервер уже добавлен."); }
        }, ct);
    }

    public Task SetFavoriteAsync(long id, bool fav, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE servers SET favorite=@f WHERE id=@id", ("@f", fav ? 1 : 0), ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    /// <summary>Удаляет сервер. Наблюдаемые игроки этого сервера (вкладка «Наблюдение») удаляются каскадом; состояние порогов групп (без внешнего ключа) чистится явно.</summary>
    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using (var gs = Sql.Cmd(c, "DELETE FROM group_state WHERE server_id=@id", ("@id", id))) gs.ExecuteNonQuery();
            using var cmd = Sql.Cmd(c, "DELETE FROM servers WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery();
        }, ct);

    public Task AddObservationAsync(long serverId, DateTimeOffset at, int players, int max, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO server_observations(server_id,at,players,max_players) VALUES(@s,@a,@p,@m)",
                ("@s", serverId), ("@a", Sql.Iso(at)), ("@p", players), ("@m", max));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<ServerObservation>> RecentObservationsAsync(long serverId, int limit, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ServerObservation>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT at,players,max_players FROM server_observations WHERE server_id=@s ORDER BY id DESC LIMIT @l", ("@s", serverId), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<ServerObservation>();
            while (r.Read()) list.Add(new ServerObservation(Sql.ParseIso(r.GetValue(0))!.Value, r.GetInt32(1), r.GetInt32(2)));
            return list;
        }, ct);
}

public sealed class PlayerRepository
{
    private readonly Database _db;
    public PlayerRepository(Database db) { _db = db; }

    public Task<IReadOnlyList<PlayerEntry>> ListAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<PlayerEntry>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,steam_id,label FROM players ORDER BY label, steam_id");
            using var r = cmd.ExecuteReader();
            var list = new List<PlayerEntry>();
            while (r.Read()) list.Add(new PlayerEntry { Id = r.GetInt64(0), SteamId = r.GetString(1), Label = r.GetString(2) });
            return list;
        }, ct);

    public Task<long> AddAsync(string steamId, string label, CancellationToken ct = default)
    {
        if (!Net.SteamClient.IsValidSteamId64(steamId?.Trim())) throw new ArgumentException("SteamID64 — 17 цифр, начинается с 7656119.");
        return _db.RunAsync(c =>
        {
            try
            {
                using var cmd = Sql.Cmd(c, "INSERT INTO players(steam_id,label) VALUES(@s,@l); SELECT last_insert_rowid();", ("@s", steamId!.Trim()), ("@l", label?.Trim() ?? ""));
                return (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { throw new InvalidOperationException("Этот профиль уже добавлен."); }
        }, ct);
    }

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM players WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);
}

public sealed class SettingsRepository
{
    private const string Key = "app";
    private readonly Database _db;
    public SettingsRepository(Database db) { _db = db; }

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT value FROM settings WHERE key=@k", ("@k", Key));
            var v = cmd.ExecuteScalar() as string;
            if (v is null) return new AppSettings();
            try { var s = JsonSerializer.Deserialize<AppSettings>(v); return s is not null && s.Validate().Count == 0 ? s : new AppSettings(); }
            catch (JsonException) { return new AppSettings(); }
        }, ct);

    public Task SaveAsync(AppSettings s, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("@k", Key), ("@v", JsonSerializer.Serialize(s)));
            cmd.ExecuteNonQuery();
        }, ct);
}
