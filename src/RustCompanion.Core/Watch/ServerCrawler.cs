using RustCompanion.Core.Models;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Watch;

/// <summary>
/// Независимый периодический опрос известных серверов (A2S) для собственной базы: списки игроков и история посещений пишутся источником при каждом опросе.
/// Защита от избыточных запросов: не чаще CrawlerPollSeconds на сервер, не более CrawlerMaxServersPerCycle за цикл, запросы идут последовательно через общий rate limiter,
/// а источник кэширует свежие снимки и сериализует запросы к одному серверу. Ограничение A2S: в списке игроков нет SteamID64 — запись ведётся по нику.
/// </summary>
public sealed class ServerCrawler
{
    private readonly PlayerDirectoryRepository _dir; private readonly WatchRepository _watch; private readonly IServerQuerySource _src;
    private readonly PresenceHistoryRepository _history; private readonly ISettingsProvider _settings; private readonly IClock _clock; private readonly DiagnosticsService? _diag;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public event Action? CycleCompleted;
    public int LastPolledCount { get; private set; }

    public ServerCrawler(PlayerDirectoryRepository dir, WatchRepository watch, IServerQuerySource src, PresenceHistoryRepository history,
        ISettingsProvider settings, IClock clock, DiagnosticsService? diag = null)
    { _dir = dir; _watch = watch; _src = src; _history = history; _settings = settings; _clock = clock; _diag = diag; }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var s = _settings.Current;
        if (!s.CrawlerEnabled) { LastPolledCount = 0; return; }
        var now = _clock.UtcNow;
        // серверы с наблюдаемыми игроками всегда входят в базу
        foreach (var t in await _watch.ListTrackedServersAsync(ct))
            if (t.SourceKey is not null) await _dir.UpsertKnownServerAsync(t.SourceKey, t.Name, now, ct);

        var due = await _dir.DueServersAsync(now, TimeSpan.FromSeconds(Math.Max(60, s.CrawlerPollSeconds)), Math.Clamp(s.CrawlerMaxServersPerCycle, 1, 200), ct);
        var polled = 0;
        foreach (var k in due)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _src.GetServerPlayersAsync(k.ServerKey, ct);
            _diag?.Record($"База серверов: {k.Name}", r.State, r.Message);
            // сбой опроса тоже отмечается, иначе недоступный сервер опрашивался бы каждый цикл
            await _dir.MarkPolledAsync(k.ServerKey, _clock.UtcNow, r.IsOk && r.Value is not null ? (r.Value.ListProvided ? r.Value.Players.Count : r.Value.Server.Players) : null, ct);
            polled++;
        }
        LastPolledCount = polled;

        if (now - _lastPrune > TimeSpan.FromHours(12))
        {
            _lastPrune = now;
            try { await _history.PruneAsync(now - TimeSpan.FromDays(Math.Max(7, s.PresenceRetentionDays)), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _diag?.Record("История наблюдений", SourceState.Error, ex.GetType().Name); }
        }
        CycleCompleted?.Invoke();
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunCycleAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _diag?.Record("База серверов", SourceState.Error, ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);       // цикл проверяет «кто пора», реальная частота задаётся CrawlerPollSeconds
            }
        }
        catch (OperationCanceledException) { }
    }
}
