using RustCompanion.Core.Data;

namespace RustCompanion.Core.Watch;

/// <summary>
/// Запись локальной истории наблюдений за игроком на сервере. Время присутствия — РАСЧЁТНОЕ: сумма интервалов между последовательными опросами,
/// в которых ник был в списке (разрыв больше допустимого начинает новую запись). Это нижняя оценка по моментам опроса, а не точное игровое время.
/// ReportedSeconds — последнее значение «времени подключения», которое сообщил сам сервер (не проверяется приложением).
/// </summary>
public sealed record PresenceRecord(string ServerKey, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Samples, double EstimatedSeconds, double? ReportedSeconds);

/// <summary>Сводка по нику за всё время наблюдений: первое и последнее обнаружение, число отдельных заходов и суммарное расчётное время.</summary>
public sealed record PresenceSummary(string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Visits, double EstimatedSeconds);

public sealed class PresenceHistoryRepository
{
    /// <summary>Допустимый разрыв между опросами внутри одной записи присутствия.</summary>
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromMinutes(4);
    private readonly Database _db;
    public PresenceHistoryRepository(Database db) { _db = db; }

    /// <summary>Записывает результат опроса: игрок, уже виденный в пределах maxGap, продолжает запись (расчётное время растёт на интервал между опросами); иначе создаётся новая.</summary>
    public Task RecordSnapshotAsync(string serverKey, IEnumerable<(string Name, double? ReportedSeconds)> players, DateTimeOffset now, TimeSpan maxGap, CancellationToken ct = default)
    {
        var distinct = players.Where(p => !string.IsNullOrWhiteSpace(p.Name)).GroupBy(p => p.Name).Select(g => g.First()).ToList();
        if (distinct.Count == 0) return Task.CompletedTask;
        return _db.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var (name, reported) in distinct)
            {
                long? id = null; DateTimeOffset last = default;
                using (var find = Sql.Cmd(c, "SELECT id,last_seen FROM presence_history WHERE server_key=@k AND name=@n ORDER BY last_seen DESC LIMIT 1", ("@k", serverKey), ("@n", name)))
                {
                    find.Transaction = tx;
                    using var r = find.ExecuteReader();
                    if (r.Read()) { id = r.GetInt64(0); last = Sql.ParseIso(r.GetValue(1))!.Value; }
                }
                if (id is not null && now >= last && now - last <= maxGap)
                {
                    using var upd = Sql.Cmd(c, "UPDATE presence_history SET last_seen=@now, samples=samples+1, est_seconds=est_seconds+@d, reported_seconds=COALESCE(@r,reported_seconds) WHERE id=@id",
                        ("@now", Sql.Iso(now)), ("@d", (now - last).TotalSeconds), ("@r", reported), ("@id", id.Value));
                    upd.Transaction = tx; upd.ExecuteNonQuery();
                }
                else if (id is null || now > last)
                {
                    using var ins = Sql.Cmd(c, "INSERT INTO presence_history(server_key,name,first_seen,last_seen,samples,est_seconds,reported_seconds) VALUES(@k,@n,@t,@t,1,0,@r)",
                        ("@k", serverKey), ("@n", name), ("@t", Sql.Iso(now)), ("@r", reported));
                    ins.Transaction = tx; ins.ExecuteNonQuery();
                }
                // now < last (часы пошли назад) — запись не меняется
            }
            tx.Commit();
        }, ct);
    }

    /// <summary>Отдельные записи присутствия ника на сервере, новые сверху.</summary>
    public Task<IReadOnlyList<PresenceRecord>> ListRecordsAsync(string serverKey, string name, int limit = 50, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<PresenceRecord>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT server_key,name,first_seen,last_seen,samples,est_seconds,reported_seconds FROM presence_history WHERE server_key=@k AND name=@n ORDER BY last_seen DESC LIMIT @l",
                ("@k", serverKey), ("@n", name), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<PresenceRecord>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }, ct);

    /// <summary>Сводка по никам сервера (по убыванию последнего обнаружения). Необязательный фильтр по подстроке ника.</summary>
    public Task<IReadOnlyList<PresenceSummary>> ListSummaryAsync(string serverKey, string? nameFilter = null, int limit = 200, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<PresenceSummary>>(c =>
        {
            using var cmd = Sql.Cmd(c, @"SELECT name, MIN(first_seen), MAX(last_seen), COUNT(*), SUM(est_seconds) FROM presence_history
                WHERE server_key=@k AND (@f IS NULL OR instr(lower(name), lower(@f)) > 0) GROUP BY name ORDER BY MAX(last_seen) DESC LIMIT @l",
                ("@k", serverKey), ("@f", string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim()), ("@l", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<PresenceSummary>();
            while (r.Read())
                list.Add(new PresenceSummary(r.GetString(0), Sql.ParseIso(r.GetValue(1))!.Value, Sql.ParseIso(r.GetValue(2))!.Value, r.GetInt32(3), r.GetDouble(4)));
            return list;
        }, ct);

    /// <summary>Удаляет записи, последний раз виденные раньше указанного момента (хранение ограничено, чтобы база не росла бесконечно).</summary>
    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "DELETE FROM presence_history WHERE last_seen < @t", ("@t", Sql.Iso(olderThan)));
            return cmd.ExecuteNonQuery();
        }, ct);

    private static PresenceRecord Read(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Sql.ParseIso(r.GetValue(2))!.Value, Sql.ParseIso(r.GetValue(3))!.Value, r.GetInt32(4), r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6));
}
