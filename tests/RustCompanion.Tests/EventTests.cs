using System.Reflection;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Services;
using Xunit;

namespace RustCompanion.Tests;

internal sealed class EventEnv
{
    public Database Db; public ObservedRepository Obs; public LedgerRepository Ledger; public ServerRepository Servers;
    public FakeClock Clock = new(); public FakeNotifier Notifier = new(); public SettingsProvider Settings = new();
    public ForecastService Forecast; public BradleyService Bradley; public ContainerService Container; public DecayRepository Decay; public DecayService DecaySvc;
    public string Path;
    public static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public EventEnv(string? path = null)
    {
        Path = path ?? TempDb.NewPath(); Db = new Database(Path); Db.Initialize();
        Obs = new ObservedRepository(Db); Ledger = new LedgerRepository(Db); Servers = new ServerRepository(Db);
        Forecast = new ForecastService(Obs, Ledger, Notifier, Clock); Bradley = new BradleyService(Obs, Ledger, Notifier, Clock);
        Container = new ContainerService(Db, Ledger, Notifier, Clock); Decay = new DecayRepository(Db, Clock); DecaySvc = new DecayService(Decay, Ledger, Notifier, Clock, Settings);
    }
    public Task<long> ServerAsync(string name = "A", string host = "1.1.1.1", int port = 1) => Servers.AddAsync(name, host, port);
    public async Task AddObs(long server, ObservedKind k, params double[] minutesFromT0) { foreach (var m in minutesFromT0) await Obs.AddAsync(server, k, T0.AddMinutes(m)); }
}

public class NotificationSettingsTests
{
    private sealed class Sink : IOverlaySink { public List<string> Shown = new(); public void Show(string id, string t) => Shown.Add(id + "|" + t); }
    private sealed class Sys : INotifier { public List<string> Sent = new(); public void Notify(string t, string m) => Sent.Add(t); }

    private static (NotificationRouter R, NotificationPrefs P, Sink O, Sys S, SettingsProvider St) Make()
    { var p = new NotificationPrefs(); var o = new Sink(); var s = new Sys(); var st = new SettingsProvider(); return (new NotificationRouter(p, o, s, st), p, o, s, st); }

    [Fact] public void AllEvents_OffByDefault_NothingShown()
    {
        var (r, _, o, s, _) = Make();
        foreach (var e in NotifyCatalog.All) Assert.False(r.Raise(e.Id, "k", "t", "x").Any);
        Assert.Empty(o.Shown); Assert.Empty(s.Sent);
    }

    [Fact] public void Toggles_AreIndependent_PerEventAndPerMethod()
    {
        var (r, p, o, s, _) = Make();
        p.Set(NotifyEventIds.ForecastChinook, new DeliveryChoice(true, false, false));
        p.Set(NotifyEventIds.Container, new DeliveryChoice(false, true, false));
        r.Raise(NotifyEventIds.ForecastChinook, "a", "Чинук", "x"); r.Raise(NotifyEventIds.Container, "b", "Контейнер", "y"); r.Raise(NotifyEventIds.Decay, "c", "d", "z");
        Assert.Single(o.Shown); Assert.Contains(NotifyEventIds.ForecastChinook, o.Shown[0]); Assert.StartsWith(NotifyEventIds.ForecastChinook, o.Shown[0]);
        Assert.Single(s.Sent); Assert.Equal("Контейнер", s.Sent[0]);
        p.Set(NotifyEventIds.ForecastChinook, DeliveryChoice.Off);      // выключили одно — второе работает
        r.Raise(NotifyEventIds.ForecastChinook, "a2", "Чинук", "x"); r.Raise(NotifyEventIds.Container, "b2", "Контейнер", "y");
        Assert.Single(o.Shown); Assert.Equal(2, s.Sent.Count);
    }

