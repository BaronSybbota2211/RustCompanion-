using RustCompanion.Core.Forecast;
using RustCompanion.Core.Notifications;
using Xunit;

namespace RustCompanion.Tests;

public class MonumentCatalogTests
{
    [Fact] public void Catalog_HasUniqueIds_AndNames()
    {
        Assert.Equal(MonumentCatalog.All.Count, MonumentCatalog.All.Select(m => m.Id).Distinct().Count());
        Assert.All(MonumentCatalog.All, m => { Assert.False(string.IsNullOrWhiteSpace(m.NameEn)); Assert.False(string.IsNullOrWhiteSpace(m.NameRu)); Assert.False(string.IsNullOrWhiteSpace(m.Group)); });
    }

    [Theory] [InlineData("airfield")] [InlineData("launch_site")] [InlineData("military_tunnels")] [InlineData("power_plant")] [InlineData("water_treatment_plant")]
    [InlineData("train_yard")] [InlineData("large_oil_rig")] [InlineData("oil_rig")] [InlineData("outpost")] [InlineData("bandit_camp")] [InlineData("the_dome")] [InlineData("missile_silo")]
    public void Catalog_ContainsKeyMonuments(string id) => Assert.NotNull(MonumentCatalog.Find(id));

    [Fact] public void CustomMonuments_AreValidated()
    {
        Assert.True(MonumentCatalog.IsValid(MonumentCatalog.CustomId("Мой РТ")));
        Assert.Equal("Мой РТ", MonumentCatalog.DisplayName(MonumentCatalog.CustomId("Мой РТ")));
        Assert.False(MonumentCatalog.IsValid("custom:")); Assert.False(MonumentCatalog.IsValid("unknown_thing"));
        Assert.False(MonumentCatalog.IsValid(MonumentCatalog.CustomId(new string('x', 61))));
    }
}

public class MonumentRespawnTests
{
    private static (EventEnv E, MonumentRespawnService S) Make(string? path = null)
    { var e = new EventEnv(path); return (e, new MonumentRespawnService(e.Db, e.Ledger, e.Notifier, e.Clock)); }

    private static async Task Press(EventEnv e, MonumentRespawnService s, long server, string m, double minutesFromT0)
    { e.Clock.UtcNow = EventEnv.T0.AddMinutes(minutesFromT0); await s.PressAsync(server, m); }

