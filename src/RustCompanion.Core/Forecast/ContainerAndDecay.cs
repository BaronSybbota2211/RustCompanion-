using System.Text.RegularExpressions;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Forecast;

public sealed record ContainerTimer(long Id, string Label, long? ServerId, DateTimeOffset StartedAtUtc)
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Warn = TimeSpan.FromMinutes(1);
    public DateTimeOffset OpensAtUtc => StartedAtUtc + Duration;
    public TimeSpan Remaining(DateTimeOffset now) => OpensAtUtc - now;
}

/// <summary>Ручной таймер закрытого контейнера/события: 15 минут с момента активации, предупреждение на 14-й минуте (один раз).</summary>
public sealed class ContainerService
{
    private readonly Database _db; private readonly LedgerRepository _ledger; private readonly IEventNotifier _notifier; private readonly IClock _clock;
    public ContainerService(Database db, LedgerRepository ledger, IEventNotifier notifier, IClock clock) { _db = db; _ledger = ledger; _notifier = notifier; _clock = clock; }

    public Task<long> ActivateAsync(string label, long? serverId, CancellationToken ct = default)
    {
        label = (label ?? "").Trim();
        if (label.Length > 60) throw new ArgumentException("Название слишком длинное (до 60 символов).");
        if (label.Length == 0) label = "Контейнер";
        var at = _clock.UtcNow;
        return _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "INSERT INTO container_timers(label,server_id,started_at) VALUES(@l,@s,@a); SELECT last_insert_rowid();", ("@l", label), ("@s", serverId), ("@a", Sql.Iso(at)));
            return (long)cmd.ExecuteScalar()!;
        }, ct);
    }

    public Task<IReadOnlyList<ContainerTimer>> ListAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ContainerTimer>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,label,server_id,started_at FROM container_timers ORDER BY started_at DESC");
            using var r = cmd.ExecuteReader();
            var l = new List<ContainerTimer>();
            while (r.Read()) l.Add(new ContainerTimer(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), Sql.ParseIso(r.GetValue(3))!.Value));
            return l;
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM container_timers WHERE id=@i", ("@i", id)); cmd.ExecuteNonQuery(); }, ct);

    public async Task<int> CheckWarningsAsync(CancellationToken ct = default)
    {
        var sent = 0; var now = _clock.UtcNow;
        foreach (var t in await ListAsync(ct))
        {
            var rem = t.Remaining(now);
            if (rem > ContainerTimer.Warn || rem <= TimeSpan.Zero) continue;   // только окно 14:00–15:00; запоздавшие предупреждения не показываем
            var key = $"container:{t.Id}:{t.StartedAtUtc.Ticks}";
            if (await _ledger.IsMarkedAsync(key, ct)) continue;
            var choice = _notifier.Raise(NotifyEventIds.Container, key, $"Контейнер «{t.Label}»", "откроется примерно через 1 минуту");
            if (choice.Any) { await _ledger.TryMarkAsync(key, now, ct); sent++; }
        }
        return sent;
    }
}

public enum DecayMaterial { Wood = 0, Stone = 1, Metal = 2, Hqm = 3 }

public sealed class DecayObject
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
    public string Element { get; set; } = "";
    public DecayMaterial Material { get; set; }
    public string Grid { get; set; } = "";
    public DateTimeOffset NoticedAtUtc { get; set; }
    public double? Hp { get; set; }
    public double? MaxHp { get; set; }
    public bool Notify { get; set; }
    public bool Destroyed { get; set; }
    public long Version { get; set; }
}

public enum DecayState { Insufficient, Estimate, TimePassed, Invalid, Destroyed }

public sealed record DecayResult(DecayState State, DateTimeOffset? EstimateUtc, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
    DateTimeOffset? UpperBoundUtc, double? FullHours, string Message, IReadOnlyList<string> Assumptions);

/// <summary>
/// Приблизительное время до разрушения УЖЕ гниющего элемента. Модель из справочных источников (сторонние калькуляторы; не подтверждена по исходному коду):
/// урон от decay линеен, оставшееся время = полное время гниения материала × (HP / макс. HP) / множитель decay сервера.
/// Время полного гниения и множитель настраиваются. Состояние сервера приложение не читает: «элемент гниёт» сообщает пользователь.
/// </summary>
public static class DecayCalculator
{
    public static readonly TimeSpan TickUncertainty = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(5);

    public static double FullHours(DecayMaterial m, AppSettings s) => m switch
    {
        DecayMaterial.Wood => s.DecayHoursWood, DecayMaterial.Stone => s.DecayHoursStone, DecayMaterial.Metal => s.DecayHoursMetal, _ => s.DecayHoursHqm
    };

