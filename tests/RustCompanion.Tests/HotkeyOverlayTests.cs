using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Hotkeys;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Overlay;
using Xunit;

namespace RustCompanion.Tests;

public class HotkeyGestureTests
{
    private static HotkeyGesture G(HotkeyMods m, int vk) => new(m, vk);

    [Fact] public void ToString_ShowsReadableCombo() =>
        Assert.Equal("Ctrl+Alt+F8", G(HotkeyMods.Control | HotkeyMods.Alt, 0x77).ToString());

    [Theory] [InlineData(HotkeyMods.Control, 0x48)] [InlineData(HotkeyMods.Alt | HotkeyMods.Shift, 0x31)] [InlineData(HotkeyMods.None, 0x77)] [InlineData(HotkeyMods.Win, 0x4D)]
    public void Valid_Gestures(HotkeyMods m, int vk) => Assert.Null(G(m, vk).Validate());

    [Theory] [InlineData(HotkeyMods.None, 0x48)] [InlineData(HotkeyMods.Shift, 0x48)] [InlineData(HotkeyMods.Control, 0x11)] [InlineData(HotkeyMods.None, 0x10)]
    [InlineData(HotkeyMods.Alt, 0x73)] [InlineData(HotkeyMods.Alt, 0x09)] [InlineData(HotkeyMods.Control, 0x1B)] [InlineData(HotkeyMods.Control, 0x00)]
    public void Invalid_Gestures_AreRejected_WithReason(HotkeyMods m, int vk) => Assert.NotNull(G(m, vk).Validate());

    [Fact] public void F1_IsAllowedButWarns_AboutRustConsole() { Assert.Null(G(HotkeyMods.None, 0x70).Validate()); Assert.Contains("консоль", G(HotkeyMods.None, 0x70).Warning()); }

    [Fact] public void Conflict_BetweenEvents_IsExplained()
    {
        var current = new Dictionary<HotkeyEvent, HotkeyGesture> { [HotkeyEvent.Chinook] = G(HotkeyMods.Control, 0x48) };
        var err = HotkeyAssignments.Validate(current, HotkeyEvent.Bradley, G(HotkeyMods.Control, 0x48));
        Assert.NotNull(err); Assert.Contains("Чинук", err);
        Assert.Null(HotkeyAssignments.Validate(current, HotkeyEvent.Chinook, G(HotkeyMods.Control, 0x48)));      // то же событие — не конфликт
        Assert.Null(HotkeyAssignments.Validate(current, HotkeyEvent.Bradley, G(HotkeyMods.Control, 0x42)));
        Assert.NotNull(HotkeyAssignments.Validate(current, HotkeyEvent.Bradley, G(HotkeyMods.None, 0x42)));      // недопустимая комбинация
    }

    [Fact] public async Task Assignments_PersistAfterRestart_AndCanBeCleared()
    {
        var e = new EventEnv(); var repo = new HotkeyRepository(e.Db);
        await repo.SaveAsync(HotkeyEvent.Chinook, G(HotkeyMods.Control | HotkeyMods.Alt, 0x43)); await repo.SaveAsync(HotkeyEvent.Airdrop, G(HotkeyMods.None, 0x78));
        var loaded = await new HotkeyRepository(new EventEnv(e.Path).Db).LoadAsync();
        Assert.Equal(2, loaded.Count); Assert.Equal("Ctrl+Alt+C", loaded[HotkeyEvent.Chinook].ToString());
        await repo.SaveAsync(HotkeyEvent.Airdrop, null); Assert.Single(await repo.LoadAsync());
    }
}

public class HotkeyServiceTests
{
    private sealed class Spy : IOverlaySink { public List<string> Shown = new(); public void Show(string id, string t) => Shown.Add(t); }

    private static async Task<(EventEnv E, HotkeyService S, Spy Spy, long Server)> Make(bool serverSelected = true, bool confirm = true)
    {
        var e = new EventEnv(); var server = await e.ServerAsync(); var spy = new Spy();
        e.Settings.Update(new AppSettings { HotkeyServerId = serverSelected ? server : 0, HotkeyConfirmOverlay = confirm });
        return (e, new HotkeyService(e.Obs, e.Servers, e.Clock, e.Settings, spy), spy, server);
    }

