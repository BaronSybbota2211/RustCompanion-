using System.Globalization;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Forecast;

public enum ObservedKind { Chinook = 0, PatrolHelicopter = 1, Airdrop = 2, BradleyDestroyed = 3, MonumentRespawn = 4, BradleyAppeared = 5, CargoShip = 6 }
public sealed record ObservedEvent(long Id, long ServerId, ObservedKind Kind, DateTimeOffset AtUtc, string Note);

public enum ForecastState { Insufficient, Ready, Overdue, Unavailable }
public enum ForecastConfidence { None, Low, Medium, High }

public sealed record ForecastResult(ForecastState State, int Observations, string Reason, DateTimeOffset? Last, DateTimeOffset? Next,
    DateTimeOffset? From, DateTimeOffset? To, ForecastConfidence Confidence, TimeSpan? MedianInterval, long? LastEventId, bool LowSample = false)
{
    public string ConfidenceText => LowSample ? "низкая: мало интервалов (для проверки стабильности нужно хотя бы 3)" : Confidence switch
    {
        ForecastConfidence.High => "высокая (интервалы близки)",
        ForecastConfidence.Medium => "средняя",
        ForecastConfidence.Low => "низкая (большой разброс интервалов, диапазон широкий)",
        _ => "нет оценки"
    };
}

public sealed record ForecastOptions(int MinObservations = 4, int RecentIntervals = 7, TimeSpan? DuplicateWindow = null,
    bool RequireRegular = false, double MaxSpreadRatio = 0.25, int MinIntervalsForConfidence = 3, double OverdueGraceFraction = 0)
{
    public TimeSpan Dup => DuplicateWindow ?? TimeSpan.FromMinutes(2);
    public static readonly ForecastOptions Default = new();
    /// <summary>Аэрдроп: прогноз только если интервалы действительно регулярны; иначе «прогноз недоступен».</summary>
    public static readonly ForecastOptions Airdrop = new(MinObservations: 5, RequireRegular: true, MaxSpreadRatio: 0.25);
    /// <summary>Счётчик респавна РТ: прогноз уже по двум нажатиям (один интервал), но с низкой уверенностью, пока интервалов меньше трёх.</summary>
    public static readonly ForecastOptions Monument = new(MinObservations: 2, OverdueGraceFraction: 0.25);
    /// <summary>Ручные отметки событий: уже один измеренный интервал даёт предварительную оценку (низкая уверенность).</summary>
    public static readonly ForecastOptions Preliminary = new(MinObservations: 2, OverdueGraceFraction: 0.25);
}

/// <summary>Устойчивая к выбросам оценка по медиане недавних интервалов. Никаких зашитых интервалов и универсальной погрешности.</summary>
public static class ForecastEngine
{
    private static double Median(IReadOnlyList<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        var n = s.Count;
        return n == 0 ? 0 : n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    public static ForecastResult Compute(IEnumerable<ObservedEvent> events, DateTimeOffset now, ForecastOptions opt)
    {
        // дедупликация случайных двойных нажатий
        var kept = new List<ObservedEvent>();
        foreach (var e in events.OrderBy(x => x.AtUtc))
            if (kept.Count == 0 || e.AtUtc - kept[^1].AtUtc >= opt.Dup) kept.Add(e);
        var n = kept.Count;
        var last = n > 0 ? kept[^1] : null;
        ForecastResult Fail(ForecastState st, string reason) => new(st, n, reason, last?.AtUtc, null, null, null, ForecastConfidence.None, null, last?.Id);

        if (n == 0) return Fail(ForecastState.Insufficient, "Наблюдений пока нет.");
        if (n < opt.MinObservations)
            return Fail(ForecastState.Insufficient, $"Недостаточно данных: {n} из {opt.MinObservations} наблюдений.");

        var intervals = new List<double>();
        for (var i = 1; i < n; i++) intervals.Add((kept[i].AtUtc - kept[i - 1].AtUtc).TotalMinutes);
        var recent = intervals.Skip(Math.Max(0, intervals.Count - opt.RecentIntervals)).ToList();
        var med = Median(recent);
        var mad = Median(recent.Select(x => Math.Abs(x - med)).ToList());
        var scaled = 1.4826 * mad;
        var ratio = med > 0 ? scaled / med : double.PositiveInfinity;

        if (opt.RequireRegular && ratio > opt.MaxSpreadRatio)
            return Fail(ForecastState.Unavailable, $"Прогноз недоступен: интервалы между наблюдениями нерегулярны (разброс {ratio:P0}), событие нельзя считать периодичным.");

        var conf = ratio <= 0.08 && recent.Count >= 5 ? ForecastConfidence.High : ratio <= 0.2 ? ForecastConfidence.Medium : ForecastConfidence.Low;
        var lowSample = recent.Count < opt.MinIntervalsForConfidence;
        if (lowSample) conf = ForecastConfidence.Low;
        double lo, hi;
        if (conf == ForecastConfidence.Low) { lo = recent.Min(); hi = recent.Max(); }       // при большом разбросе — широкий диапазон
        else { lo = med - 2 * scaled; hi = med + 2 * scaled; }
        lo = Math.Max(lo, 1);
        hi = Math.Max(hi, lo);

        var t0 = last!.AtUtc;
        var next = t0.AddMinutes(med);
        var from = t0.AddMinutes(lo); var to = t0.AddMinutes(hi);
        if (now > to + TimeSpan.FromMinutes(med * opt.OverdueGraceFraction))
            return new ForecastResult(ForecastState.Overdue, n, "Ожидаемое окно прошло — событие, возможно, не зафиксировано. Нажмите кнопку при следующем наблюдении.",
                t0, next, from, to, conf, TimeSpan.FromMinutes(med), last.Id, lowSample);
        return new ForecastResult(ForecastState.Ready, n, lowSample ? $"Прогноз по {recent.Count} интервалу(ам): данных мало, возможна ошибка." : "", t0, next, from, to, conf, TimeSpan.FromMinutes(med), last.Id, lowSample);
    }
}

public sealed class ObservedRepository
{
    private readonly Database _db;
    public ObservedRepository(Database db) { _db = db; }

