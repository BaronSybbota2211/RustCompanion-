using RustCompanion.Core.Data;

namespace RustCompanion.Core.RustPlus;

public sealed class PairedServerRepository
{
    private readonly Database _db; private readonly ISecretStore _secrets;
    public PairedServerRepository(Database db, ISecretStore secrets) { _db = db; _secrets = secrets; }

    private static PairedServer Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), ulong.Parse(r.GetString(4)), Sql.ParseIso(r.GetValue(5))!.Value, Sql.ParseIso(r.GetValue(6)), r.IsDBNull(7) ? null : r.GetString(7));

    public Task<IReadOnlyList<PairedServer>> ListAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<PairedServer>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,name,ip,port,player_id,paired_at,last_connected,rustplus_id FROM rp_servers ORDER BY name");
            using var r = cmd.ExecuteReader();
            var l = new List<PairedServer>(); while (r.Read()) l.Add(Map(r)); return l;
        }, ct);

    /// <summary>Добавляет или обновляет сопряжённый сервер. Токен игрока сохраняется только в защищённом хранилище, не в БД.</summary>
    public async Task<PairedServer> UpsertAsync(string name, string ip, int port, ulong playerId, int playerToken, DateTimeOffset at, CancellationToken ct = default, string? rustPlusId = null)
    {
        rustPlusId = string.IsNullOrWhiteSpace(rustPlusId) ? null : rustPlusId.Trim();
        if (!RustPlusRoutes.HasDirectAddress(ip, port))
        {
            // Новый протокол: в уведомлении нет IP/порта, сервер однозначно определяется Server ID (подключение — через новый канал).
            if (!RustPlusRoutes.TryGetServerId(rustPlusId, out var sid)) throw new ArgumentException("Адрес и порт сервера Rust+ некорректны, а ID сервера в уведомлении отсутствует.");
            ip = RustPlusRoutes.NoAddressPrefix + sid.ToString("D"); port = 0;
        }
        var id = await _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO rp_servers(name,ip,port,player_id,paired_at,rustplus_id) VALUES(@n,@i,@p,@pl,@a,@u)
                ON CONFLICT(ip,port,player_id) DO UPDATE SET name=@n,paired_at=@a,rustplus_id=COALESCE(@u,rustplus_id); SELECT id FROM rp_servers WHERE ip=@i AND port=@p AND player_id=@pl",
                ("@n", string.IsNullOrWhiteSpace(name) ? $"{ip}:{port}" : name.Trim()), ("@i", ip.Trim()), ("@p", port), ("@pl", playerId.ToString()), ("@a", Sql.Iso(at)), ("@u", rustPlusId));
            return (long)cmd.ExecuteScalar()!;
        }, ct);
        _secrets.Set(SecretNames.RustPlusToken(id), playerToken.ToString());
        return (await ListAsync(ct)).First(s => s.Id == id);
    }

    public int? GetToken(long serverId) => int.TryParse(_secrets.Get(SecretNames.RustPlusToken(serverId)), out var t) ? t : null;

    /// <summary>Токен входа Facepunch (общий для всех серверов аккаунта). Нужен новому каналу подключения /socket/{ServerId}. Хранится только в защищённом хранилище; значение регистрируется в Redactor и не попадает в журнал.</summary>
    public string? GetCompanionToken()
    {
        var t = _secrets.Get(SecretNames.RustPlusCompanionToken);
        if (string.IsNullOrWhiteSpace(t)) return null;
        global::RustCompanion.Core.Net.Redactor.Register(t);
        return t;
    }

    public void SetCompanionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) { _secrets.Delete(SecretNames.RustPlusCompanionToken); return; }
        global::RustCompanion.Core.Net.Redactor.Register(token);
        _secrets.Set(SecretNames.RustPlusCompanionToken, token);
    }

    /// <summary>Полные данные для подключения: токен игрока, ID сервера (из уведомления о сопряжении) и токен входа Facepunch. null — нет токена игрока.</summary>
    public RpCredentials? BuildCredentials(PairedServer server)
    {
        var token = GetToken(server.Id);
        return token is null ? null : new RpCredentials(server.Ip, server.Port, server.PlayerId, token.Value, server.RustPlusId, GetCompanionToken());
    }

    /// <summary>То же, но по свежей записи из БД: ID сервера мог появиться после повторного сопряжения, а токен входа — после нового входа через Steam.</summary>
    public async Task<RpCredentials?> ResolveCredentialsAsync(long serverId, CancellationToken ct = default)
    {
        var server = (await ListAsync(ct)).FirstOrDefault(s => s.Id == serverId);
        return server is null ? null : BuildCredentials(server);
    }

    public Task TouchConnectedAsync(long id, DateTimeOffset at, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE rp_servers SET last_connected=@a WHERE id=@i", ("@a", Sql.Iso(at)), ("@i", id)); cmd.ExecuteNonQuery(); }, ct);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM rp_servers WHERE id=@i", ("@i", id)); cmd.ExecuteNonQuery(); }, ct);
        _secrets.Delete(SecretNames.RustPlusToken(id));
    }
}

public sealed class RpDevice
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public ulong EntityId { get; set; }
    public RpDeviceKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Note { get; set; } = "";
    public bool AlarmNotify { get; set; }
    /// <summary>Текст уведомления этой сигнализации ({device}, {server}, \n); пусто — стандартный.</summary>
    public string AlarmText { get; set; } = "";
    /// <summary>Канал уведомления при срабатывании: 0 — выкл, 1 — Team Chat, 2 — Overlay, 3 — оба.</summary>
    public int AlarmChannel { get; set; } = 2;
    public DateTimeOffset? AddedAtUtc { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public bool? LastIsOn { get; set; }
}
public sealed record RpDeviceEvent(DateTimeOffset AtUtc, string Kind, string Detail);