    [Fact] public void OverlayMasterSwitch_DisablesOnlyOverlay()
    {
        var (r, p, o, s, st) = Make();
        st.Update(new AppSettings { OverlayEnabled = false });
        p.Set(NotifyEventIds.Decay, new DeliveryChoice(true, true, false));
        r.Raise(NotifyEventIds.Decay, "k", "t", "x");
        Assert.Empty(o.Shown); Assert.Single(s.Sent);
    }

    [Fact] public void Catalog_CoversEveryNotificationEventId()
    {
        var ids = typeof(NotifyEventIds).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Equal(ids, NotifyCatalog.All.Select(e => e.Id).ToHashSet());
        Assert.All(new[] { TrackerKind.Monument, TrackerKind.DeepSea, TrackerKind.PatrolHelicopter, TrackerKind.Bradley }, k => Assert.Contains(NotifyEventIds.ForTimer(k), ids));
    }

    [Fact] public async Task Prefs_PersistAcrossRestart_AndDefaultsOffForNewEvents()
    {
        var e = new EventEnv(); var repo = new NotificationPrefsRepository(e.Db);
        await repo.SaveAsync(NotifyEventIds.ForecastHeli, new DeliveryChoice(true, false, false));
        var db2 = new Database(e.Path); db2.Initialize();
        var p2 = new NotificationPrefs(); p2.Load(await new NotificationPrefsRepository(db2).LoadAsync());
        Assert.True(p2.Get(NotifyEventIds.ForecastHeli).Overlay); Assert.False(p2.Get(NotifyEventIds.ForecastHeli).System);
        Assert.False(p2.Get(NotifyEventIds.ForecastChinook).Any);
    }

    [Fact] public async Task DisabledNotification_DoesNotStopTimer_OrHistory()
    {
        var (r, _, o, s, _) = Make();
        var repo = new MemoryTimerRepo(); var clock = new FakeClock(); var svc = new TimerService(repo, clock, r);
        var t = await svc.CreateAsync(TrackerKind.Monument, "A", 1200, 300);
        await svc.StartAsync(t); clock.Advance(TimeSpan.FromMinutes(21));
        await svc.TickAsync(new[] { t });
        Assert.Empty(o.Shown); Assert.Empty(s.Sent);                                          // показа нет…
        Assert.Equal(TimerPhase.Due, TimerCalculator.Phase(t, clock.UtcNow));               // …но таймер идёт
        Assert.Contains(repo.History, h => h.Action == "start");
    }

    [Fact] public void Settings_OverlayAndDecayValidation()
    {
        Assert.Empty(new AppSettings().Validate());
        Assert.NotEmpty(new AppSettings { OverlayDurationSeconds = 0 }.Validate());
        Assert.NotEmpty(new AppSettings { OverlayCorner = 9 }.Validate());
        Assert.NotEmpty(new AppSettings { DecayScale = 0 }.Validate());
        Assert.NotEmpty(new AppSettings { DecayHoursWood = -1 }.Validate());
    }

    [Fact] public async Task OverlaySettings_PersistAcrossRestart()
    {
        var e = new EventEnv(); await new SettingsRepository(e.Db).SaveAsync(new AppSettings { OverlayCorner = 3, OverlayDurationSeconds = 15 });
        var db2 = new Database(e.Path); db2.Initialize();
        var s = await new SettingsRepository(db2).LoadAsync();
        Assert.Equal((3, 15), (s.OverlayCorner, s.OverlayDurationSeconds));
    }
}