    public Task<long> AddAsync(long serverId, ObservedKind kind, DateTimeOffset at, string note = "", CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var chk = Sql.Cmd(c, "SELECT 1 FROM servers WHERE id=@s", ("@s", serverId));
            if (chk.ExecuteScalar() is null) throw new InvalidOperationException("Сервер не найден (возможно, удалён).");
            using var cmd = Sql.Cmd(c, "INSERT INTO observed_events(server_id,kind,at,note) VALUES(@s,@k,@a,@n); SELECT last_insert_rowid();",
                ("@s", serverId), ("@k", (int)kind), ("@a", Sql.Iso(at)), ("@n", note ?? ""));
            return (long)cmd.ExecuteScalar()!;
        }, ct);

    public Task<IReadOnlyList<ObservedEvent>> ListAsync(long serverId, ObservedKind kind, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ObservedEvent>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,server_id,kind,at,note FROM observed_events WHERE server_id=@s AND kind=@k ORDER BY at", ("@s", serverId), ("@k", (int)kind));
            using var r = cmd.ExecuteReader();
            var l = new List<ObservedEvent>();
            while (r.Read()) l.Add(new ObservedEvent(r.GetInt64(0), r.GetInt64(1), (ObservedKind)r.GetInt32(2), Sql.ParseIso(r.GetValue(3))!.Value, r.GetString(4)));
            return l;
        }, ct);

    public Task<IReadOnlyList<long>> ServersWithAsync(ObservedKind kind, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<long>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT DISTINCT server_id FROM observed_events WHERE kind=@k", ("@k", (int)kind));
            using var r = cmd.ExecuteReader();
            var l = new List<long>(); while (r.Read()) l.Add(r.GetInt64(0)); return l;
        }, ct);

    public Task UpdateAsync(long id, DateTimeOffset at, string note, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE observed_events SET at=@a,note=@n WHERE id=@id", ("@a", Sql.Iso(at)), ("@n", note ?? ""), ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM observed_events WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    // настройки конкретного сервера (не смешиваются между серверами)
    public Task<string?> GetPrefAsync(long serverId, string key, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "SELECT value FROM server_prefs WHERE server_id=@s AND key=@k", ("@s", serverId), ("@k", key)); return cmd.ExecuteScalar() as string; }, ct);

    public Task SetPrefAsync(long serverId, string key, string value, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO server_prefs(server_id,key,value) VALUES(@s,@k,@v) ON CONFLICT(server_id,key) DO UPDATE SET value=@v", ("@s", serverId), ("@k", key), ("@v", value));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeletePrefAsync(long serverId, string key, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM server_prefs WHERE server_id=@s AND key=@k", ("@s", serverId), ("@k", key)); cmd.ExecuteNonQuery(); }, ct);
}

