using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;

namespace RustCompanion.App.Services;

/// <summary>Таймеры приложения для команды бота «!timer»: ручные таймеры и таймеры контейнеров. Данные кэшируются и обновляются периодически, обратный отсчёт считается по меткам времени.</summary>
public sealed class AppTimerSource : ITimerSource
{
    private readonly ITimerRepository _timers; private readonly ContainerService _containers;
    private List<(string Name, DateTimeOffset Due)> _cache = new();
    public AppTimerSource(ITimerRepository timers, ContainerService containers) { _timers = timers; _containers = containers; }

    public async Task RefreshAsync()
    {
        var list = new List<(string, DateTimeOffset)>();
        foreach (var t in await _timers.ListAsync(null)) if (TimerCalculator.DueAt(t) is { } due) list.Add((t.Name, due));
        foreach (var c in await _containers.ListAsync()) list.Add(("Контейнер «" + c.Label + "»", c.OpensAtUtc));
        _cache = list;
    }

    public IReadOnlyList<(string Name, TimeSpan Remaining)> Active(DateTimeOffset now) =>
        _cache.Select(x => (x.Name, x.Due - now)).Where(x => x.Item2 > TimeSpan.FromMinutes(-1)).ToList();
}
