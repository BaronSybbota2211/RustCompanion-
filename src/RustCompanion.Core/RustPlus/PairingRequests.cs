using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

public enum RpPairingRequestStatus { Pending = 0, Added = 1, Dismissed = 2 }

/// <summary>
/// Карточка обнаруженного FCM-сопряжения. Это не ожидающий ответа запрос протокола:
/// Rust+ присылает OnEntityPairing после Pair в игре, а отдельных accept/reject-команд не имеет.
/// </summary>
public sealed class RpDevicePairingRequest
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string ServerUid { get; set; } = "";
    public ulong PlayerId { get; set; }
    public string? PersistentId { get; set; }
    public string RequestKey { get; set; } = "";
    public ulong EntityId { get; set; }
    public RpDeviceKind Kind { get; set; }
    public string Name { get; set; } = "";
    public RpPairingRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class RpDevicePairingRepository
{
    private readonly Database _db;
    public RpDevicePairingRepository(Database db) => _db = db;

    private static RpDevicePairingRequest Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), ServerId = r.GetInt64(1), ServerUid = r.GetString(2), PlayerId = ulong.Parse(r.GetString(3)),
        PersistentId = r.IsDBNull(4) ? null : r.GetString(4), RequestKey = r.GetString(5), EntityId = ulong.Parse(r.GetString(6)),
        Kind = (RpDeviceKind)r.GetInt32(7), Name = r.GetString(8), Status = (RpPairingRequestStatus)r.GetInt32(9),
        CreatedAtUtc = Sql.ParseIso(r.GetValue(10))!.Value, UpdatedAtUtc = Sql.ParseIso(r.GetValue(11))!.Value, LastError = r.GetString(12)
    };

    private const string Columns = "id,server_id,server_uid,player_id,persistent_id,request_key,entity_id,kind,name,status,created_at,updated_at,last_error";

    public Task<RpDevicePairingRequest?> GetAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync<RpDevicePairingRequest?>(c =>
        {
            using var cmd = Sql.Cmd(c, $"SELECT {Columns} FROM rp_device_pairing_requests WHERE id=@i", ("@i", id));
            using var r = cmd.ExecuteReader(); return r.Read() ? Map(r) : null;
        }, ct);

    public Task<IReadOnlyList<RpDevicePairingRequest>> ListPendingAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RpDevicePairingRequest>>(c =>
        {
            using var cmd = Sql.Cmd(c, $"SELECT {Columns} FROM rp_device_pairing_requests WHERE server_id=@s AND status=0 ORDER BY created_at DESC", ("@s", serverId));
            using var r = cmd.ExecuteReader(); var list = new List<RpDevicePairingRequest>(); while (r.Read()) list.Add(Map(r)); return list;
        }, ct);

    /// <summary>
    /// Дедупликация по FCM Persistent ID и по ожидающей карточке (сервер, Entity ID).
    /// Возвращает null, если устройство уже сохранено либо это повтор уже скрытого/обработанного FCM-сообщения.
    /// Новое событие с другим Persistent ID может снова открыть ранее скрытую карточку.
    /// </summary>
    public Task<RpDevicePairingRequest?> UpsertPendingAsync(PairedServer server, FcmEvent ev, DateTimeOffset now, CancellationToken ct = default)
    {
        if (ev.EntityId is not { } entityId || ev.DeviceKind is not { } kind) throw new ArgumentException("Неполное событие сопряжения устройства.");
        var persistentId = string.IsNullOrWhiteSpace(ev.PersistentId) ? null : ev.PersistentId.Trim();
        var requestKey = persistentId is null ? $"entity:{server.Id}:{entityId}" : "fcm:" + persistentId;
        var name = ev.Name?.Trim() ?? "";
        return _db.RunAsync<RpDevicePairingRequest?>(c =>
        {
            using var tx = c.BeginTransaction();
            using (var exists = Sql.Cmd(c, "SELECT 1 FROM rp_devices WHERE server_id=@s AND entity_id=@e", ("@s", server.Id), ("@e", entityId.ToString())))
            { exists.Transaction = tx; if (exists.ExecuteScalar() is not null) { tx.Rollback(); return null; } }

            RpDevicePairingRequest? ByKey(string key)
            {
                using var cmd = Sql.Cmd(c, $"SELECT {Columns} FROM rp_device_pairing_requests WHERE request_key=@k", ("@k", key));
                cmd.Transaction = tx; using var r = cmd.ExecuteReader(); return r.Read() ? Map(r) : null;
            }

            var sameMessage = ByKey(requestKey);
            if (sameMessage is not null && sameMessage.Status != RpPairingRequestStatus.Pending) { tx.Rollback(); return null; }

            long id;
            if (sameMessage is not null) id = sameMessage.Id;
            else
            {
                using var pending = Sql.Cmd(c, "SELECT id FROM rp_device_pairing_requests WHERE server_id=@s AND entity_id=@e AND status=0",
                    ("@s", server.Id), ("@e", entityId.ToString())); pending.Transaction = tx;
                var pendingId = pending.ExecuteScalar();
                if (pendingId is not null) id = (long)pendingId;
                else
                {
                    using var insert = Sql.Cmd(c, @"INSERT INTO rp_device_pairing_requests
                        (server_id,server_uid,player_id,persistent_id,request_key,entity_id,kind,name,status,created_at,updated_at,last_error)
                        VALUES(@s,@uid,@pl,@pid,@key,@e,@k,@n,0,@at,@at,''); SELECT last_insert_rowid()",
                        ("@s", server.Id), ("@uid", ev.ServerUid ?? ""), ("@pl", (ev.PlayerId ?? server.PlayerId).ToString()), ("@pid", persistentId),
                        ("@key", requestKey), ("@e", entityId.ToString()), ("@k", (int)kind), ("@n", string.IsNullOrWhiteSpace(name) ? $"Устройство {entityId}" : name), ("@at", Sql.Iso(now)));
                    insert.Transaction = tx; id = (long)insert.ExecuteScalar()!;
                }
            }

            using (var update = Sql.Cmd(c, @"UPDATE rp_device_pairing_requests SET server_uid=@uid,player_id=@pl,persistent_id=@pid,
                request_key=@key,kind=@k,name=CASE WHEN @n='' THEN name ELSE @n END,updated_at=@at,last_error='' WHERE id=@id",
                ("@uid", ev.ServerUid ?? server.RustPlusId ?? ""), ("@pl", (ev.PlayerId ?? server.PlayerId).ToString()), ("@pid", persistentId),
                ("@key", requestKey), ("@k", (int)kind), ("@n", name), ("@at", Sql.Iso(now)), ("@id", id)))
            { update.Transaction = tx; update.ExecuteNonQuery(); }
            var result = ByKey(requestKey);
            tx.Commit();
            return result;
        }, ct);
    }

    public Task SetErrorAsync(long id, string error, DateTimeOffset now, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE rp_device_pairing_requests SET last_error=@e,updated_at=@a WHERE id=@i AND status=0",
                ("@e", error.Trim()), ("@a", Sql.Iso(now)), ("@i", id)); cmd.ExecuteNonQuery();
        }, ct);

    public Task SetStatusAsync(long id, RpPairingRequestStatus status, DateTimeOffset now, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "UPDATE rp_device_pairing_requests SET status=@s,updated_at=@a,last_error='' WHERE id=@i",
                ("@s", (int)status), ("@a", Sql.Iso(now)), ("@i", id)); cmd.ExecuteNonQuery();
        }, ct);
}