/// <summary>Прогнозы по ручным отметкам (Чинук, вертолёт, Bradley, грузовой корабль, аэрдроп). Предупреждение — за настраиваемое время, один раз на прогноз.</summary>
public sealed class ForecastService
{
    public static readonly ObservedKind[] WarnedKinds = { ObservedKind.Chinook, ObservedKind.PatrolHelicopter, ObservedKind.Airdrop, ObservedKind.BradleyAppeared, ObservedKind.CargoShip };
    private readonly ObservedRepository _obs; private readonly LedgerRepository _ledger; private readonly IEventNotifier _notifier; private readonly IClock _clock; private readonly ISettingsProvider? _settings;
    public ForecastService(ObservedRepository obs, LedgerRepository ledger, IEventNotifier notifier, IClock clock, ISettingsProvider? settings = null)
    { _obs = obs; _ledger = ledger; _notifier = notifier; _clock = clock; _settings = settings; }

    public TimeSpan WarnBefore => TimeSpan.FromMinutes(Math.Clamp(_settings?.Current.ForecastLeadMinutes ?? 10, 1, 120));

    public static ForecastOptions OptionsFor(ObservedKind k) => k == ObservedKind.Airdrop ? ForecastOptions.Airdrop : ForecastOptions.Preliminary;
    public static string EventIdFor(ObservedKind k) => k switch
    {
        ObservedKind.Chinook => NotifyEventIds.ForecastChinook, ObservedKind.PatrolHelicopter => NotifyEventIds.ForecastHeli,
        ObservedKind.BradleyAppeared => NotifyEventIds.ForecastBradleyAppear, ObservedKind.CargoShip => NotifyEventIds.ForecastCargo, _ => NotifyEventIds.ForecastAirdrop
    };
    public static string NameFor(ObservedKind k) => k switch
    {
        ObservedKind.Chinook => "Чинук", ObservedKind.PatrolHelicopter => "Патрульный вертолёт", ObservedKind.Airdrop => "Аэрдроп",
        ObservedKind.BradleyAppeared => "Bradley", ObservedKind.CargoShip => "Грузовой корабль", _ => "Bradley"
    };

    public async Task<ForecastResult> ComputeAsync(long serverId, ObservedKind kind, CancellationToken ct = default) =>
        ForecastEngine.Compute(await _obs.ListAsync(serverId, kind, ct), _clock.UtcNow, OptionsFor(kind));

    public async Task<int> CheckWarningsAsync(CancellationToken ct = default)
    {
        var sent = 0; var now = _clock.UtcNow; var lead = WarnBefore;
        foreach (var kind in WarnedKinds)
            foreach (var serverId in await _obs.ServersWithAsync(kind, ct))
            {
                var f = await ComputeAsync(serverId, kind, ct);
                if (f.State != ForecastState.Ready || f.Next is null) continue;
                if (now < f.Next.Value - lead || now >= f.Next.Value) continue;          // только в окне «за N минут … до расчётного времени»
                var key = $"forecast:{(int)kind}:{serverId}:{f.LastEventId}";            // новая отметка → новый ключ (прогноз пересчитан)
                if (await _ledger.IsMarkedAsync(key, ct)) continue;
                var mins = Math.Max(1, (int)Math.Ceiling((f.Next.Value - now).TotalMinutes));
                var choice = _notifier.Raise(EventIdFor(kind), key, NameFor(kind), $"ориентировочно через {mins} мин (прогноз по вашим отметкам, не гарантия)");
                if (choice.Any) { await _ledger.TryMarkAsync(key, now, ct); sent++; }   // выключенное уведомление не «сгорает»
            }
        return sent;
    }
}

public enum BradleyPhase { NoCycle, IntervalNotSet, Counting, InWindow, WindowEnded }

public sealed record BradleyStatus(BradleyPhase Phase, DateTimeOffset? DestroyedAtUtc, long? CycleId, int? MinMinutes, int? MaxMinutes,
    DateTimeOffset? EarliestUtc, DateTimeOffset? LatestUtc, string Message);