public class OverlayCoreTests
{
    private static readonly DateTimeOffset N = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void WindowStyles_NoActivate_ClickThrough_ToolWindow()
    {
        var f = OverlayWindowStyles.Apply(0);
        Assert.True((f & OverlayWindowStyles.WS_EX_NOACTIVATE) != 0); Assert.True((f & OverlayWindowStyles.WS_EX_TRANSPARENT) != 0);
        Assert.True((f & OverlayWindowStyles.WS_EX_TOOLWINDOW) != 0);
        Assert.Equal(0x1000 | OverlayWindowStyles.Apply(0), OverlayWindowStyles.Apply(0x1000)); // чужие флаги сохраняются
    }

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 24, 24)] [InlineData(OverlayCorner.TopRight, 1456, 24)]
    [InlineData(OverlayCorner.BottomLeft, 24, 996)] [InlineData(OverlayCorner.BottomRight, 1456, 996)]
    public void Placement_Corners(OverlayCorner c, double x, double y)
    {
        var (px, py) = OverlayPlacement.Compute(c, 0, 0, 1920, 1080, 440, 60, 24);
        Assert.Equal((x, y), (px, py));
    }

    [Fact] public void Queue_NoDuplicates_Expires_AndLimitsVisible()
    {
        var q = new OverlayQueue(2);
        q.Add("a", N, TimeSpan.FromSeconds(8)); q.Add("a", N.AddSeconds(1), TimeSpan.FromSeconds(8));   // повтор — не дубликат
        Assert.Single(q.Visible(N.AddSeconds(2)));
        q.Add("b", N.AddSeconds(2), TimeSpan.FromSeconds(8)); q.Add("c", N.AddSeconds(2), TimeSpan.FromSeconds(8));
        Assert.Equal(new[] { "b", "c" }, q.Visible(N.AddSeconds(3)));
        Assert.Empty(q.Visible(N.AddSeconds(30)));
    }
}

