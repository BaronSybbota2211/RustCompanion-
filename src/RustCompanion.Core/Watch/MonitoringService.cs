using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Watch;

/// <summary>Вычисленный индикатор наблюдаемого игрока: цвет, действующий уровень достоверности и пояснение для подсказки.</summary>
public sealed record PlayerIndicatorInfo(PlayerIndicator Indicator, IdentityLevel Level, string Detail);

/// <summary>
/// Один цикл проверки: независимо по каждому игроку на каждом сервере. Запросы идут последовательно через rate limiter.
/// Уведомляется только подтверждённый выход игрока (вход, смена сервера и возвращение не уведомляются).
/// </summary>
public sealed class MonitoringService
{
    private readonly WatchRepository _repo;
    private readonly IServerQuerySource _bm;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ISettingsProvider _settings;
    private readonly IClock _clock;
    private readonly DiagnosticsService? _diag;
    private readonly Database _db;
    private readonly ISteamStatusSource? _steam;
    private readonly IGameNotifier? _game;
    private readonly PlayerDirectoryRepository? _directory;
    private readonly Dictionary<long, (SteamPlayerStatus? Status, DateTimeOffset At)> _steamCache = new();
    private readonly Dictionary<long, PlayerIndicatorInfo> _indicators = new();
    private readonly object _lock = new();

    public event Action? CycleCompleted;
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public SourceState LastSourceState { get; private set; } = SourceState.Ok;
    public string LastSourceMessage { get; private set; } = "";

    public MonitoringService(Database db, WatchRepository repo, IServerQuerySource bm, NotificationDispatcher dispatcher,
        ISettingsProvider settings, IClock clock, DiagnosticsService? diag = null,
        ISteamStatusSource? steam = null, IGameNotifier? game = null, PlayerDirectoryRepository? directory = null)
    { _db = db; _repo = repo; _bm = bm; _dispatcher = dispatcher; _settings = settings; _clock = clock; _diag = diag; _steam = steam; _game = game; _directory = directory; }

    /// <summary>Текущие индикаторы по идентификатору записи наблюдения (обновляются каждый цикл).</summary>
    public PlayerIndicatorInfo IndicatorFor(long watchId) { lock (_lock) return _indicators.TryGetValue(watchId, out var i) ? i : new PlayerIndicatorInfo(PlayerIndicator.Unknown, IdentityLevel.Unconfirmed, "Ещё не проверялся"); }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var s = _settings.Current;
        var servers = (await _repo.ListTrackedServersAsync(ct)).ToDictionary(x => x.Id);
        var groups = (await _repo.ListGroupsAsync(ct)).ToDictionary(g => g.Id);
        var all = await _repo.ListWatchAsync(null, ct);

