using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RustCompanion.Core;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Models;
using RustCompanion.Core.Overlay;

namespace RustCompanion.App.Services;

/// <summary>
/// Данные для постоянной панели оверлея. Используются те же сервисы и таблицы, что и на вкладках приложения (отдельного механизма данных нет);
/// кэш обновляется раз в несколько секунд, а обратный отсчёт считается каждую секунду по сохранённым временным меткам. Сервер — тот, что выбран для горячих клавиш.
/// </summary>
public sealed class HudDataService
{
    private readonly ObservedRepository _obs; private readonly BradleyService _bradley; private readonly ContainerService _containers; private readonly MonumentRespawnService _monuments;
    private readonly ISettingsProvider _settings; private readonly IClock _clock;
    private readonly List<HudEntry> _cache = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, List<ObservedEvent>> _events = new();
    private readonly Dictionary<string, ObservedKind> _kinds = new()
    { [OverlayHud.Chinook] = ObservedKind.Chinook, [OverlayHud.Heli] = ObservedKind.PatrolHelicopter, [OverlayHud.BradleyAppear] = ObservedKind.BradleyAppeared, [OverlayHud.Airdrop] = ObservedKind.Airdrop, [OverlayHud.Cargo] = ObservedKind.CargoShip };
    private readonly List<(string Name, DateTimeOffset? Next)> _monumentNext = new();
    private DateTimeOffset? _bradleyEarliest; private List<ContainerTimer> _containerList = new();
    private bool _dirty = true;

    public HudDataService(ObservedRepository obs, BradleyService bradley, ContainerService containers, MonumentRespawnService monuments, ISettingsProvider settings, IClock clock)
    { _obs = obs; _bradley = bradley; _containers = containers; _monuments = monuments; _settings = settings; _clock = clock; }

    public void Invalidate() => _dirty = true;
    public bool NeedsRefresh => _dirty || _clock.UtcNow - _loadedAt > TimeSpan.FromSeconds(10);

    public async Task RefreshAsync()
    {
        _dirty = false; _loadedAt = _clock.UtcNow;
        var s = _settings.Current; var server = s.HotkeyServerId; var sel = OverlayHud.ParseSelection(s.OverlayHudItems);
        _events.Clear(); _monumentNext.Clear(); _bradleyEarliest = null; _containerList = new();
        if (sel.Count == 0) return;
        if (server > 0)
        {
            foreach (var (id, kind) in _kinds) if (sel.Contains(id)) _events[id] = (await _obs.ListAsync(server, kind)).ToList();
            if (sel.Contains(OverlayHud.BradleyDestroyed))
            { var st = await _bradley.GetStatusAsync(server); _bradleyEarliest = st.Phase == BradleyPhase.Counting ? st.EarliestUtc : null; }
            if (sel.Contains(OverlayHud.Monuments))
                foreach (var t in await _monuments.ListTrackedAsync(server)) { var f = await _monuments.ComputeAsync(server, t.MonumentId); _monumentNext.Add((t.Name, f.State == ForecastState.Ready ? f.Next : null)); }
        }
        if (sel.Contains(OverlayHud.Containers)) _containerList = (await _containers.ListAsync()).ToList();
    }

    public IReadOnlyList<string> Lines()
    {
        var now = _clock.UtcNow; var s = _settings.Current; var sel = OverlayHud.ParseSelection(s.OverlayHudItems);
        var entries = new List<HudEntry>();
        string Title(string id) => OverlayHud.Catalog.First(c => c.Id == id).Title.Split('(')[0].Trim();
        if (s.HotkeyServerId <= 0 && sel.Any(i => _kinds.ContainsKey(i) || i is OverlayHud.BradleyDestroyed or OverlayHud.Monuments))
            entries.Add(new HudEntry("srv", "Сервер для отметок не выбран", null, "вкладка «Горячие клавиши»"));
        else
        {
            foreach (var (id, list) in _events)
            {
                var f = ForecastEngine.Compute(list, now, ForecastService.OptionsFor(_kinds[id]));
                entries.Add(new HudEntry(id, Title(id), f.State == ForecastState.Ready ? f.Next : null, f.State == ForecastState.Overdue ? "окно прошло" : f.Observations < 2 ? "нужно 2 отметки" : null));
            }
            if (sel.Contains(OverlayHud.BradleyDestroyed)) entries.Add(new HudEntry("bd", "Bradley (респавн)", _bradleyEarliest, "нет отсчёта"));
            foreach (var (name, next) in _monumentNext) entries.Add(new HudEntry("m:" + name, name, next, "нет прогноза"));
        }
        foreach (var c in _containerList) entries.Add(new HudEntry("c" + c.Id, "Контейнер «" + c.Label + "»", c.OpensAtUtc));
        return OverlayHud.Build(entries, now);
    }
}