    public static string MaterialRu(DecayMaterial m) => m switch { DecayMaterial.Wood => "дерево", DecayMaterial.Stone => "камень", DecayMaterial.Metal => "металл", _ => "высококачественный металл" };

    public static DecayResult Calculate(DecayObject o, AppSettings s, DateTimeOffset now)
    {
        var assumptions = new List<string>
        {
            $"Время полного гниения ({MaterialRu(o.Material)}): {FullHours(o.Material, s):0.##} ч — справочное значение из сторонних источников, не подтверждено по исходному коду игры; можно изменить в настройках.",
            $"Множитель decay сервера: ×{s.DecayScale:0.##} (на ванильном сервере по справочнику Facepunch равен 1; на модифицированных может отличаться).",
            "Модель: урон от decay линеен, оставшееся время пропорционально HP/макс. HP.",
            "Состояние объекта и сервера приложение не проверяет: данные введены пользователем."
        };
        if (o.Destroyed) return new(DecayState.Destroyed, null, null, null, null, null, "Отмечен разрушенным.", assumptions);

        var full = FullHours(o.Material, s);
        if (!(full > 0) || !(s.DecayScale > 0)) return new(DecayState.Invalid, null, null, null, null, null, "Некорректные настройки времени гниения или множителя.", assumptions);
        var fullEff = full / s.DecayScale;
        var upper = o.NoticedAtUtc.AddHours(fullEff);   // не позже: объект не может гнить дольше, чем полное время от 100% HP

        if (o.Hp is null || o.MaxHp is null)
            return new(DecayState.Insufficient, null, null, null, upper, fullEff,
                "Недостаточно данных для точного времени: введите текущий HP и максимальный HP элемента. Пока известна только верхняя граница.", assumptions);
        if (o.Hp < 0 || o.MaxHp <= 0 || o.Hp > o.MaxHp)
            return new(DecayState.Invalid, null, null, null, upper, fullEff, "Некорректные HP: текущий должен быть от 0 до максимального.", assumptions);

        var fraction = o.Hp.Value / o.MaxHp.Value;
        var at = o.NoticedAtUtc.AddHours(fraction * fullEff);
        var from = at - TickUncertainty; var to = at + TickUncertainty;
        if (at <= now)
            return new(DecayState.TimePassed, at, from, to, upper, fullEff, "Расчётное время разрушения прошло. Если объект цел — данные устарели; отметьте разрушенным или обновите HP.", assumptions);
        assumptions.Add($"Разброс ±{TickUncertainty.TotalMinutes:0} мин учитывает шаг начисления decay (по справочникам ~10 мин; не подтверждено).");
        return new(DecayState.Estimate, at, from, to, upper, fullEff, "Ориентировочная оценка по введённым данным.", assumptions);
    }
}

public sealed class DecayRepository
{
    public const int MaxActive = 5;
    private static readonly Regex GridRx = new(@"^[A-Za-z]{1,2}\d{1,2}$", RegexOptions.Compiled);
    private readonly Database _db; private readonly IClock _clock;
    public DecayRepository(Database db, IClock clock) { _db = db; _clock = clock; }

    public void Validate(DecayObject o)
    {
        if (string.IsNullOrWhiteSpace(o.Label)) throw new ArgumentException("Укажите название/метку объекта.");
        if (o.Label.Length > 60) throw new ArgumentException("Название слишком длинное (до 60 символов).");
        if (string.IsNullOrWhiteSpace(o.Element)) throw new ArgumentException("Укажите тип строительного элемента.");
        if (!GridRx.IsMatch((o.Grid ?? "").Trim())) throw new ArgumentException("Квадрат карты в формате H12 или AA5.");
        if (o.NoticedAtUtc > _clock.UtcNow.AddMinutes(5)) throw new ArgumentException("Время обнаружения гниения не может быть в будущем.");
        if (o.Hp is < 0) throw new ArgumentException("HP не может быть отрицательным.");
        if (o.MaxHp is <= 0) throw new ArgumentException("Максимальный HP должен быть больше нуля.");
        if (o.Hp is { } h && o.MaxHp is { } m && h > m) throw new ArgumentException("Текущий HP не может превышать максимальный.");
    }