public class ForecastEngineTests
{
    private static ObservedEvent E(int i, double min) => new(i, 1, ObservedKind.Chinook, EventEnv.T0.AddMinutes(min), "");
    private static List<ObservedEvent> Series(params double[] minutes) => minutes.Select((m, i) => E(i + 1, m)).ToList();
    private static readonly DateTimeOffset Now = EventEnv.T0.AddMinutes(1);

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void TooFewObservations_NoForecast(int n)
    {
        var r = ForecastEngine.Compute(Series(Enumerable.Range(0, n).Select(i => i * 60.0).ToArray()), Now, ForecastOptions.Default);
        Assert.Equal(ForecastState.Insufficient, r.State); Assert.Null(r.Next); Assert.Contains(n == 0 ? "нет" : "Недостаточно", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void RegularSeries_ForecastsMedianInterval_WithoutHardcodedMargin()
    {
        var r = ForecastEngine.Compute(Series(0, 60, 120, 180, 240, 300, 360), Now, ForecastOptions.Default);
        Assert.Equal(ForecastState.Ready, r.State); Assert.Equal(ForecastConfidence.High, r.Confidence);
        Assert.Equal(EventEnv.T0.AddMinutes(420), r.Next); Assert.Equal(r.Next, r.From); Assert.Equal(r.Next, r.To); // погрешность не выдумана
        Assert.Equal(7, r.Observations);
    }

    [Fact] public void OneOutlier_DoesNotShiftMedian()
    {
        var r = ForecastEngine.Compute(Series(0, 60, 120, 180, 480), Now, ForecastOptions.Default);   // интервалы 60,60,60,300
        Assert.Equal(TimeSpan.FromMinutes(60), r.MedianInterval);
    }

    [Fact] public void LargeSpread_LowersConfidence_AndWidensRange()
    {
        var r = ForecastEngine.Compute(Series(0, 30, 120, 165, 285, 345), Now, ForecastOptions.Default);  // 30,90,45,120,60
        Assert.Equal(ForecastConfidence.Low, r.Confidence);
        Assert.Equal(EventEnv.T0.AddMinutes(345 + 30), r.From); Assert.Equal(EventEnv.T0.AddMinutes(345 + 120), r.To);
    }

    [Fact] public void AccidentalDoubleClick_IsIgnored()
    {
        var r = ForecastEngine.Compute(Series(0, 60, 61, 120, 180), Now, ForecastOptions.Default);   // 61 — дубликат в пределах 2 минут
        Assert.Equal(4, r.Observations);
    }

    [Fact] public void ExpiredWindow_IsOverdue_NotFakeCountdown()
    {
        var r = ForecastEngine.Compute(Series(0, 60, 120, 180), EventEnv.T0.AddMinutes(500), ForecastOptions.Default);
        Assert.Equal(ForecastState.Overdue, r.State);
    }

    [Fact] public void Airdrop_Irregular_NoRegularForecast()
    {
        var r = ForecastEngine.Compute(Series(0, 20, 150, 170, 400, 460), Now, ForecastOptions.Airdrop);
        Assert.Equal(ForecastState.Unavailable, r.State); Assert.Null(r.Next); Assert.Contains("недоступен", r.Reason);
    }

    [Fact] public void Airdrop_NeedsFiveObservations_AndRegularity()
    {
        Assert.Equal(ForecastState.Insufficient, ForecastEngine.Compute(Series(0, 60, 120, 180), Now, ForecastOptions.Airdrop).State);
        Assert.Equal(ForecastState.Ready, ForecastEngine.Compute(Series(0, 60, 120, 180, 240), Now, ForecastOptions.Airdrop).State);
    }
}

public class ForecastServiceTests
{
    [Fact] public async Task WarnsOnce_TenMinutesBefore_NotBefore_AndRearmsAfterNewObservation()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.AddObs(s, ObservedKind.Chinook, 0, 60, 120, 180, 240);
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(285);                                 // за 15 минут до 300 — рано
        Assert.Equal(0, await e.Forecast.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(292);                                 // внутри окна «за 10 минут»
        Assert.Equal(1, await e.Forecast.CheckWarningsAsync()); Assert.Contains("ориентировочно", e.Notifier.Sent[0].Message);
        Assert.Equal(0, await e.Forecast.CheckWarningsAsync());                       // повтор для того же прогноза не создаётся
        await e.Obs.AddAsync(s, ObservedKind.Chinook, EventEnv.T0.AddMinutes(302));   // новое наблюдение → прогноз сдвинулся
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(302 + 55);
        Assert.Equal(1, await e.Forecast.CheckWarningsAsync());
        Assert.Equal(2, e.Notifier.Sent.Count); Assert.All(e.Notifier.EventIds, id => Assert.Equal(NotifyEventIds.ForecastChinook, id));
    }

    [Fact] public async Task DisabledNotification_DoesNotConsumeWarning_ButForecastContinues()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.AddObs(s, ObservedKind.PatrolHelicopter, 0, 60, 120, 180);
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(235);
        e.Notifier.Choice = DeliveryChoice.Off;
        Assert.Equal(0, await e.Forecast.CheckWarningsAsync());
        Assert.Equal(ForecastState.Ready, (await e.Forecast.ComputeAsync(s, ObservedKind.PatrolHelicopter)).State);   // расчёт продолжается
        e.Notifier.Choice = new DeliveryChoice(true, false, false);
        Assert.Equal(1, await e.Forecast.CheckWarningsAsync());                        // включили в окне — получили
    }

    [Fact] public async Task ServersAndKinds_AreSeparate()
    {
        var e = new EventEnv(); var a = await e.ServerAsync("A", "1.1.1.1", 1); var b = await e.ServerAsync("B", "2.2.2.2", 2);
        await e.AddObs(a, ObservedKind.Chinook, 0, 60, 120, 180);
        await e.AddObs(b, ObservedKind.Chinook, 0);
        await e.AddObs(a, ObservedKind.PatrolHelicopter, 10);
        Assert.Equal(ForecastState.Ready, (await e.Forecast.ComputeAsync(a, ObservedKind.Chinook)).State);
        Assert.Equal(ForecastState.Insufficient, (await e.Forecast.ComputeAsync(b, ObservedKind.Chinook)).State);
        Assert.Equal(1, (await e.Forecast.ComputeAsync(a, ObservedKind.PatrolHelicopter)).Observations);
        Assert.Equal(1, (await e.Forecast.ComputeAsync(b, ObservedKind.Chinook)).Observations);
    }

    [Fact] public async Task History_PersistsAfterRestart_EditAndDelete()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        var id = await e.Obs.AddAsync(s, ObservedKind.Airdrop, EventEnv.T0);
        var e2 = new EventEnv(e.Path);
        Assert.Single(await e2.Obs.ListAsync(s, ObservedKind.Airdrop));
        await e2.Obs.UpdateAsync(id, EventEnv.T0.AddMinutes(5), "поправил");
        Assert.Equal(EventEnv.T0.AddMinutes(5), (await e2.Obs.ListAsync(s, ObservedKind.Airdrop))[0].AtUtc);
        await e2.Obs.DeleteAsync(id); Assert.Empty(await e2.Obs.ListAsync(s, ObservedKind.Airdrop));
    }