    [Fact] public async Task OnePress_OneMark_WithConfirmation()
    {
        var (e, s, spy, srv) = await Make();
        var r = await s.PressAsync(HotkeyEvent.PatrolHelicopter);
        Assert.True(r.Recorded); Assert.Equal(EventEnv.T0, r.AtUtc);
        Assert.Single(await e.Obs.ListAsync(srv, ObservedKind.PatrolHelicopter));
        Assert.Single(spy.Shown); Assert.Contains("Патрульный вертолёт", spy.Shown[0]); Assert.Contains("нужна ещё одна отметка", spy.Shown[0]);   // интервал ещё неизвестен — не выдумывается
    }

    [Fact] public async Task HoldingKey_CreatesNoSeriesOfMarks()
    {
        var (e, s, _, srv) = await Make();
        for (var i = 0; i < 50; i++) { await s.PressAsync(HotkeyEvent.Chinook); e.Clock.Advance(TimeSpan.FromMilliseconds(15)); }   // ~0,75 с удержания
        Assert.Single(await e.Obs.ListAsync(srv, ObservedKind.Chinook));
    }

    [Fact] public async Task SecondMark_MeasuresInterval_AndForecastRecomputesWithMoreMarks()
    {
        var (e, s, spy, srv) = await Make();
        await s.PressAsync(HotkeyEvent.Bradley);
        e.Clock.Advance(TimeSpan.FromMinutes(60));
        var second = await s.PressAsync(HotkeyEvent.Bradley);
        Assert.Equal(TimeSpan.FromMinutes(60), second.Interval); Assert.Equal(ForecastState.Ready, second.Forecast!.State);
        Assert.True(second.Forecast.LowSample); Assert.Equal(EventEnv.T0.AddMinutes(120), second.Forecast.Next);                      // по одному интервалу — предварительная оценка
        Assert.Contains("по одному интервалу", spy.Shown[^1]);
        e.Clock.Advance(TimeSpan.FromMinutes(70)); await s.PressAsync(HotkeyEvent.Bradley);                                        // интервалы 60, 70
        e.Clock.Advance(TimeSpan.FromMinutes(60)); var fourth = await s.PressAsync(HotkeyEvent.Bradley);                           // 60, 70, 60 → медиана 60
        Assert.False(fourth.Forecast!.LowSample); Assert.Equal(TimeSpan.FromMinutes(60), fourth.Forecast.MedianInterval); Assert.Equal(4, fourth.Forecast.Observations);
        Assert.Equal(4, (await e.Obs.ListAsync(srv, ObservedKind.BradleyAppeared)).Count);
    }

    [Fact] public async Task TooCloseMark_ClockChange_NoServer_DeletedServer_AreExplained_NotRecorded()
    {
        var (e, s, _, srv) = await Make();
        await s.PressAsync(HotkeyEvent.Airdrop);
        e.Clock.Advance(TimeSpan.FromSeconds(5));
        var close = await s.PressAsync(HotkeyEvent.Airdrop); Assert.False(close.Recorded); Assert.Contains("слишком близко", close.Message);
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(-30);                                                                              // системное время перевели назад
        var back = await s.PressAsync(HotkeyEvent.Airdrop); Assert.False(back.Recorded); Assert.Contains("системное время", back.Message);
        Assert.Single(await e.Obs.ListAsync(srv, ObservedKind.Airdrop));
        var (_, s2, _, _) = await Make(serverSelected: false);
        var none = await s2.PressAsync(HotkeyEvent.Chinook); Assert.False(none.Recorded); Assert.Contains("не выбран сервер", none.Message);
        await e.Servers.DeleteAsync(srv); e.Clock.UtcNow = EventEnv.T0.AddHours(1);
        var gone = await s.PressAsync(HotkeyEvent.Chinook); Assert.False(gone.Recorded); Assert.Contains("удалён", gone.Message);
    }

