using RustCompanion.Core;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.RustPlus;
using Xunit;

namespace RustCompanion.Tests;

public class RustPlusFixesTests
{
    private static async Task<(RpEnv E, FakeGateway G, RustPlusHub Hub, RustPlusDeviceController C, RpDevice Dev)> ConnectedSwitchAsync()
    {
        var e = new RpEnv(); await e.PairAsync("Srv");
        await e.Devices.AddAsync(e.Server.Id, 500, RpDeviceKind.SmartSwitch, "Свет");
        var dev = (await e.Devices.ListAsync(e.Server.Id)).First();
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        return (e, e.Created[0], hub, new RustPlusDeviceController(e.Manager, hub, e.Devices, e.Clock), dev);
    }

    [Fact] public async Task Switch_ConfirmedByServer_UpdatesSharedState()
    {
        var (e, g, hub, c, dev) = await ConnectedSwitchAsync();
        g.SwitchState = false;
        var r = await c.SetSwitchAsync(dev, true, "test");
        Assert.True(r.Ok); Assert.True(g.SwitchState);
        Assert.True(hub.GetLive(e.Server.Id, 500)!.State!.IsOn);
        Assert.Equal(DeviceAvailability.Available, hub.GetLive(e.Server.Id, 500)!.Availability);
    }

    [Fact] public async Task Switch_NotConfirmed_DoesNotFakeSuccess()
    {
        var (e, g, hub, c, dev) = await ConnectedSwitchAsync();
        g.SwitchState = false; g.ApplyOnSet = false;                      // сервер принял команду, но состояние не изменилось
        var r = await c.SetSwitchAsync(dev, true, "test");
        Assert.False(r.Ok);
        Assert.NotEqual(true, hub.GetLive(e.Server.Id, 500)?.State?.IsOn);
    }

    [Fact] public async Task Switch_DeviceOfAnotherServer_IsNotSent()
    {
        var (e, g, hub, c, dev) = await ConnectedSwitchAsync();
        var other = await e.Servers.UpsertAsync("Other", "9.9.9.9", 28082, 76561198000000001UL, 1, e.Clock.UtcNow);
        var foreign = new RpDevice { Id = 99, ServerId = other.Id, EntityId = 600, Kind = RpDeviceKind.SmartSwitch, Name = "Чужой" };
        var r = await c.SetSwitchAsync(foreign, true, "test");
        Assert.False(r.Ok); Assert.Contains("другому серверу", r.Message);
    }

    [Fact] public async Task Switch_WithoutConnection_ReportsError()
    {
        var e = new RpEnv(); await e.PairAsync();
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var c = new RustPlusDeviceController(e.Manager, hub, e.Devices, e.Clock);
        var r = await c.SetSwitchAsync(new RpDevice { ServerId = e.Server.Id, EntityId = 1, Kind = RpDeviceKind.SmartSwitch, Name = "x" }, true, "test");
        Assert.False(r.Ok);
    }

    [Fact] public async Task Chat_EventWithoutTimeAndPolledCopy_AreDeliveredOnce()
    {
        var e = new RpEnv(); await e.PairAsync();
        var polled = new List<RpChatMessage>();
        e.Next = () => { var g = new FakeGateway(); g.Chat = () => RpResult<IReadOnlyList<RpChatMessage>>.Success(polled.ToList()); return g; };
        var got = new List<RpChatMessage>(); e.Manager.ChatMessage += got.Add;
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        var gw = e.Created[0];
        gw.RaiseChat(new RpChatMessage(1, "Bob", "!time", null));
        polled.Add(new RpChatMessage(1, "Bob", "!time", e.Clock.UtcNow));
        await Task.Delay(50);
        gw.RaiseChat(new RpChatMessage(1, "Bob", "!time", e.Clock.UtcNow));
        Assert.Single(got);
    }