        foreach (var w in all)
        {
            ct.ThrowIfCancellationRequested();
            if (!servers.TryGetValue(w.ServerId, out var srv) || srv.SourceKey is null) continue;

            var res = await _bm.CheckPresenceAsync(w.PlayerKey, srv.SourceKey, ct);
            _diag?.Record($"Игрок {w.Name}@{srv.Name}", res.State, res.Message);
            LastSourceState = res.State; LastSourceMessage = res.Message;

            var state = await _repo.GetStateAsync(w.Id, ct);
            var obs = res.IsOk && res.Value is not null
                ? res.Value
                : new PresenceObservation(PresenceKind.Unknown, _clock.UtcNow, IsError: res.State != SourceState.NoData);
            if (res.IsOk) LastSuccessUtc = _clock.UtcNow;
            state.LastError = res.IsOk ? null : res.Message;

            var outcome = PresenceMonitor.Apply(state, obs with { ObservedAtUtc = _clock.UtcNow }, s.AbsenceConfirmations, s.AllowUntimestampedAbsence);
            await _repo.SaveStateAsync(outcome.State, ct);

            var steamStatus = await GetSteamAsync(w, s, ct);
            var host = ServerKey.TryParse(srv.SourceKey, out var h, out var qp) ? h : srv.SourceKey; if (host == srv.SourceKey) qp = 0;
            var match = SteamMatch.Evaluate(steamStatus, host, qp);
            var level = SteamMatch.Effective(w.IdentityLevel, steamStatus);
            var indicator = PlayerIndicatorRules.Compute(level, outcome.State.Status, match);
            lock (_lock) _indicators[w.Id] = new PlayerIndicatorInfo(indicator, level, Describe(level, outcome.State.Status, match, indicator));

            foreach (var ev in outcome.Events)
            {
                var isNew = await _repo.TryMarkNotifiedAsync(ev.EventKey, ev.AtUtc, ct); // сохраняется до отправки → нет дублей после перезапуска
                if (!isNew) continue;
                await _repo.AddActivityAsync(w.Id, ev.AtUtc, ev.Kind == WatchEventKind.Join ? "join_confirmed" : "exit_confirmed",
                    ev.Kind == WatchEventKind.Exit ? $"Подтверждений отсутствия: {ev.Confirmations}" : "", ct);

                // Вход, возвращение и переход на другой сервер НЕ уведомляются: только запись в историю активности.
                if (ev.Kind != WatchEventKind.Exit) continue;
                groups.TryGetValue(w.GroupId ?? -1, out var g);
                if (!(w.NotifyLeave && (g?.NotifyLeave ?? true))) continue;
                await NotifyExitAsync(w, srv.Name, host, level, ev, ct);
            }
        }
        CycleCompleted?.Invoke();
    }

    private async Task NotifyExitAsync(WatchEntry w, string serverName, string host, IdentityLevel level, WatchEvent ev, CancellationToken ct)
    {
        var note = level == IdentityLevel.Unconfirmed ? "\n(совпадение по нику, личность не подтверждена)" : "";
        var chat = $"{w.Name} покинул {serverName}" + (level == IdentityLevel.Unconfirmed ? " (по нику, не подтверждено)" : "");
        var overlay = $"{w.Name} покинул {serverName}{note}";
        if (_game is not null)
            await _game.NotifyAsync(new GameNotification(GameNotifyKind.PlayerExit, chat, overlay, null, host), ct);
        // внешние каналы (Telegram/Discord) и Windows-уведомление — прежний механизм со своей защитой от частых повторов
        await _dispatcher.DispatchAsync(new NotificationMessage(NotifyKind.Exit, $"{w.Name} покинул {serverName}",
            $"Игрок не обнаружен на сервере после {ev.Confirmations} успешных проверок (опрос Steam Query периодический и список может быть неполным, это не гарантия выхода).\n" +
            $"Последнее подтверждённое присутствие: {(ev.LastPresentUtc is null ? "неизвестно" : ev.LastPresentUtc.Value.ToLocalTime().ToString("g"))}\nДостоверность: {level.Ru()}" +
            (w.SteamId is null ? "" : $"\nSteamID: {w.SteamId}"), $"exit:{w.Id}", w.GroupId), ct);
    }

    private async Task<SteamPlayerStatus?> GetSteamAsync(WatchEntry w, AppSettings s, CancellationToken ct)
    {
        if (_steam is null || w.SteamId is null || w.IdentityLevel == IdentityLevel.Unconfirmed) return null;
        var now = _clock.UtcNow;
        if (_steamCache.TryGetValue(w.Id, out var c) && now - c.At < TimeSpan.FromSeconds(Math.Max(30, s.SteamPollSeconds))) return c.Status;
        var r = await _steam.GetStatusAsync(w.SteamId, ct);
        _diag?.Record($"Steam {w.Name}", r.State, r.Message);
        var status = r.IsOk ? r.Value : null;               // при ошибке данные не «замораживаются»: устаревший статус не даёт зелёный/красный
        _steamCache[w.Id] = (status, now);
        if (status?.PersonaName is { Length: > 0 } nick && _directory is not null)
        { try { await _directory.RecordNameAsync(w.SteamId, nick, now, ct); } catch (Exception ex) { _diag?.Record("История ников", SourceState.Error, ex.GetType().Name); } }
        return status;
    }

    private static string Describe(IdentityLevel level, PresenceStatus status, SteamServerMatch steam, PlayerIndicator ind)
    {
        var basis = level switch
        {
            IdentityLevel.SteamConfirmed => "Steam сообщает сервер игрока",
            IdentityLevel.UserConfirmed => "ник связан вами со SteamID64; присутствие определяется по списку Steam Query",
            _ => "найден только по нику — личность не подтверждена"
        };
        var what = ind switch { PlayerIndicator.Present => "на сервере", PlayerIndicator.Absent => "подтверждённо отсутствует", _ => "статус неизвестен" };
        return $"{level.Ru()}: {what}. Основание: {basis}. Проверка списка: {status.Ru()}" + (steam == SteamServerMatch.Inconclusive ? "; данные Steam не позволяют однозначно связать игрока с этим сервером" : "") + ".";
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunCycleAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _diag?.Record("Мониторинг", SourceState.Error, ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _settings.Current.WatchPollSeconds)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