/// <summary>Проверяет завершённое в игре сопряжение реальным getEntityInfo и только затем сохраняет устройство.</summary>
public sealed class RustPlusDevicePairingService
{
    private readonly Func<IRustPlusGateway> _factory;
    private readonly RustPlusManager _manager;
    private readonly PairedServerRepository _servers;
    private readonly RpDeviceRepository _devices;
    private readonly RpDevicePairingRepository _requests;
    private readonly RpDeviceCommandRepository _commands;
    private readonly RustPlusHub _hub;
    private readonly IClock _clock;
    private readonly DiagnosticsService? _diag;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RustPlusDevicePairingService(Func<IRustPlusGateway> factory, RustPlusManager manager, PairedServerRepository servers,
        RpDeviceRepository devices, RpDevicePairingRepository requests, RpDeviceCommandRepository commands, RustPlusHub hub,
        IClock clock, DiagnosticsService? diag = null)
    { _factory = factory; _manager = manager; _servers = servers; _devices = devices; _requests = requests; _commands = commands; _hub = hub; _clock = clock; _diag = diag; }

    public async Task<RpResult> VerifyAndAddAsync(long requestId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var request = await _requests.GetAsync(requestId, ct);
            if (request is null || request.Status != RpPairingRequestStatus.Pending)
                return RpResult.Fail("not_pending", "Карточка уже обработана или скрыта.");
            var server = (await _servers.ListAsync(ct)).FirstOrDefault(s => s.Id == request.ServerId);
            if (server is null) return await FailAsync(request, "not_found", "Сопряжённый сервер удалён. Устройство не добавлено.", ct);

            var existing = (await _devices.ListAsync(server.Id, ct)).FirstOrDefault(d => d.EntityId == request.EntityId);
            if (existing is not null)
            {
                await _requests.SetStatusAsync(request.Id, RpPairingRequestStatus.Added, _clock.UtcNow, ct);
                _hub.NotifyPairingRequestsChanged();
                return RpResult.Success with { Message = "Устройство уже есть в общем списке." };
            }

            var read = await ReadAsync(server, request, ct);
            if (!read.Ok || read.Value is null)
                return await FailAsync(request, read.Code ?? "verify_failed", "Сопряжение не подтверждено чтением Rust+: " + (read.Message ?? "устройство не ответило."), ct);

            var deviceId = await _devices.AddAsync(server.Id, request.EntityId, request.Kind, request.Name, ct, _clock.UtcNow);
            var device = (await _devices.ListAsync(server.Id, ct)).First(d => d.Id == deviceId);
            await _devices.MarkSynchronizedAsync(deviceId, read.Value, _clock.UtcNow, ct);
            if (request.Kind == RpDeviceKind.SmartSwitch)
            {
                var defaults = await _commands.GetAsync(deviceId, ct);
                await _commands.SaveAsync(defaults, _clock.UtcNow, ct);
            }
            await _requests.SetStatusAsync(request.Id, RpPairingRequestStatus.Added, _clock.UtcNow, ct);
            _hub.SetState(server.Id, request.EntityId, read.Value);
            _hub.SetAvailability(server.Id, request.EntityId, DeviceAvailability.Available, null);
            _hub.NotifyDevicesChanged(server.Id);
            _hub.NotifyPairingRequestsChanged();
            return RpResult.Success with { Message = $"Устройство «{device.Name}» проверено и добавлено на сервер «{server.Name}»." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _diag?.Record("Rust+ сопряжение устройства", SourceState.Error, ex.GetType().Name + ": " + ex.Message);
            return RpResult.Fail(ex.GetType().Name, "Не удалось проверить и добавить устройство: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<RpResult<RpDeviceState>> ReadAsync(PairedServer server, RpDevicePairingRequest request, CancellationToken ct)
    {
        if (_manager.IsConnected && _manager.Server?.Id == server.Id)
            return await _manager.ReadDeviceAsync(request.EntityId, request.Kind, ct);

        var token = _servers.GetToken(server.Id);
        if (token is null) return RpResult<RpDeviceState>.Fail("no_token", "Нет сохранённого токена сервера; выполните сопряжение сервера заново.");
        var gateway = _factory();
        try
        {
            var connected = await gateway.ConnectAsync(_servers.BuildCredentials(server) ?? new RpCredentials(server.Ip, server.Port, server.PlayerId, token.Value), ct);
            if (!connected.Ok) return RpResult<RpDeviceState>.Fail(connected.Code, connected.Message);
            return await gateway.GetDeviceAsync(request.EntityId, request.Kind, ct);
        }
        finally { try { await gateway.DisposeAsync(); } catch (Exception) { } }
    }

    private async Task<RpResult> FailAsync(RpDevicePairingRequest request, string code, string message, CancellationToken ct)
    {
        await _requests.SetErrorAsync(request.Id, message, _clock.UtcNow, ct);
        _hub.NotifyPairingRequestsChanged();
        _diag?.Record("Rust+ сопряжение устройства", SourceState.NoResponse, code + ": " + message);
        return RpResult.Fail(code, message);
    }

    /// <summary>Только локально скрывает карточку. Протокол Rust+ не имеет команды отклонения уже выполненного сопряжения.</summary>
    public async Task<RpResult> DismissAsync(long requestId, CancellationToken ct = default)
    {
        var request = await _requests.GetAsync(requestId, ct);
        if (request is null || request.Status != RpPairingRequestStatus.Pending) return RpResult.Fail("not_pending", "Карточка уже обработана.");
        await _requests.SetStatusAsync(requestId, RpPairingRequestStatus.Dismissed, _clock.UtcNow, ct);
        _hub.NotifyPairingRequestsChanged();
        return RpResult.Success with { Message = "Карточка скрыта только в Rust Companion; команда отклонения в Rust+ не отправлялась." };
    }
}
