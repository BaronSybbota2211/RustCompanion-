using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Services;

public enum TimerPhase { Idle, Running, LeadWindow, Due }

/// <summary>Все расчёты идут по абсолютному UTC-времени: сон, смена часового пояса и полночь не ломают отсчёт.</summary>
public static class TimerCalculator
{
    public static DateTimeOffset? DueAt(TimerEntry t) =>
        t.StartedAtUtc is null || t.IntervalSeconds <= 0 ? null : t.StartedAtUtc.Value.AddSeconds(t.IntervalSeconds);

    public static TimeSpan? Remaining(TimerEntry t, DateTimeOffset now) => DueAt(t) - now;

    public static TimeSpan? Elapsed(TimerEntry t, DateTimeOffset now) => t.StartedAtUtc is null ? null : now - t.StartedAtUtc.Value;

    public static TimerPhase Phase(TimerEntry t, DateTimeOffset now)
    {
        var rem = Remaining(t, now);
        if (rem is null) return TimerPhase.Idle;
        if (rem.Value <= TimeSpan.Zero) return TimerPhase.Due;
        if (t.LeadSeconds > 0 && rem.Value.TotalSeconds <= t.LeadSeconds) return TimerPhase.LeadWindow;
        return TimerPhase.Running;
    }
}

public static class NotificationPlanner
{
    /// <summary>Возвращает события, которые нужно показать, и выставляет флаги, чтобы не дублировать уведомления.</summary>
    public static List<NotificationEvent> Evaluate(TimerEntry t, DateTimeOffset now)
    {
        var result = new List<NotificationEvent>();
        if (!t.NotifyEnabled) return result;
        var due = TimerCalculator.DueAt(t);
        if (due is null) return result;
        var rem = due.Value - now;

        if (rem <= TimeSpan.Zero)
        {
            if (!t.DueNotified)
            {
                t.DueNotified = true;
                t.LeadNotified = true; // если приложение было закрыто в «окне заранее», предупреждение уже не нужно
                result.Add(new NotificationEvent(t.Id, t.Name, "Расчётное время наступило (оценка по введённым вами данным).", true));
            }
        }
        else if (t.LeadSeconds > 0 && rem.TotalSeconds <= t.LeadSeconds && !t.LeadNotified)
        {
            t.LeadNotified = true;
            var minutes = (int)Math.Ceiling(rem.TotalMinutes);
            result.Add(new NotificationEvent(t.Id, t.Name, $"Примерно через {minutes} мин (оценка по введённым вами данным).", false));
        }
        return result;
    }
}

public interface ITimerRepository
{
    Task<IReadOnlyList<TimerEntry>> ListAsync(TrackerKind? kind, CancellationToken ct = default);
    Task<long> InsertAsync(TimerEntry t, CancellationToken ct = default);
    Task UpdateAsync(TimerEntry t, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task AddHistoryAsync(long timerId, DateTimeOffset atUtc, string action, string note, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryEntry>> HistoryAsync(long timerId, int limit, CancellationToken ct = default);
}

public sealed class TimerService
{
    private readonly ITimerRepository _repo;
    private readonly IClock _clock;
    private readonly IEventNotifier _notifier;

    public TimerService(ITimerRepository repo, IClock clock, IEventNotifier notifier)
    { _repo = repo; _clock = clock; _notifier = notifier; }

    public static void Validate(string name, int intervalSeconds, int leadSeconds)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название таймера.");
        if (name.Length > 80) throw new ArgumentException("Название слишком длинное (максимум 80 символов).");
        if (intervalSeconds <= 0) throw new ArgumentException("Интервал должен быть больше нуля.");
        if (intervalSeconds > 60 * 60 * 24 * 30) throw new ArgumentException("Интервал не может превышать 30 суток.");
        if (leadSeconds < 0) throw new ArgumentException("Время предупреждения не может быть отрицательным.");
        if (leadSeconds >= intervalSeconds) throw new ArgumentException("Предупреждение должно быть короче интервала.");
    }

    public async Task<TimerEntry> CreateAsync(TrackerKind kind, string name, int intervalSeconds, int leadSeconds, CancellationToken ct = default)
    {
        Validate(name, intervalSeconds, leadSeconds);
        var t = new TimerEntry { Kind = kind, Name = name.Trim(), IntervalSeconds = intervalSeconds, LeadSeconds = leadSeconds };
        t.Id = await _repo.InsertAsync(t, ct);
        return t;
    }

    public async Task StartAsync(TimerEntry t, string action = "start", CancellationToken ct = default)
    {
        t.StartedAtUtc = _clock.UtcNow;
        t.LeadNotified = false;
        t.DueNotified = false;
        await _repo.UpdateAsync(t, ct);
        await _repo.AddHistoryAsync(t.Id, _clock.UtcNow, action, "", ct);
    }

    public async Task ResetAsync(TimerEntry t, CancellationToken ct = default)
    {
        t.StartedAtUtc = null;
        t.LeadNotified = false;
        t.DueNotified = false;
        await _repo.UpdateAsync(t, ct);
        await _repo.AddHistoryAsync(t.Id, _clock.UtcNow, "reset", "", ct);
    }

    /// <summary>Отметка события (лут собран / уничтожен). restart=true — сразу начать новый отсчёт.</summary>
    public async Task MarkAsync(TimerEntry t, string action, bool restart, CancellationToken ct = default)
    {
        if (restart) { await StartAsync(t, action, ct); return; }
        t.StartedAtUtc = null;
        t.LeadNotified = false;
        t.DueNotified = false;
        await _repo.UpdateAsync(t, ct);
        await _repo.AddHistoryAsync(t.Id, _clock.UtcNow, action, "", ct);
    }

    public async Task SaveNoteAsync(TimerEntry t, string note, CancellationToken ct = default)
    {
        t.Note = note ?? "";
        await _repo.UpdateAsync(t, ct);
        await _repo.AddHistoryAsync(t.Id, _clock.UtcNow, "note", t.Note, ct);
    }

    public Task DeleteAsync(TimerEntry t, CancellationToken ct = default) => _repo.DeleteAsync(t.Id, ct);

    public async Task<IReadOnlyList<NotificationEvent>> TickAsync(IEnumerable<TimerEntry> timers, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var all = new List<NotificationEvent>();
        foreach (var t in timers)
        {
            var events = NotificationPlanner.Evaluate(t, now);
            if (events.Count == 0) continue;
            await _repo.UpdateAsync(t, ct); // сначала сохраняем флаги — чтобы не дублировать после перезапуска
            foreach (var e in events) _notifier.Raise(NotifyEventIds.ForTimer(t.Kind), $"timer:{t.Id}:{(e.IsDue ? "due" : "lead")}:{t.StartedAtUtc?.Ticks}", e.Title, e.Message);
            all.AddRange(events);
        }
        return all;
    }
}