/// <summary>Bradley: отсчёт запускается ТОЛЬКО кнопкой «Bradley уничтожен». Интервал не зашит — задаётся пользователем для каждого сервера.</summary>
public sealed class BradleyService
{
    public const string MinKey = "bradley.min_minutes", MaxKey = "bradley.max_minutes";
    private readonly ObservedRepository _obs; private readonly LedgerRepository _ledger; private readonly IEventNotifier _notifier; private readonly IClock _clock;
    public BradleyService(ObservedRepository obs, LedgerRepository ledger, IEventNotifier notifier, IClock clock) { _obs = obs; _ledger = ledger; _notifier = notifier; _clock = clock; }

    public Task<long> MarkDestroyedAsync(long serverId, CancellationToken ct = default) =>
        _obs.AddAsync(serverId, ObservedKind.BradleyDestroyed, _clock.UtcNow, "", ct);   // точное время нажатия, новый цикл закрывает предыдущий

    public async Task SetIntervalAsync(long serverId, int minMinutes, int? maxMinutes, CancellationToken ct = default)
    {
        if (minMinutes < 1 || minMinutes > 24 * 60) throw new ArgumentException("Интервал — от 1 до 1440 минут.");
        if (maxMinutes is { } mx && (mx < minMinutes || mx > 24 * 60)) throw new ArgumentException("Максимум не может быть меньше минимума и больше 1440 минут.");
        await _obs.SetPrefAsync(serverId, MinKey, minMinutes.ToString(CultureInfo.InvariantCulture), ct);
        if (maxMinutes is null) await _obs.DeletePrefAsync(serverId, MaxKey, ct);
        else await _obs.SetPrefAsync(serverId, MaxKey, maxMinutes.Value.ToString(CultureInfo.InvariantCulture), ct);
    }

    public async Task<BradleyStatus> GetStatusAsync(long serverId, CancellationToken ct = default)
    {
        var events = await _obs.ListAsync(serverId, ObservedKind.BradleyDestroyed, ct);
        var last = events.LastOrDefault();
        int? min = int.TryParse(await _obs.GetPrefAsync(serverId, MinKey, ct), out var a) ? a : null;
        int? max = int.TryParse(await _obs.GetPrefAsync(serverId, MaxKey, ct), out var b) ? b : null;
        if (last is null) return new(BradleyPhase.NoCycle, null, null, min, max, null, null, "Отсчёт не запущен: нажмите «Bradley уничтожен».");
        if (min is null) return new(BradleyPhase.IntervalNotSet, last.AtUtc, last.Id, null, null, null, null, "Уничтожение записано, но интервал респавна для этого сервера не задан — задайте его (универсального значения нет).");
        var earliest = last.AtUtc.AddMinutes(min.Value);
        var latest = last.AtUtc.AddMinutes(max ?? min.Value);
        var now = _clock.UtcNow;
        if (now < earliest) return new(BradleyPhase.Counting, last.AtUtc, last.Id, min, max, earliest, latest, "Идёт расчётный отсчёт (оценка по заданному вами интервалу).");
        if (now <= latest && latest > earliest) return new(BradleyPhase.InWindow, last.AtUtc, last.Id, min, max, earliest, latest, "Расчётное окно респавна началось (появление не подтверждено).");
        return new(BradleyPhase.WindowEnded, last.AtUtc, last.Id, min, max, earliest, latest, "Расчётный интервал завершился. Это не подтверждает, что Bradley появился.");
    }

    public async Task<int> CheckWarningsAsync(CancellationToken ct = default)
    {
        var sent = 0; var now = _clock.UtcNow;
        foreach (var serverId in await _obs.ServersWithAsync(ObservedKind.BradleyDestroyed, ct))
        {
            var st = await GetStatusAsync(serverId, ct);
            if (st.Phase != BradleyPhase.Counting || st.EarliestUtc is null) continue;
            if (now < st.EarliestUtc.Value - TimeSpan.FromMinutes(10)) continue;
            var key = $"bradley:{serverId}:{st.CycleId}";
            if (await _ledger.IsMarkedAsync(key, ct)) continue;
            var mins = Math.Max(1, (int)Math.Ceiling((st.EarliestUtc.Value - now).TotalMinutes));
            var choice = _notifier.Raise(NotifyEventIds.BradleyRespawn, key, "Bradley", $"расчётное время респавна через {mins} мин (оценка, не подтверждение)");
            if (choice.Any) { await _ledger.TryMarkAsync(key, now, ct); sent++; }
        }
        return sent;
    }
}
