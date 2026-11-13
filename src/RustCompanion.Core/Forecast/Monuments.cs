using RustCompanion.Core.Data;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Forecast;

public sealed record MonumentInfo(string Id, string NameEn, string NameRu, string Group, string? Note = null)
{
    public string Display => $"{NameRu} ({NameEn})";
}

/// <summary>
/// Справочник РТ (монументов) Rust. Составлен по вики Facepunch (раздел The Map), Rust Wiki на Fandom (страница Monument) и сообщественным гайдам.
/// Список меняется между версиями игры, поэтому есть «свой РТ» (id вида custom:Название). Русские названия — рабочие переводы, внутриигровая локализация может отличаться.
/// Намеренно не включены: Cargo Ship и Patrol Helicopter (события, не РТ), Caves, Train Tunnel Network, Road, Crow's Nest.
/// </summary>
public static class MonumentCatalog
{
    public const string CustomPrefix = "custom:";
    private const string Safe = "Безопасные зоны", Quarry = "Карьеры", Sea = "Море", Main = "Крупные и ключевые РТ", Small = "Малые РТ", Rural = "Фермы и посёлки";

    public static readonly IReadOnlyList<MonumentInfo> All = new MonumentInfo[]
    {
        new("airfield", "Airfield", "Аэродром", Main),
        new("giant_excavator_pit", "Giant Excavator Pit", "Гигантский экскаватор", Main),
        new("launch_site", "Launch Site", "Космодром (стартовая площадка)", Main),
        new("military_tunnels", "Military Tunnels", "Военные туннели", Main),
        new("power_plant", "Power Plant", "Электростанция", Main),
        new("train_yard", "Train Yard", "Железнодорожное депо", Main),
        new("water_treatment_plant", "Water Treatment Plant", "Водоочистная станция", Main),
        new("abandoned_military_base", "Abandoned Military Base", "Заброшенная военная база", Main),
        new("arctic_research_base", "Arctic Research Base", "Арктическая исследовательская база", Main),
        new("missile_silo", "Missile Silo", "Ракетная шахта", Main),
        new("sewer_branch", "Sewer Branch", "Канализационная ветка", Main),
        new("the_dome", "The Dome", "Купол", Main),
        new("satellite_dish", "Satellite Dish Array", "Спутниковая тарелка", Main),
        new("junkyard", "Junkyard", "Свалка", Main),
        new("harbor_large", "Harbor (Large)", "Большой порт", Main, "Вариант порта; в части источников — просто «Harbor»"),
        new("harbor_small", "Harbor (Small)", "Малый порт", Main, "Вариант порта; в части источников — просто «Harbor»"),
        new("ferry_terminal", "Ferry Terminal", "Паромный терминал", Main),
        new("radtown", "Radtown", "Радтаун", Main),
        new("abandoned_cabins", "Abandoned Cabins", "Заброшенные хижины", Small),
        new("abandoned_supermarket", "Abandoned Supermarket", "Заброшенный супермаркет", Small),
        new("lighthouse", "Lighthouse", "Маяк", Small),
        new("mining_outpost", "Mining Outpost", "Шахтёрский аванпост", Small),
        new("oxums_gas_station", "Oxum's Gas Station", "Заправка Оксума", Small),
        new("fishing_village", "Fishing Village", "Рыбацкая деревня", Small),
        new("large_barn", "Large Barn", "Большой амбар", Rural),
        new("ranch", "Ranch", "Ранчо", Rural),
        new("stables", "Stables", "Конюшни", Rural, "Встречается не во всех источниках"),
        new("jungle_ziggurat", "Jungle Ziggurat", "Джунглевый зиккурат", Small, "Встречается не во всех источниках (новые версии)"),
        new("hqm_quarry", "HQM Quarry", "Карьер МВК", Quarry),
        new("stone_quarry", "Stone Quarry", "Каменный карьер", Quarry),
        new("sulfur_quarry", "Sulfur Quarry", "Серный карьер", Quarry),
        new("oil_rig", "Oil Rig", "Малая нефтяная вышка", Sea),
        new("large_oil_rig", "Large Oil Rig", "Большая нефтяная вышка", Sea),
        new("underwater_labs", "Underwater Labs", "Подводные лаборатории", Sea),
        new("outpost", "Outpost", "Аванпост", Safe),
        new("bandit_camp", "Bandit Camp", "Лагерь бандитов", Safe),
    };

    private static readonly Dictionary<string, MonumentInfo> ById = All.ToDictionary(m => m.Id);