    [Fact] public void Bell_GrayHasPriority_ThenGreenRed()
    {
        var ok = new DeviceLive(null, DeviceAvailability.Available, null, DateTimeOffset.UtcNow);
        Assert.Equal(RpBellState.Green, RpBell.Resolve(true, ok));
        Assert.Equal(RpBellState.Red, RpBell.Resolve(false, ok));
        Assert.Equal(RpBellState.Gray, RpBell.Resolve(true, ok with { Availability = DeviceAvailability.Unavailable }));
        Assert.Equal(RpBellState.Gray, RpBell.Resolve(false, ok with { Availability = DeviceAvailability.Unknown }));
        Assert.Equal(RpBellState.Gray, RpBell.Resolve(true, null));
    }

    [Fact] public async Task NewAlarm_BellOnByDefault_AndKindComesFromStoredDevice()
    {
        var e = new RpEnv(); await e.PairAsync("Rust EU Main");
        var game = new RecordingGameNotifier(); var ev = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock, game);
        await e.Devices.AddAsync(e.Server.Id, 42, RpDeviceKind.SmartAlarm, "Base Alarm H1");
        // websocket-событие не знает типа и приходит как SmartSwitch — тип берётся из записи устройства
        Assert.True(await ev.HandleAsync(e.Server, new RpDeviceChange(42, new RpDeviceState(RpDeviceKind.SmartSwitch, true), e.Clock.UtcNow)));
        Assert.Single(game.Sent);
    }

    [Fact] public async Task FcmAlarm_ForUnknownServerId_StillFindsDeviceByTitle_AndNotifiesOnce()
    {
        var e = new RpEnv(); await e.PairAsync("Rust EU Main");
        await e.Devices.AddAsync(e.Server.Id, 42, RpDeviceKind.SmartAlarm, "Base Alarm H1");
        var game = new RecordingGameNotifier(); var ev = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock, game);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var router = new FcmEventRouter(e.Servers, e.Devices, new RpDevicePairingRepository(e.Db), ev, e.Manager, hub, e.Clock);
        var props = new Dictionary<string, string?> { ["Title"] = "Base Alarm H1", ["Message"] = "Тревога", ["ServerId"] = "00000000-0000-0000-0000-000000000001" };
        var r = await router.HandleAsync("OnAlarmTriggered", props);
        Assert.Equal(FcmEventKind.AlarmTriggered, r.Kind);
        Assert.Single(game.Sent);
        Assert.StartsWith("Base Alarm H1", game.Sent[0].OverlayText);
        await router.HandleAsync("OnAlarmTriggered", props);               // тот же сигнал из второго источника
        Assert.Single(game.Sent);
    }

    [Fact] public async Task FcmAlarm_UnknownDevice_IsStillShownByTitle()
    {
        var e = new RpEnv(); await e.PairAsync("Rust EU Main");
        var game = new RecordingGameNotifier(); var ev = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock, game);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var router = new FcmEventRouter(e.Servers, e.Devices, new RpDevicePairingRepository(e.Db), ev, e.Manager, hub, e.Clock);
        await router.HandleAsync("OnAlarmTriggered", new Dictionary<string, string?> { ["Title"] = "Новая сигнализация" });
        Assert.Single(game.Sent); Assert.Contains("Новая сигнализация", game.Sent[0].OverlayText);
    }
}

public class GridAndTextTests
{
    [Fact] public void Grid_CellsMatchRustMap()
    {
        Assert.Equal("A0", RpGrid.Cell(1, 3999, 4000));
        Assert.Equal("K12", RpGrid.Cell(10 * RpGrid.CellSize + 5, 4000 - (12 * RpGrid.CellSize + 5), 4000));
        Assert.Equal("AA0", RpGrid.Cell(26 * RpGrid.CellSize + 1, 3999, 4500));
        Assert.Null(RpGrid.Cell(null, 5, 4000)); Assert.Null(RpGrid.Cell(5, 5, null)); Assert.Null(RpGrid.Cell(-10, 5, 4000));
    }

