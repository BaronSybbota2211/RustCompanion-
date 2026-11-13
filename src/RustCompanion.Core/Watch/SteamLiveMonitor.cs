using System.Collections.Concurrent;
using System.Net;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.Watch;

public enum LivePresence { Unknown, OnServer, OtherServer, InRustServerUnknown, NotInRust, OnlineGameHidden, Hidden, Error }

/// <summary>Текущее состояние наблюдаемого игрока по данным Steam (онлайн-статус профиля и игровой сервер).</summary>
public sealed record LiveStatus(LivePresence Presence, string Text, string Detail, DateTimeOffset AtUtc);

/// <summary>
/// Быстрая проверка всех наблюдаемых игрок со SteamID64: один пакетный запрос Steam Web API каждые 3 секунды на всех игроков.
/// Не зависит от списка игроков сервера (он может быть поддельным). Steam сам обновляет данные профиля с задержкой, поэтому реальная точность — секунды-десятки секунд;
/// «на сервере» показывается только если профиль и сведения об игре открыты.
/// </summary>
public sealed class SteamLiveMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private readonly WatchRepository _repo; private readonly ISteamStatusSource _steam; private readonly IServerQuerySource _servers;
    private readonly IClock _clock; private readonly DiagnosticsService? _diag;
    private readonly ConcurrentDictionary<long, LiveStatus> _status = new();
    private readonly ConcurrentDictionary<string, (int? GamePort, IReadOnlyCollection<string> Ips, DateTimeOffset At)> _serverInfo = new();
    public event Action? Updated;

    public SteamLiveMonitor(WatchRepository repo, ISteamStatusSource steam, IServerQuerySource servers, IClock clock, DiagnosticsService? diag = null)
    { _repo = repo; _steam = steam; _servers = servers; _clock = clock; _diag = diag; }

    public LiveStatus? For(long watchId) => _status.TryGetValue(watchId, out var s) ? s : null;

    public static LivePresence Classify(SteamPlayerStatus? st, SteamServerMatch match) => st switch
    {
        null => LivePresence.Unknown,
        _ when match == SteamServerMatch.OnThisServer => LivePresence.OnServer,
        _ when match == SteamServerMatch.OnOtherServer => LivePresence.OtherServer,
        { InRust: true } => LivePresence.InRustServerUnknown,
        { InRust: false } => LivePresence.NotInRust,
        { InRust: null, PersonaState: > 0 } => LivePresence.OnlineGameHidden,     // в сети, но Steam не показывает, во что играет
        { InRust: null, ProfilePublic: true } => LivePresence.NotInRust,           // профиль открыт, игры нет, статус «не в сети»
        _ => LivePresence.Hidden
    };

    public static (string Text, string Detail) Describe(LivePresence p, SteamPlayerStatus? st) => p switch
    {
        LivePresence.OnServer => ("На сервере", "Steam сообщает, что игрок играет на этом сервере."),
        LivePresence.OtherServer => ("Играет на другом сервере", "Steam сообщает другой сервер: " + (st?.ReportedServer ?? "?")),
        LivePresence.InRustServerUnknown => ("В Rust (сервер не указан)", "Игрок в Rust, но Steam не сообщает, на каком сервере (в основном — сервер скрыт или игрок в главном меню)."),
        LivePresence.NotInRust => st is { PersonaState: 0 } ? ("Не в сети", "Steam показывает игрока «не в сети».") : ("Не в Rust", "Игрок в Steam, но запущена другая игра или Rust закрыт."),
        LivePresence.OnlineGameHidden => ("В сети в Steam, игра скрыта", "Игрок онлайн, но Steam не показывает, запущен ли Rust и на каком сервере: у него закрыты «Сведения об играх». Чтобы статус «на сервере» работал, игрок должен открыть их в Steam (Профиль → Настройки приватности → Сведения об играх → Открытые)."),
        LivePresence.Hidden => ("Статус скрыт", st is { ProfilePublic: false } ? "Профиль закрыт: Steam не показывает игру." : "Игрок скрыл сведения об игре: определить онлайн нельзя."),
        _ => ("Нет данных", "")
    };

    private async Task<(int? GamePort, IReadOnlyCollection<string> Ips)> InfoAsync(string sourceKey, CancellationToken ct)
    {
        if (_serverInfo.TryGetValue(sourceKey, out var c) && _clock.UtcNow - c.At < TimeSpan.FromMinutes(10)) return (c.GamePort, c.Ips);
        int? gp = null; var ips = new List<string>();
        if (ServerKey.TryParse(sourceKey, out var host, out _))
        {
            ips.Add(host);
            if (!IPAddress.TryParse(host, out _))
                try { foreach (var a in await Dns.GetHostAddressesAsync(host, ct)) ips.Add(a.ToString()); } catch (Exception) { }
            var d = await _servers.GetServerDetailsAsync(sourceKey, ct);
            if (d.IsOk) gp = d.Value?.GamePort;
        }
        _serverInfo[sourceKey] = (gp, ips, _clock.UtcNow);
        return (gp, ips);
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var servers = (await _repo.ListTrackedServersAsync(ct)).ToDictionary(x => x.Id);
        var watched = (await _repo.ListWatchAsync(null, ct)).Where(w => SteamClient.IsValidSteamId64(w.SteamId)).ToList();
        if (watched.Count == 0) return;
        var now = _clock.UtcNow;
        var r = await _steam.GetStatusesAsync(watched.Select(w => w.SteamId!).Distinct().ToList(), ct);
        if (!r.IsOk || r.Value is null)
        {
            var text = r.State switch { SourceState.NotConfigured => "Нужен ключ Steam Web API (Настройки)", SourceState.AccessDenied => "Steam отклонил ключ API", SourceState.RateLimited => "Лимит запросов Steam", _ => "Steam не отвечает" };
            foreach (var w in watched) _status[w.Id] = new LiveStatus(LivePresence.Error, text, r.Message, now);
            _diag?.Record("Наблюдение (Steam, 3 с)", r.State, r.Message);
            Updated?.Invoke(); return;
        }
        var byId = r.Value.ToDictionary(x => x.SteamId, x => x);
        foreach (var w in watched)
        {
            byId.TryGetValue(w.SteamId!, out var st);
            var match = SteamServerMatch.NotChecked;
            if (st is not null && servers.TryGetValue(w.ServerId, out var srv) && srv.SourceKey is not null && ServerKey.TryParse(srv.SourceKey, out _, out var qp))
            {
                var (gp, ips) = await InfoAsync(srv.SourceKey, ct);
                match = SteamMatch.Evaluate(st, ips, qp, gp);
            }
            var p = Classify(st, match); var (txt, det) = Describe(p, st);
            _status[w.Id] = new LiveStatus(p, txt, det, now);
        }
        _diag?.Record("Наблюдение (Steam, 3 с)", SourceState.Ok, $"проверено игроков: {watched.Count}");
        Updated?.Invoke();
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunCycleAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _diag?.Record("Наблюдение (Steam, 3 с)", SourceState.Error, ex.GetType().Name); }
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