    public static MonumentInfo? Find(string id) => ById.TryGetValue(id, out var m) ? m : null;
    public static bool IsCustom(string id) => id.StartsWith(CustomPrefix, StringComparison.Ordinal);
    public static string CustomId(string name) => CustomPrefix + name.Trim();

    public static bool IsValid(string id) =>
        ById.ContainsKey(id) || (IsCustom(id) && id.Length > CustomPrefix.Length && id.Length <= CustomPrefix.Length + 60);

    public static string DisplayName(string id) =>
        Find(id)?.NameRu ?? (IsCustom(id) ? id[CustomPrefix.Length..] : id);
}

public sealed record TrackedMonument(long ServerId, string MonumentId, DateTimeOffset AddedAtUtc)
{
    public string Name => MonumentCatalog.DisplayName(MonumentId);
}
public sealed record MonumentPress(long Id, long ServerId, string MonumentId, DateTimeOffset AtUtc, bool Active);

/// <summary>
/// Универсальный счётчик респавна РТ. Игрок нажимает «РТ респавн» после уведомления о респавне; промежуток между двумя нажатиями — оцениваемый интервал,
/// следующий респавн = последнее нажатие + медиана интервалов. «Сброс» начинает отсчёт заново (история сохраняется, но в прогноз не входит).
/// Данные независимы по серверам и по РТ. Это оценка по вашим нажатиям, а не данные сервера.
/// </summary>
public sealed class MonumentRespawnService
{
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(10);
    private readonly Database _db; private readonly LedgerRepository _ledger; private readonly IEventNotifier _notifier; private readonly IClock _clock;
    public MonumentRespawnService(Database db, LedgerRepository ledger, IEventNotifier notifier, IClock clock) { _db = db; _ledger = ledger; _notifier = notifier; _clock = clock; }