    [Fact] public void AlarmText_IsDeviceNamePlusUserMessage_WithoutServer()
    {
        var d = RustPlusDeviceEvents.AlarmText("Base Alarm H1");
        Assert.Equal("Base Alarm H1\n\nСРАБОТАЛА СИГНАЛИЗАЦИЯ!", d.Overlay);
        var c = RustPlusDeviceEvents.AlarmText("Дверь", "Кто-то\\nу входа");
        Assert.Equal("Дверь: Кто-то у входа", c.Chat); Assert.Equal("Дверь\n\nКто-то\nу входа", c.Overlay);
    }
}

public class PerDeviceAlarmTextTests
{
    [Fact] public async Task EachAlarmUsesItsOwnText_AndPersists()
    {
        var e = new RpEnv(); await e.PairAsync("EU");
        var game = new RecordingGameNotifier(); var ev = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock, game);
        var a = await e.Devices.AddAsync(e.Server.Id, 1, RpDeviceKind.SmartAlarm, "Дверь");
        await e.Devices.AddAsync(e.Server.Id, 2, RpDeviceKind.SmartAlarm, "Гараж");
        var dev = (await e.Devices.ListAsync(e.Server.Id)).First(d => d.Id == a);
        dev.AlarmNotify = true;
        await e.Devices.UpdateAsync(dev);
        await e.Devices.SetAlarmSettingsAsync(a, "Кто-то у двери!", 2);
        Assert.Equal("Кто-то у двери!", (await e.Devices.ListAsync(e.Server.Id)).First(d => d.Id == a).AlarmText);

        await ev.HandleAsync(e.Server, new RpDeviceChange(1, new RpDeviceState(RpDeviceKind.SmartAlarm, true), e.Clock.UtcNow));
        await ev.HandleAsync(e.Server, new RpDeviceChange(2, new RpDeviceState(RpDeviceKind.SmartAlarm, true), e.Clock.UtcNow));
        Assert.Equal("Дверь\n\nКто-то у двери!", game.Sent[0].OverlayText);
        Assert.Equal("Дверь: Кто-то у двери!", game.Sent[0].ChatText);
        Assert.Equal("Гараж\n\nСРАБОТАЛА СИГНАЛИЗАЦИЯ!", game.Sent[1].OverlayText);      // у второй сигнализации стандартный текст
    }
}

public class SteamProfileInputTests
{
    [Theory]
    [InlineData("76561198000000001", "76561198000000001", null)]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/", "76561198000000001", null)]
    [InlineData("https://steamcommunity.com/id/some_name/", null, "some_name")]
    [InlineData("some_name", null, "some_name")]
    [InlineData("", null, null)]
    [InlineData("https://example.com/x", null, null)]
    public void ParsesProfileInput(string input, string? id, string? vanity)
    {
        var (i, v) = RustCompanion.Core.Net.SteamClient.ParseProfileInput(input);
        Assert.Equal(id, i); Assert.Equal(vanity, v);
    }
}

public class SteamPlaytimeTests
{
    [Fact] public void ParsesRustPlaytime()
    {
        var r = RustCompanion.Core.Net.SteamClient.ParsePlaytime("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":252490,\"playtime_forever\":12345}]}}");
        Assert.True(r.IsOk); Assert.Equal(12345, r.Value);
    }
    [Fact] public void HiddenGames_AreReportedNotInvented()
    {
        var r = RustCompanion.Core.Net.SteamClient.ParsePlaytime("{\"response\":{}}");
        Assert.False(r.IsOk); Assert.Contains("скрыл", r.Message);
    }
}

public class BattleMetricsLinkTests
{
    [Fact] public void LinkContainsSteamId_AndInvalidIdGivesNull()
    {
        var u = RustCompanion.Core.Watch.BattleMetricsLink.ForSteamId("76561198000000001");
        Assert.StartsWith("https://www.battlemetrics.com/players?", u); Assert.Contains("76561198000000001", u);
        Assert.Null(RustCompanion.Core.Watch.BattleMetricsLink.ForSteamId("abc")); Assert.Null(RustCompanion.Core.Watch.BattleMetricsLink.ForSteamId(null));
    }
}
