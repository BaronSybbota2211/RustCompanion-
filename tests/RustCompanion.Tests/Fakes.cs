using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Services;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan t) => UtcNow += t;
}

public sealed class FakeNotifier : IEventNotifier
{
    public DeliveryChoice Choice { get; set; } = new(true, true, true);
    public List<(string Title, string Message)> Sent { get; } = new();
    public List<string> EventIds { get; } = new();
    public List<string> Keys { get; } = new();
    public DeliveryChoice Raise(string eventId, string key, string title, string text)
    {
        if (!Choice.Any) return DeliveryChoice.Off;
        Sent.Add((title, text)); EventIds.Add(eventId); Keys.Add(key);
        return Choice;
    }
}

public sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> _d = new();
    public string? Get(string name) => _d.GetValueOrDefault(name);
    public void Set(string name, string value) => _d[name] = value;
    public void Delete(string name) => _d.Remove(name);
}

public sealed class MemoryTimerRepo : ITimerRepository
{
    public List<TimerEntry> Items { get; } = new();
    public List<(long Id, string Action)> History { get; } = new();
    public int Updates { get; private set; }
    public Task<IReadOnlyList<TimerEntry>> ListAsync(TrackerKind? kind, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TimerEntry>>(Items.Where(i => kind is null || i.Kind == kind).ToList());
    public Task<long> InsertAsync(TimerEntry t, CancellationToken ct = default) { t.Id = Items.Count + 1; Items.Add(t); return Task.FromResult(t.Id); }
    public Task UpdateAsync(TimerEntry t, CancellationToken ct = default) { Updates++; return Task.CompletedTask; }
    public Task DeleteAsync(long id, CancellationToken ct = default) { Items.RemoveAll(i => i.Id == id); return Task.CompletedTask; }
    public Task AddHistoryAsync(long id, DateTimeOffset at, string action, string note, CancellationToken ct = default) { History.Add((id, action)); return Task.CompletedTask; }
    public Task<IReadOnlyList<HistoryEntry>> HistoryAsync(long id, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HistoryEntry>>(new List<HistoryEntry>());
}

public static class TempDb
{
    public static string NewPath() => Path.Combine(Path.GetTempPath(), "rc-test-" + Guid.NewGuid().ToString("N") + ".db");
}
