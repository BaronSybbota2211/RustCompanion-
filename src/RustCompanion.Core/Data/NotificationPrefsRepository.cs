using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Data;

public sealed class NotificationPrefsRepository
{
    private readonly Database _db;
    public NotificationPrefsRepository(Database db) { _db = db; }

    public Task<IReadOnlyList<(string Id, DeliveryChoice Choice)>> LoadAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<(string, DeliveryChoice)>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT event_id,overlay,system,external FROM notification_prefs");
            using var r = cmd.ExecuteReader();
            var l = new List<(string, DeliveryChoice)>();
            while (r.Read()) l.Add((r.GetString(0), new DeliveryChoice(r.GetInt32(1) != 0, r.GetInt32(2) != 0, r.GetInt32(3) != 0)));
            return l;
        }, ct);

    public Task SaveAsync(string eventId, DeliveryChoice c, CancellationToken ct = default) =>
        _db.RunAsync(conn =>
        {
            using var cmd = Sql.Cmd(conn, @"INSERT INTO notification_prefs(event_id,overlay,system,external) VALUES(@e,@o,@s,@x)
                ON CONFLICT(event_id) DO UPDATE SET overlay=@o,system=@s,external=@x",
                ("@e", eventId), ("@o", c.Overlay ? 1 : 0), ("@s", c.System ? 1 : 0), ("@x", c.External ? 1 : 0));
            cmd.ExecuteNonQuery();
        }, ct);
}

/// <summary>Журнал уже показанных предупреждений: один раз на событие/цикл, переживает перезапуск.</summary>
public sealed class LedgerRepository
{
    private readonly Database _db;
    public LedgerRepository(Database db) { _db = db; }

    public Task<bool> IsMarkedAsync(string key, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "SELECT 1 FROM notified_events WHERE event_key=@k", ("@k", key)); return cmd.ExecuteScalar() is not null; }, ct);

    public Task<bool> TryMarkAsync(string key, DateTimeOffset at, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "INSERT OR IGNORE INTO notified_events(event_key,at) VALUES(@k,@a)", ("@k", key), ("@a", Sql.Iso(at))); return cmd.ExecuteNonQuery() > 0; }, ct);
}