    [Fact] public async Task PressRequiresTracking_AndValidId()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PressAsync(srv, "airfield"));
        await Assert.ThrowsAsync<ArgumentException>(() => s.TrackAsync(srv, "no_such_monument"));
        await s.TrackAsync(srv, "airfield"); await s.TrackAsync(srv, "airfield");                  // повтор не создаёт дубликат
        Assert.Single(await s.ListTrackedAsync(srv));
    }

    [Fact] public async Task TwoPresses_GiveInterval_AndForecastNextRespawn()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "launch_site");
        Assert.Equal(ForecastState.Insufficient, (await s.ComputeAsync(srv, "launch_site")).State);
        await Press(e, s, srv, "launch_site", 0);
        var one = await s.ComputeAsync(srv, "launch_site");
        Assert.Equal(ForecastState.Insufficient, one.State); Assert.Equal(1, one.Observations); Assert.Null(one.Next);   // одно нажатие — интервала ещё нет
        await Press(e, s, srv, "launch_site", 50);                                                                       // второй респавн через 50 минут
        var f = await s.ComputeAsync(srv, "launch_site");
        Assert.Equal(ForecastState.Ready, f.State);
        Assert.Equal(TimeSpan.FromMinutes(50), f.MedianInterval);
        Assert.Equal(EventEnv.T0.AddMinutes(100), f.Next);                                                              // последнее нажатие + интервал
        Assert.True(f.LowSample); Assert.Equal(ForecastConfidence.Low, f.Confidence);                                   // по одному интервалу — низкая уверенность
    }

    [Fact] public async Task MoreInterval_ImprovesConfidence_AndMedianResistsOutlier()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "airfield");
        foreach (var m in new double[] { 0, 60, 120, 180, 240, 300 }) await Press(e, s, srv, "airfield", m);
        var f = await s.ComputeAsync(srv, "airfield");
        Assert.False(f.LowSample); Assert.Equal(ForecastConfidence.High, f.Confidence); Assert.Equal(EventEnv.T0.AddMinutes(360), f.Next);
        await Press(e, s, srv, "airfield", 420 + 60);                                                                    // один «длинный» интервал (пропуск) не сдвигает медиану
        Assert.Equal(TimeSpan.FromMinutes(60), (await s.ComputeAsync(srv, "airfield")).MedianInterval);
    }

    [Fact] public async Task Reset_StartsFresh_KeepsHistory_AndNewPressesCountAnew()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "power_plant");
        await Press(e, s, srv, "power_plant", 0); await Press(e, s, srv, "power_plant", 45);
        Assert.Equal(2, await s.ResetAsync(srv, "power_plant"));
        var f = await s.ComputeAsync(srv, "power_plant");
        Assert.Equal(ForecastState.Insufficient, f.State); Assert.Equal(0, f.Observations);
        Assert.Equal(2, (await s.ListPressesAsync(srv, "power_plant", false)).Count);                                    // история сохранена
        Assert.Empty(await s.ListPressesAsync(srv, "power_plant", true));
        await Press(e, s, srv, "power_plant", 200); await Press(e, s, srv, "power_plant", 260);
        Assert.Equal(TimeSpan.FromMinutes(60), (await s.ComputeAsync(srv, "power_plant")).MedianInterval);              // старые нажатия не влияют
    }

    [Fact] public async Task ServersAndMonuments_AreIndependent()
    {
        var (e, s) = Make(); var a = await e.ServerAsync("A", "1.1.1.1", 1); var b = await e.ServerAsync("B", "2.2.2.2", 2);
        await s.TrackAsync(a, "airfield"); await s.TrackAsync(a, "train_yard"); await s.TrackAsync(b, "airfield");
        await Press(e, s, a, "airfield", 0); await Press(e, s, a, "airfield", 30); await Press(e, s, b, "airfield", 0); await Press(e, s, a, "train_yard", 5);
        Assert.Equal(TimeSpan.FromMinutes(30), (await s.ComputeAsync(a, "airfield")).MedianInterval);
        Assert.Equal(1, (await s.ComputeAsync(b, "airfield")).Observations);
        Assert.Equal(1, (await s.ComputeAsync(a, "train_yard")).Observations);
        await s.ResetAsync(a, "airfield");                                                                               // сброс одного РТ не трогает остальные
        Assert.Equal(1, (await s.ComputeAsync(b, "airfield")).Observations); Assert.Equal(1, (await s.ComputeAsync(a, "train_yard")).Observations);
    }

    [Fact] public async Task DoubleClick_IsIgnored_AndPressCanBeCorrectedOrDeleted()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "junkyard");
        await Press(e, s, srv, "junkyard", 0); await Press(e, s, srv, "junkyard", 0.5);                                   // двойное нажатие
        Assert.Equal(1, (await s.ComputeAsync(srv, "junkyard")).Observations);
        await Press(e, s, srv, "junkyard", 40);
        var presses = await s.ListPressesAsync(srv, "junkyard", true);
        await s.UpdatePressAsync(presses[^1].Id, EventEnv.T0.AddMinutes(45));
        Assert.Equal(TimeSpan.FromMinutes(45), (await s.ComputeAsync(srv, "junkyard")).MedianInterval);
        await s.DeletePressAsync(presses[^1].Id);
        Assert.Equal(1, (await s.ComputeAsync(srv, "junkyard")).Observations);
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpdatePressAsync(presses[0].Id, EventEnv.T0.AddDays(2)));
    }

    [Fact] public async Task Warning_TenMinutesBefore_Once_RearmedByNewPress_NotConsumedWhenDisabled()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "military_tunnels");
        await Press(e, s, srv, "military_tunnels", 0); await Press(e, s, srv, "military_tunnels", 60);               // следующий респавн: 120
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(105); Assert.Equal(0, await s.CheckWarningsAsync());                      // рано
        e.Notifier.Choice = DeliveryChoice.Off; e.Clock.UtcNow = EventEnv.T0.AddMinutes(112);
        Assert.Equal(0, await s.CheckWarningsAsync());                                                                     // выключено — не показываем и не «сжигаем»
        e.Notifier.Choice = new DeliveryChoice(true, false, false);
        Assert.Equal(1, await s.CheckWarningsAsync()); Assert.Equal(0, await s.CheckWarningsAsync());                      // один раз
        Assert.Equal(NotifyEventIds.ForecastMonument, e.Notifier.EventIds[0]); Assert.Contains("Военные туннели", e.Notifier.Sent[0].Title);
        Assert.Contains("не гарантия", e.Notifier.Sent[0].Message);
        await Press(e, s, srv, "military_tunnels", 121);                                                                   // новый респавн → новый прогноз → новое предупреждение
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(121 + 55);
        Assert.Equal(1, await s.CheckWarningsAsync());
    }

    [Fact] public async Task UntrackedMonument_NeverWarns_AndHint_ForMissedRespawn()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "oil_rig");
        await Press(e, s, srv, "oil_rig", 0); await Press(e, s, srv, "oil_rig", 40);
        await s.UntrackAsync(srv, "oil_rig"); e.Clock.UtcNow = EventEnv.T0.AddMinutes(75);
        Assert.Equal(0, await s.CheckWarningsAsync());
        var times = new[] { EventEnv.T0, EventEnv.T0.AddMinutes(40) };
        Assert.Contains("Сброс", MonumentRespawnService.Hint(times, new ForecastResult(ForecastState.Ready, 2, "", null, null, null, null, ForecastConfidence.Low, null, null, true))!);
        var overdue = new ForecastResult(ForecastState.Overdue, 2, "", null, null, null, null, ForecastConfidence.Low, null, null, true);
        Assert.Contains("Сброс", MonumentRespawnService.Hint(times, overdue)!);
        var long4 = new[] { 0.0, 40, 80, 120, 240 }.Select(m => EventEnv.T0.AddMinutes(m)).ToList();                      // последний интервал 120 при медиане 40
        Assert.Contains("пропущен", MonumentRespawnService.Hint(long4, new ForecastResult(ForecastState.Ready, 5, "", null, null, null, null, ForecastConfidence.Medium, null, null))!);
    }

    [Fact] public async Task Overdue_OnlyAfterGracePeriod_AndStatePersistsAfterRestart()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, MonumentCatalog.CustomId("Мой РТ"));
        var id = MonumentCatalog.CustomId("Мой РТ");
        await Press(e, s, srv, id, 0); await Press(e, s, srv, id, 60);                                                      // респавн ожидается на 120
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(125);                                                                       // 5 мин после — ещё в допуске (25% интервала)
        Assert.Equal(ForecastState.Ready, (await s.ComputeAsync(srv, id)).State);
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(140);
        Assert.Equal(ForecastState.Overdue, (await s.ComputeAsync(srv, id)).State);
        var (e2, s2) = Make(e.Path); e2.Clock.UtcNow = EventEnv.T0.AddMinutes(100);
        Assert.Equal(2, (await s2.ComputeAsync(srv, id)).Observations); Assert.Single(await s2.ListTrackedAsync(srv));
    }

    [Fact] public async Task DeletedServer_RemovesTrackingAndPresses()
    {
        var (e, s) = Make(); var srv = await e.ServerAsync(); await s.TrackAsync(srv, "airfield"); await Press(e, s, srv, "airfield", 0);
        await e.Servers.DeleteAsync(srv);
        Assert.Empty(await s.ListTrackedAsync(srv)); Assert.Empty(await s.ListPressesAsync(srv, "airfield", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.TrackAsync(srv, "airfield"));
    }
}