    private static DecayObject Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Label = r.GetString(1), Element = r.GetString(2), Material = (DecayMaterial)r.GetInt32(3), Grid = r.GetString(4),
        NoticedAtUtc = Sql.ParseIso(r.GetValue(5))!.Value, Hp = r.IsDBNull(6) ? null : r.GetDouble(6), MaxHp = r.IsDBNull(7) ? null : r.GetDouble(7),
        Notify = r.GetInt32(8) != 0, Destroyed = r.GetInt32(9) != 0, Version = r.GetInt64(10)
    };

    public Task<IReadOnlyList<DecayObject>> ListAsync(bool includeDestroyed, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<DecayObject>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT id,label,element,material,grid,noticed_at,hp,max_hp,notify,destroyed,version FROM decay_objects" + (includeDestroyed ? "" : " WHERE destroyed=0") + " ORDER BY id");
            using var r = cmd.ExecuteReader();
            var l = new List<DecayObject>(); while (r.Read()) l.Add(Map(r)); return l;
        }, ct);

    public Task<long> AddAsync(DecayObject o, CancellationToken ct = default)
    {
        Validate(o);
        return _db.RunAsync(c =>
        {
            using var cnt = Sql.Cmd(c, "SELECT COUNT(*) FROM decay_objects WHERE destroyed=0");
            if ((long)cnt.ExecuteScalar()! >= MaxActive) throw new InvalidOperationException($"Одновременно можно отслеживать не более {MaxActive} объектов. Удалите или отметьте разрушенным один из них.");
            using var cmd = Sql.Cmd(c, @"INSERT INTO decay_objects(label,element,material,grid,noticed_at,hp,max_hp,notify,destroyed,version) VALUES(@l,@e,@m,@g,@n,@h,@x,0,0,1); SELECT last_insert_rowid();",
                ("@l", o.Label.Trim()), ("@e", o.Element.Trim()), ("@m", (int)o.Material), ("@g", o.Grid.Trim().ToUpperInvariant()), ("@n", Sql.Iso(o.NoticedAtUtc)), ("@h", o.Hp), ("@x", o.MaxHp));
            return (long)cmd.ExecuteScalar()!;
        }, ct);
    }

    public Task UpdateAsync(DecayObject o, CancellationToken ct = default)
    {
        Validate(o);
        return _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"UPDATE decay_objects SET label=@l,element=@e,material=@m,grid=@g,noticed_at=@n,hp=@h,max_hp=@x,version=version+1 WHERE id=@id",
                ("@l", o.Label.Trim()), ("@e", o.Element.Trim()), ("@m", (int)o.Material), ("@g", o.Grid.Trim().ToUpperInvariant()), ("@n", Sql.Iso(o.NoticedAtUtc)), ("@h", o.Hp), ("@x", o.MaxHp), ("@id", o.Id));
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task SetNotifyAsync(long id, bool on, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE decay_objects SET notify=@n WHERE id=@id", ("@n", on ? 1 : 0), ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task MarkDestroyedAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "UPDATE decay_objects SET destroyed=1 WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(c => { using var cmd = Sql.Cmd(c, "DELETE FROM decay_objects WHERE id=@id", ("@id", id)); cmd.ExecuteNonQuery(); }, ct);
}

public sealed class DecayService
{
    private readonly DecayRepository _repo; private readonly LedgerRepository _ledger; private readonly IEventNotifier _notifier; private readonly IClock _clock; private readonly ISettingsProvider _settings;
    public DecayService(DecayRepository repo, LedgerRepository ledger, IEventNotifier notifier, IClock clock, ISettingsProvider settings)
    { _repo = repo; _ledger = ledger; _notifier = notifier; _clock = clock; _settings = settings; }

    /// <summary>Предупреждение за 5 минут — только для записей с включённым индивидуальным переключателем и только если оценка есть. Один раз на версию записи.</summary>
    public async Task<int> CheckWarningsAsync(CancellationToken ct = default)
    {
        var sent = 0; var now = _clock.UtcNow;
        foreach (var o in await _repo.ListAsync(false, ct))
        {
            if (!o.Notify) continue;
            var r = DecayCalculator.Calculate(o, _settings.Current, now);
            if (r.State != DecayState.Estimate || r.EstimateUtc is null) continue;
            if (now < r.EstimateUtc.Value - DecayCalculator.WarnBefore || now >= r.EstimateUtc.Value) continue; // старые предупреждения не повторяем
            var key = $"decay:{o.Id}:{o.Version}";
            if (await _ledger.IsMarkedAsync(key, ct)) continue;
            var mins = Math.Max(1, (int)Math.Ceiling((r.EstimateUtc.Value - now).TotalMinutes));
            var choice = _notifier.Raise(NotifyEventIds.Decay, key, $"объект «{o.Label}», {o.Grid.ToUpperInvariant()} ({DecayCalculator.MaterialRu(o.Material)})", $"до предполагаемого разрушения около {mins} мин (оценка)");
            if (choice.Any) { await _ledger.TryMarkAsync(key, now, ct); sent++; }
        }
        return sent;
    }
}