    [Fact] public async Task DeletedServer_ObservationsRemoved_AndAddRejected()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.Obs.AddAsync(s, ObservedKind.Chinook, EventEnv.T0);
        await e.Servers.DeleteAsync(s);
        Assert.Empty(await e.Obs.ListAsync(s, ObservedKind.Chinook));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Obs.AddAsync(s, ObservedKind.Chinook, EventEnv.T0));
    }

    [Fact] public async Task Airdrop_WithIrregularHistory_NeverWarns()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.AddObs(s, ObservedKind.Airdrop, 0, 20, 150, 170, 400);
        for (var m = 400; m < 700; m += 5) { e.Clock.UtcNow = EventEnv.T0.AddMinutes(m); Assert.Equal(0, await e.Forecast.CheckWarningsAsync()); }
    }
}

public class BradleyTests
{
    [Fact] public async Task CountdownStartsOnlyAfterDestroyed_AndNeedsUserInterval()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        Assert.Equal(BradleyPhase.NoCycle, (await e.Bradley.GetStatusAsync(s)).Phase);
        await e.Bradley.SetIntervalAsync(s, 60, null);
        Assert.Equal(BradleyPhase.NoCycle, (await e.Bradley.GetStatusAsync(s)).Phase);   // интервал без уничтожения отсчёт не запускает
        await e.Bradley.MarkDestroyedAsync(s);
        var st = await e.Bradley.GetStatusAsync(s);
        Assert.Equal(BradleyPhase.Counting, st.Phase); Assert.Equal(EventEnv.T0, st.DestroyedAtUtc); Assert.Equal(EventEnv.T0.AddMinutes(60), st.EarliestUtc);
    }

    [Fact] public async Task NoIntervalSet_NoInventedRespawnTime()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.Bradley.MarkDestroyedAsync(s);
        var st = await e.Bradley.GetStatusAsync(s);
        Assert.Equal(BradleyPhase.IntervalNotSet, st.Phase); Assert.Null(st.EarliestUtc);
    }

    [Fact] public async Task WarnsOncePerCycle_AndNewDestroyedStartsNewCycle()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.Bradley.SetIntervalAsync(s, 60, null); await e.Bradley.MarkDestroyedAsync(s);
        e.Clock.Advance(TimeSpan.FromMinutes(40)); Assert.Equal(0, await e.Bradley.CheckWarningsAsync());
        e.Clock.Advance(TimeSpan.FromMinutes(11)); Assert.Equal(1, await e.Bradley.CheckWarningsAsync());
        Assert.Equal(0, await e.Bradley.CheckWarningsAsync());
        e.Clock.Advance(TimeSpan.FromMinutes(10));                                           // 61 минута: расчётный интервал завершился
        var ended = await e.Bradley.GetStatusAsync(s);
        Assert.Equal(BradleyPhase.WindowEnded, ended.Phase); Assert.Contains("не подтверждает", ended.Message);
        Assert.Equal(0, await e.Bradley.CheckWarningsAsync());                               // старое предупреждение не повторяется
        await e.Bradley.MarkDestroyedAsync(s);                                               // новый цикл
        Assert.Equal(BradleyPhase.Counting, (await e.Bradley.GetStatusAsync(s)).Phase);
        e.Clock.Advance(TimeSpan.FromMinutes(51)); Assert.Equal(1, await e.Bradley.CheckWarningsAsync());
        Assert.Equal(2, e.Notifier.Sent.Count); Assert.All(e.Notifier.Sent, m => Assert.Contains("оценка", m.Message));
    }

    [Fact] public async Task IntervalRange_AndPerServerSeparation_AndValidation()
    {
        var e = new EventEnv(); var a = await e.ServerAsync("A", "1.1.1.1", 1); var b = await e.ServerAsync("B", "2.2.2.2", 2);
        await e.Bradley.SetIntervalAsync(a, 30, 45); await e.Bradley.MarkDestroyedAsync(a); await e.Bradley.MarkDestroyedAsync(b);
        var sa = await e.Bradley.GetStatusAsync(a); var sb = await e.Bradley.GetStatusAsync(b);
        Assert.Equal(EventEnv.T0.AddMinutes(45), sa.LatestUtc); Assert.Equal(BradleyPhase.IntervalNotSet, sb.Phase);
        await Assert.ThrowsAsync<ArgumentException>(() => e.Bradley.SetIntervalAsync(a, 0, null));
        await Assert.ThrowsAsync<ArgumentException>(() => e.Bradley.SetIntervalAsync(a, 60, 30));
    }

    [Fact] public async Task Countdown_SurvivesRestart_FromSavedTimestamps()
    {
        var e = new EventEnv(); var s = await e.ServerAsync();
        await e.Bradley.SetIntervalAsync(s, 60, null); await e.Bradley.MarkDestroyedAsync(s);
        var e2 = new EventEnv(e.Path); e2.Clock.UtcNow = EventEnv.T0.AddMinutes(20);
        Assert.Equal(EventEnv.T0.AddMinutes(60), (await e2.Bradley.GetStatusAsync(s)).EarliestUtc);   // не перезапущен
    }

    [Fact] public void NoAppearedAction_InService() =>
        Assert.DoesNotContain(typeof(BradleyService).GetMethods().Select(m => m.Name), n => n.Contains("Appear", StringComparison.OrdinalIgnoreCase) || n.Contains("Spawned", StringComparison.OrdinalIgnoreCase));
}