    [Fact] public async Task Events_AreIndependent_AndConfirmationCanBeDisabled()
    {
        var (e, s, spy, srv) = await Make(confirm: false);
        await s.PressAsync(HotkeyEvent.Chinook); await s.PressAsync(HotkeyEvent.CargoShip);
        Assert.Single(await e.Obs.ListAsync(srv, ObservedKind.Chinook)); Assert.Single(await e.Obs.ListAsync(srv, ObservedKind.CargoShip)); Assert.Empty(await e.Obs.ListAsync(srv, ObservedKind.PatrolHelicopter));
        Assert.Empty(spy.Shown);                                                                                                   // подтверждение в оверлее выключено
    }

    [Fact] public async Task HistoryPersists_MistakenMarkCanBeDeleted_ResetClearsEvent()
    {
        var (e, s, _, srv) = await Make();
        await s.PressAsync(HotkeyEvent.Chinook); e.Clock.Advance(TimeSpan.FromMinutes(50)); await s.PressAsync(HotkeyEvent.Chinook);
        e.Clock.Advance(TimeSpan.FromMinutes(1)); await s.PressAsync(HotkeyEvent.Chinook);   // ошибочная отметка через минуту (в прогноз не войдёт — меньше 2 минут не отсекается, но удаляется вручную)
        var e2 = new EventEnv(e.Path); Assert.Equal(3, (await e2.Obs.ListAsync(srv, ObservedKind.Chinook)).Count);                  // после «перезапуска»
        Assert.Equal(1, await s.DeleteLastMarkAsync(HotkeyEvent.Chinook)); Assert.Equal(2, (await e.Obs.ListAsync(srv, ObservedKind.Chinook)).Count);
        Assert.Equal(2, await s.ResetAsync(HotkeyEvent.Chinook)); Assert.Empty(await e.Obs.ListAsync(srv, ObservedKind.Chinook));
    }

    [Fact] public async Task ForecastWarning_UsesConfiguredLead_OncePerForecast_OffByDefaultNotification()
    {
        var (e, s, _, srv) = await Make();
        e.Settings.Update(new AppSettings { HotkeyServerId = srv, ForecastLeadMinutes = 20 });
        var svc = new ForecastService(e.Obs, e.Ledger, e.Notifier, e.Clock, e.Settings);
        await s.PressAsync(HotkeyEvent.PatrolHelicopter); e.Clock.Advance(TimeSpan.FromMinutes(100)); await s.PressAsync(HotkeyEvent.PatrolHelicopter);   // следующее: +200
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(100 + 70); Assert.Equal(0, await svc.CheckWarningsAsync());                        // за 30 мин — рано при 20
        e.Clock.UtcNow = EventEnv.T0.AddMinutes(100 + 85); Assert.Equal(1, await svc.CheckWarningsAsync()); Assert.Equal(0, await svc.CheckWarningsAsync());
        e.Notifier.Choice = DeliveryChoice.Off; await s.ResetAsync(HotkeyEvent.PatrolHelicopter);
        Assert.Equal(1, e.Notifier.Sent.Count);
    }
}

public class OverlayHudTests
{
    private static readonly RectD Virtual = new(0, 0, 3840, 1080);   // два монитора 1920 рядом
    private static readonly RectD Work = new(0, 0, 1920, 1040);

    [Fact] public void Layout_KeepsVisibleSavedPosition_OnSecondMonitor()
    {
        var r = OverlayLayout.Resolve(2300, 100, 320, 120, Virtual, Work, OverlayCorner.TopRight, 24);
        Assert.True(r.UsedSaved); Assert.Equal((2300, 100, 320), (r.X, r.Y, r.Width));
    }

    [Fact] public void Layout_FallsBackToCorner_WhenMonitorRemovedOrResolutionShrank()
    {
        var single = new RectD(0, 0, 1920, 1080);
        var r = OverlayLayout.Resolve(2300, 100, 320, 120, single, Work, OverlayCorner.TopRight, 24);
        Assert.False(r.UsedSaved); Assert.Equal((1920 - 320 - 24, 24), (r.X, r.Y));
        Assert.False(OverlayLayout.Resolve(-5000, 100, 320, 120, single, Work, OverlayCorner.TopLeft, 10).UsedSaved);
    }