    public Task TrackAsync(long serverId, string monumentId, CancellationToken ct = default)
    {
        if (!MonumentCatalog.IsValid(monumentId)) throw new ArgumentException("Неизвестный РТ. Выберите из списка или добавьте свой (название до 60 символов).");
        var at = _clock.UtcNow;
        return _db.RunAsync(c =>
        {
            using var chk = Sql.Cmd(c, "SELECT 1 FROM servers WHERE id=@s", ("@s", serverId));
            if (chk.ExecuteScalar() is null) throw new InvalidOperationException("Сервер не найден (возможно, удалён).");
            using var cmd = Sql.Cmd(c, "INSERT OR IGNORE INTO monument_tracking(server_id,monument_id,added_at) VALUES(@s,@m,@a)", ("@s", serverId), ("@m", monumentId), ("@a", Sql.Iso(at)));
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task UntrackAsync(long serverId, string monumentId, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM monument_tracking WHERE server_id=@s AND monument_id=@m", ("@s", serverId), ("@m", monumentId)); cmd.ExecuteNonQuery(); }, ct);

    public Task<IReadOnlyList<TrackedMonument>> ListTrackedAsync(long? serverId = null, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<TrackedMonument>>(c =>
        {
            using var cmd = serverId is null ? Sql.Cmd(c, "SELECT server_id,monument_id,added_at FROM monument_tracking ORDER BY added_at")
                                             : Sql.Cmd(c, "SELECT server_id,monument_id,added_at FROM monument_tracking WHERE server_id=@s ORDER BY added_at", ("@s", serverId));
            using var r = cmd.ExecuteReader();
            var l = new List<TrackedMonument>();
            while (r.Read()) l.Add(new TrackedMonument(r.GetInt64(0), r.GetString(1), Sql.ParseIso(r.GetValue(2))!.Value));
            return l;
        }, ct);

    public Task<long> PressAsync(long serverId, string monumentId, CancellationToken ct = default)
    {
        var at = _clock.UtcNow;
        return _db.RunAsync(c =>
        {
            using var chk = Sql.Cmd(c, "SELECT 1 FROM monument_tracking WHERE server_id=@s AND monument_id=@m", ("@s", serverId), ("@m", monumentId));
            if (chk.ExecuteScalar() is null) throw new InvalidOperationException("Сначала добавьте этот РТ в отслеживание.");
            using var cmd = Sql.Cmd(c, "INSERT INTO monument_respawns(server_id,monument_id,at,active) VALUES(@s,@m,@a,1); SELECT last_insert_rowid();", ("@s", serverId), ("@m", monumentId), ("@a", Sql.Iso(at)));
            return (long)cmd.ExecuteScalar()!;
        }, ct);
    }

    /// <summary>Сброс: пропущено два респавна подряд — прежние нажатия больше не влияют на прогноз (в истории остаются).</summary>
    public Task<int> ResetAsync(long serverId, string monumentId, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE monument_respawns SET active=0 WHERE server_id=@s AND monument_id=@m AND active=1", ("@s", serverId), ("@m", monumentId)); return cmd.ExecuteNonQuery(); }, ct);

    public Task<IReadOnlyList<MonumentPress>> ListPressesAsync(long serverId, string monumentId, bool activeOnly, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<MonumentPress>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,server_id,monument_id,at,active FROM monument_respawns WHERE server_id=@s AND monument_id=@m" + (activeOnly ? " AND active=1" : "") + " ORDER BY at",
                ("@s", serverId), ("@m", monumentId));
            using var r = cmd.ExecuteReader();
            var l = new List<MonumentPress>();
            while (r.Read()) l.Add(new MonumentPress(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Sql.ParseIso(r.GetValue(3))!.Value, r.GetInt32(4) != 0));
            return l;
        }, ct);

    public Task DeletePressAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM monument_respawns WHERE id=@i", ("@i", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task UpdatePressAsync(long id, DateTimeOffset at, CancellationToken ct = default)
    {
        if (at > _clock.UtcNow.AddMinutes(5)) throw new ArgumentException("Время нажатия не может быть в будущем.");
        return _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE monument_respawns SET at=@a WHERE id=@i", ("@a", Sql.Iso(at)), ("@i", id)); cmd.ExecuteNonQuery(); }, ct);
    }

    public async Task<ForecastResult> ComputeAsync(long serverId, string monumentId, CancellationToken ct = default)
    {
        var presses = await ListPressesAsync(serverId, monumentId, true, ct);
        var events = presses.Select(p => new ObservedEvent(p.Id, p.ServerId, ObservedKind.MonumentRespawn, p.AtUtc, ""));
        return ForecastEngine.Compute(events, _clock.UtcNow, ForecastOptions.Monument);
    }

    /// <summary>Подсказка пользователю: возможный пропуск респавна. Проверяется по уже введённым нажатиям, без выдуманных значений.</summary>
    public static string? Hint(IReadOnlyList<DateTimeOffset> activePresses, ForecastResult f)
    {
        if (f.State == ForecastState.Overdue) return "Ожидаемое время прошло, а нажатия не было. Если вы пропустили два респавна подряд — нажмите «Сброс» и начните заново с ближайшего.";
        var times = activePresses.OrderBy(x => x).ToList();
        if (times.Count == 2) return "Прогноз основан на одном интервале. Если между нажатиями вы пропустили респавн, интервал окажется вдвое длиннее реального — тогда нажмите «Сброс».";
        if (times.Count >= 4)
        {
            var iv = Enumerable.Range(1, times.Count - 1).Select(i => (times[i] - times[i - 1]).TotalMinutes).ToList();
            var prev = iv.Take(iv.Count - 1).OrderBy(x => x).ToList();
            var med = prev.Count % 2 == 1 ? prev[prev.Count / 2] : (prev[prev.Count / 2 - 1] + prev[prev.Count / 2]) / 2;
            if (med > 0 && iv[^1] > 1.8 * med) return "Последний интервал заметно длиннее прежних: возможно, один респавн был пропущен. При необходимости нажмите «Сброс».";
        }
        return null;
    }

    public async Task<int> CheckWarningsAsync(CancellationToken ct = default)
    {
        var sent = 0; var now = _clock.UtcNow;
        foreach (var t in await ListTrackedAsync(null, ct))
        {
            var f = await ComputeAsync(t.ServerId, t.MonumentId, ct);
            if (f.State != ForecastState.Ready || f.Next is null) continue;
            if (now < f.Next.Value - WarnBefore || now >= f.Next.Value) continue;
            var key = $"monument:{t.ServerId}:{t.MonumentId}:{f.LastEventId}";   // новое нажатие или сброс → новый ключ
            if (await _ledger.IsMarkedAsync(key, ct)) continue;
            var mins = Math.Max(1, (int)Math.Ceiling((f.Next.Value - now).TotalMinutes));
            var choice = _notifier.Raise(NotifyEventIds.ForecastMonument, key, t.Name, $"расчётный респавн примерно через {mins} мин (по интервалу между вашими нажатиями, не гарантия)");
            if (choice.Any) { await _ledger.TryMarkAsync(key, now, ct); sent++; }
        }
        return sent;
    }
}