public class ContainerTests
{
    [Fact] public async Task FifteenMinutes_WarningAtFourteenth_OncePerTimer()
    {
        var e = new EventEnv(); var id = await e.Container.ActivateAsync("Ящик", null);
        var t = (await e.Container.ListAsync())[0];
        Assert.Equal(TimeSpan.FromMinutes(15), t.Remaining(EventEnv.T0));
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(13).AddSeconds(59); Assert.Equal(0, await e.Container.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(14); Assert.Equal(1, await e.Container.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(14).AddSeconds(30); Assert.Equal(0, await e.Container.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(16); Assert.Equal(0, await e.Container.CheckWarningsAsync());
        Assert.Single(e.Notifier.Sent); Assert.Equal(NotifyEventIds.Container, e.Notifier.EventIds[0]);
    }

    [Fact] public async Task TwoTimers_Independent_AndLateStartNoStaleWarning_AndPersist()
    {
        var e = new EventEnv(); await e.Container.ActivateAsync("A", null);
        e.Clock.Advance(TimeSpan.FromMinutes(5)); await e.Container.ActivateAsync("B", null);
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(14).AddSeconds(5); Assert.Equal(1, await e.Container.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(19).AddSeconds(5); Assert.Equal(1, await e.Container.CheckWarningsAsync());
        var e2 = new EventEnv(e.Path); e2.Clock.UtcNow = EventEnv.T0.AddMinutes(60);                 // приложение запущено позже — старые предупреждения не показываем
        Assert.Equal(0, await e2.Container.CheckWarningsAsync()); Assert.Equal(2, (await e2.Container.ListAsync()).Count);
    }

    [Fact] public async Task DisabledNotification_TimerStillCounts()
    {
        var e = new EventEnv(); await e.Container.ActivateAsync("A", null); e.Notifier.Choice = DeliveryChoice.Off;
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(14).AddSeconds(10);
        Assert.Equal(0, await e.Container.CheckWarningsAsync());
        Assert.True((await e.Container.ListAsync())[0].Remaining(e.Clock.UtcNow) > TimeSpan.Zero);
    }
}

public class DecayTests
{
    private static DecayObject Obj(string label = "Стена у гаража", DecayMaterial m = DecayMaterial.Wood, double? hp = 250, double? max = 250, bool notify = false)
        => new() { Label = label, Element = "Стена", Material = m, Grid = "H12", NoticedAtUtc = EventEnv.T0, Hp = hp, MaxHp = max, Notify = notify };

    [Fact] public void Linear_ByHpFraction_AndEditableHours()
    {
        var s = new AppSettings();
        Assert.Equal(EventEnv.T0.AddHours(3), DecayCalculator.Calculate(Obj(), s, EventEnv.T0).EstimateUtc);
        Assert.Equal(EventEnv.T0.AddHours(1.5), DecayCalculator.Calculate(Obj(hp: 125), s, EventEnv.T0).EstimateUtc);
        Assert.Equal(EventEnv.T0.AddHours(2.5), DecayCalculator.Calculate(Obj(m: DecayMaterial.Stone, hp: 250, max: 500), s, EventEnv.T0).EstimateUtc);
        s.DecayHoursStone = 6; s.DecayScale = 2;
        Assert.Equal(EventEnv.T0.AddHours(3), DecayCalculator.Calculate(Obj(m: DecayMaterial.Stone, hp: 500, max: 500), s, EventEnv.T0).EstimateUtc); // 6 ч / ×2
    }

    [Fact] public void UnknownHp_NoPointEstimate_OnlyUpperBound_WithAssumptions()
    {
        var r = DecayCalculator.Calculate(Obj(hp: null, max: null), new AppSettings(), EventEnv.T0);
        Assert.Equal(DecayState.Insufficient, r.State); Assert.Null(r.EstimateUtc); Assert.Equal(EventEnv.T0.AddHours(3), r.UpperBoundUtc);
        Assert.Contains("HP", r.Message); Assert.NotEmpty(r.Assumptions);
        Assert.Equal(DecayState.Insufficient, DecayCalculator.Calculate(Obj(hp: 100, max: null), new AppSettings(), EventEnv.T0).State);
    }

    [Fact] public void InvalidHp_AndPassedTime()
    {
        Assert.Equal(DecayState.Invalid, DecayCalculator.Calculate(Obj(hp: 300, max: 250), new AppSettings(), EventEnv.T0).State);
        Assert.Equal(DecayState.TimePassed, DecayCalculator.Calculate(Obj(), new AppSettings(), EventEnv.T0.AddHours(4)).State);
        Assert.Equal(DecayState.Destroyed, DecayCalculator.Calculate(new DecayObject { Destroyed = true, NoticedAtUtc = EventEnv.T0 }, new AppSettings(), EventEnv.T0).State);
    }

    [Fact] public async Task LimitOfFive_Crud_DestroyedFreesSlot()
    {
        var e = new EventEnv(); var ids = new List<long>();
        for (var i = 1; i <= 5; i++) ids.Add(await e.Decay.AddAsync(Obj("Объект " + i)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Decay.AddAsync(Obj("Шестой")));
        await e.Decay.MarkDestroyedAsync(ids[0]);
        Assert.Equal(4, (await e.Decay.ListAsync(false)).Count); Assert.Equal(5, (await e.Decay.ListAsync(true)).Count);
        var sixth = await e.Decay.AddAsync(Obj("Шестой"));
        var o = (await e.Decay.ListAsync(false)).First(x => x.Id == sixth); o.Label = "Переименован"; o.Hp = 100;
        await e.Decay.UpdateAsync(o);
        Assert.Equal("Переименован", (await e.Decay.ListAsync(false)).First(x => x.Id == sixth).Label);
        await e.Decay.DeleteAsync(sixth); Assert.Equal(4, (await e.Decay.ListAsync(false)).Count);
    }

    [Fact] public async Task Validation_RejectsBadInput()
    {
        var e = new EventEnv();
        await Assert.ThrowsAsync<ArgumentException>(() => e.Decay.AddAsync(Obj("")));
        var bad = Obj(); bad.Grid = "12H"; await Assert.ThrowsAsync<ArgumentException>(() => e.Decay.AddAsync(bad));
        var fut = Obj(); fut.NoticedAtUtc = EventEnv.T0.AddHours(2); await Assert.ThrowsAsync<ArgumentException>(() => e.Decay.AddAsync(fut));
        await Assert.ThrowsAsync<ArgumentException>(() => e.Decay.AddAsync(Obj(hp: 300, max: 250)));
    }

    [Fact] public async Task Notification_PerRecordSwitch_FiveMinutesBefore_OnceAndNotAfterDestruction()
    {
        var e = new EventEnv();
        await e.Decay.AddAsync(Obj("Выключенная"));                                  // переключатель по умолчанию выключен
        var on = await e.Decay.AddAsync(Obj("Со включённым"));
        await e.Decay.SetNotifyAsync(on, true);
        e.Clock.UtcNow = EventEnv.T0.AddHours(3).AddMinutes(-6); Assert.Equal(0, await e.DecaySvc.CheckWarningsAsync());
        e.Clock.UtcNow = EventEnv.T0.AddHours(3).AddMinutes(-4);
        Assert.Equal(1, await e.DecaySvc.CheckWarningsAsync());                      // только запись с включённым переключателем
        Assert.Contains("Со включённым", e.Notifier.Sent[0].Title); Assert.Contains("H12", e.Notifier.Sent[0].Title);
        Assert.Equal(0, await e.DecaySvc.CheckWarningsAsync());                      // один раз
        e.Clock.UtcNow = EventEnv.T0.AddHours(4); Assert.Equal(0, await e.DecaySvc.CheckWarningsAsync()); // время прошло — старое не повторяем
    }

    [Fact] public async Task EditedRecord_GetsNewWarning_AndDisabledNotificationNotConsumed()
    {
        var e = new EventEnv(); var id = await e.Decay.AddAsync(Obj("A", notify: false)); await e.Decay.SetNotifyAsync(id, true);
        e.Notifier.Choice = DeliveryChoice.Off; e.Clock.UtcNow = EventEnv.T0.AddHours(3).AddMinutes(-4);
        Assert.Equal(0, await e.DecaySvc.CheckWarningsAsync());
        e.Notifier.Choice = new DeliveryChoice(true, false, false);
        Assert.Equal(1, await e.DecaySvc.CheckWarningsAsync());
        var o = (await e.Decay.ListAsync(false))[0]; o.Hp = 250; o.NoticedAtUtc = EventEnv.T0.AddMinutes(1); await e.Decay.UpdateAsync(o);   // правка → новая версия
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(1).AddHours(3).AddMinutes(-4);
        Assert.Equal(1, await e.DecaySvc.CheckWarningsAsync());
    }

    [Fact] public async Task List_PersistsAfterRestart_AndOldUpkeepUiIsGone()
    {
        var e = new EventEnv(); await e.Decay.AddAsync(Obj("Сохранится"));
        Assert.Single(await new EventEnv(e.Path).Decay.ListAsync(false));
        Assert.DoesNotContain(typeof(DecayCalculator).Assembly.GetTypes(), t => t.Name.Contains("Upkeep", StringComparison.OrdinalIgnoreCase));
    }
}