    [Fact] public void Layout_NoSavedPosition_UsesCorner_AndClampsWidth()
    {
        var r = OverlayLayout.Resolve(null, null, 5000, 120, Virtual, Work, OverlayCorner.BottomLeft, 20);
        Assert.False(r.UsedSaved); Assert.Equal(OverlayLayout.MaxWidth, r.Width); Assert.Equal((20, 1040 - 120 - 20), (r.X, r.Y));
        Assert.Equal(OverlayLayout.MinWidth, OverlayLayout.ClampWidth(10)); Assert.Equal(320, OverlayLayout.ClampWidth(double.NaN));
    }

    [Fact] public void Hud_SortsByTime_MarksEstimates_AndNeverInventsMissingForecast()
    {
        var now = EventEnv.T0;
        var lines = OverlayHud.Build(new[]
        {
            new HudEntry("a", "Чинук", now.AddMinutes(30)), new HudEntry("b", "Bradley", null), new HudEntry("c", "Вертолёт", now.AddMinutes(5)), new HudEntry("d", "Аэрдроп", now.AddMinutes(-1)),
        }, now);
        Assert.StartsWith("Аэрдроп", lines[0]); Assert.Contains("время наступило", lines[0]);
        Assert.StartsWith("Вертолёт", lines[1]); Assert.Contains("~", lines[1]); Assert.Contains("00:05:00", lines[1]);
        Assert.StartsWith("Чинук", lines[2]); Assert.Contains("нет прогноза", lines[3]);
        Assert.Equal(2, OverlayHud.Build(new[] { new HudEntry("a", "A", null), new HudEntry("b", "B", null), new HudEntry("c", "C", null) }, now, 2).Count);
    }

    [Fact] public void HudSelection_RoundTrips_IgnoresUnknownIds()
    {
        var sel = OverlayHud.ParseSelection("chinook, heli ,bogus,monuments");
        Assert.Equal(3, sel.Count); Assert.DoesNotContain("bogus", sel);
        Assert.Equal("chinook,heli,monuments", OverlayHud.Serialize(sel));
        Assert.Empty(OverlayHud.ParseSelection(null));
    }

    [Theory] [InlineData(3, FullscreenState.ExclusiveD3D)] [InlineData(2, FullscreenState.FullscreenWindowOrApp)] [InlineData(5, FullscreenState.NotFullscreen)] [InlineData(99, FullscreenState.Unknown)]
    public void Diagnostics_RecognizeExclusiveFullscreen_AndAdvise(int quns, FullscreenState expected)
    {
        var (state, advice) = OverlayDiagnostics.FromNotificationState(quns);
        Assert.Equal(expected, state); Assert.False(string.IsNullOrWhiteSpace(advice));
        if (state == FullscreenState.ExclusiveD3D) Assert.Contains("Borderless", advice);
    }

    [Fact] public void Settings_NewFields_ValidateAndDefaultSafely()
    {
        var d = new AppSettings();
        Assert.False(d.OverlayHudVisible); Assert.True(d.OverlayLocked); Assert.Null(d.OverlayLeft); Assert.Equal(10, d.ForecastLeadMinutes); Assert.Empty(d.Validate());
        Assert.NotEmpty(new AppSettings { OverlayWidth = 50 }.Validate()); Assert.NotEmpty(new AppSettings { ForecastLeadMinutes = 0 }.Validate()); Assert.NotEmpty(new AppSettings { ServerPanelRefreshSeconds = 5 }.Validate());
    }

    [Fact] public async Task OverlayPosition_PersistsAfterRestart_IncludingNullAndNaNFreeJson()
    {
        var e = new EventEnv();
        await new SettingsRepository(e.Db).SaveAsync(new AppSettings { OverlayLeft = 2300.5, OverlayTop = 80, OverlayWidth = 410, OverlayLocked = false, OverlayHudVisible = true, OverlayHudItems = "chinook,heli" });
        var s = await new SettingsRepository(new EventEnv(e.Path).Db).LoadAsync();
        Assert.Equal((2300.5, 80.0, 410.0, false, true, "chinook,heli"), (s.OverlayLeft, s.OverlayTop, s.OverlayWidth, s.OverlayLocked, s.OverlayHudVisible, s.OverlayHudItems));
        var fresh = await new SettingsRepository(new EventEnv().Db).LoadAsync(); Assert.Null(fresh.OverlayLeft);
    }
}