public sealed class RpDeviceRepository
{
    private readonly Database _db;
    public RpDeviceRepository(Database db) { _db = db; }

    private static DateTimeOffset? ParseOptionalDate(object value) =>
        value is DBNull || string.IsNullOrWhiteSpace(value as string) ? null : Sql.ParseIso(value);

    private static RpDevice Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), ServerId = r.GetInt64(1), EntityId = ulong.Parse(r.GetString(2)), Kind = (RpDeviceKind)r.GetInt32(3),
        Name = r.GetString(4), Group = r.GetString(5), Note = r.GetString(6), AlarmNotify = r.GetInt32(7) != 0,
        AddedAtUtc = ParseOptionalDate(r.GetValue(8)), LastSyncUtc = ParseOptionalDate(r.GetValue(9)), LastIsOn = r.IsDBNull(10) ? null : r.GetInt32(10) != 0,
        AlarmText = r.IsDBNull(11) ? "" : r.GetString(11),
        AlarmChannel = r.IsDBNull(12) ? 2 : r.GetInt32(12)
    };

    public Task<IReadOnlyList<RpDevice>> ListAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RpDevice>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,server_id,entity_id,kind,name,grp,note,alarm_notify,added_at,last_sync_at,last_is_on,alarm_text,alarm_channel FROM rp_devices WHERE server_id=@s ORDER BY grp,name", ("@s", serverId));
            using var r = cmd.ExecuteReader();
            var l = new List<RpDevice>(); while (r.Read()) l.Add(Map(r)); return l;
        }, ct);

    /// <summary>Добавление после подтверждённого чтения сопряжения. Дубликат по (сервер, entity) обновляет тип и не затирает пользовательское имя.</summary>
    public Task<long> AddAsync(long serverId, ulong entityId, RpDeviceKind kind, string name, CancellationToken ct = default, DateTimeOffset? addedAtUtc = null) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO rp_devices(server_id,entity_id,kind,name,added_at,alarm_notify) VALUES(@s,@e,@k,@n,@a,@bell)
                ON CONFLICT(server_id,entity_id) DO UPDATE SET kind=@k; SELECT id FROM rp_devices WHERE server_id=@s AND entity_id=@e",
                ("@s", serverId), ("@e", entityId.ToString()), ("@k", (int)kind), ("@n", string.IsNullOrWhiteSpace(name) ? $"Устройство {entityId}" : name.Trim()),
                ("@a", Sql.Iso(addedAtUtc ?? DateTimeOffset.UtcNow)), ("@bell", kind == RpDeviceKind.SmartAlarm ? 1 : 0));   // новая сигнализация сразу уведомляет; пользователь может выключить колокольчик
            return (long)cmd.ExecuteScalar()!;
        }, ct);

    public Task UpdateAsync(RpDevice d, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE rp_devices SET name=@n,grp=@g,note=@o,alarm_notify=@a WHERE id=@i",
                ("@n", string.IsNullOrWhiteSpace(d.Name) ? $"Устройство {d.EntityId}" : d.Name.Trim()), ("@g", d.Group.Trim()), ("@o", d.Note), ("@a", d.AlarmNotify ? 1 : 0), ("@i", d.Id));
            cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>Текст и канал уведомления одной сигнализации. Отдельный запрос: устаревшая строка другой вкладки не может затереть эти поля.</summary>
    public Task SetAlarmSettingsAsync(long deviceId, string text, int channel, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE rp_devices SET alarm_text=@t,alarm_channel=@ch WHERE id=@i", ("@t", text ?? ""), ("@ch", Math.Clamp(channel, 0, 3)), ("@i", deviceId)); cmd.ExecuteNonQuery(); }, ct);

    /// <summary>Колокольчик Smart Alarm: включает/выключает отслеживание срабатывания одной сигнализации, не затрагивая остальные поля.</summary>
    public Task SetAlarmNotifyAsync(long deviceId, bool enabled, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE rp_devices SET alarm_notify=@a WHERE id=@i", ("@a", enabled ? 1 : 0), ("@i", deviceId)); cmd.ExecuteNonQuery(); }, ct);

    public Task MarkSynchronizedAsync(long deviceId, RpDeviceState state, DateTimeOffset atUtc, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE rp_devices SET last_sync_at=@a,last_is_on=@v WHERE id=@i",
                ("@a", Sql.Iso(atUtc)), ("@v", state.IsOn is null ? null : state.IsOn.Value ? 1 : 0), ("@i", deviceId));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM rp_devices WHERE id=@i", ("@i", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task AddEventAsync(long deviceId, DateTimeOffset at, string kind, string detail, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "INSERT INTO rp_device_events(device_id,at,kind,detail) VALUES(@d,@a,@k,@t)", ("@d", deviceId), ("@a", Sql.Iso(at)), ("@k", kind), ("@t", detail)); cmd.ExecuteNonQuery(); }, ct);

    public Task<IReadOnlyList<RpDeviceEvent>> EventsAsync(long deviceId, int limit, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RpDeviceEvent>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT at,kind,detail FROM rp_device_events WHERE device_id=@d ORDER BY id DESC LIMIT @l", ("@d", deviceId), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var l = new List<RpDeviceEvent>(); while (r.Read()) l.Add(new RpDeviceEvent(Sql.ParseIso(r.GetValue(0))!.Value, r.GetString(1), r.GetString(2))); return l;
        }, ct);
}
