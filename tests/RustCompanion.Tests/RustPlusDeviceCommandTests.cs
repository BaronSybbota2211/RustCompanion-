using RustCompanion.Core.RustPlus;
using Xunit;

namespace RustCompanion.Tests;

public class RustPlusDeviceCommandTests
{
    private static RpDeviceCommandSettings Toggle(bool enabled = true) => new()
    {
        DeviceId = 1, Enabled = enabled, Prefix = "!", ToggleCommand = "свет на базе", AllowedSenders = "Ann; 76561198000000001"
    };

    [Fact]
    public void ExactMatch_UnicodePrefixesAndSeparateMode()
    {
        var s = Toggle();
        Assert.Equal(RpDeviceCommandAction.Toggle, RpDeviceCommandRules.Match(s, "  !свет на базе  "));
        Assert.Equal(RpDeviceCommandAction.None, RpDeviceCommandRules.Match(s, "!свет на базе пожалуйста"));
        Assert.Equal(RpDeviceCommandAction.None, RpDeviceCommandRules.Match(s, "!Свет на базе"));
        Assert.Equal(RpDeviceCommandAction.None, RpDeviceCommandRules.Match(s, "свет на базе"));
        s.Prefix = "/"; Assert.Equal(RpDeviceCommandAction.Toggle, RpDeviceCommandRules.Match(s, "/свет на базе"));
        s.Prefix = "."; s.Mode = RpDeviceCommandMode.Separate; s.OnCommand = "включить 💡"; s.OffCommand = "выключить 💡";
        Assert.Equal(RpDeviceCommandAction.TurnOn, RpDeviceCommandRules.Match(s, ".включить 💡"));
        Assert.Equal(RpDeviceCommandAction.TurnOff, RpDeviceCommandRules.Match(s, ".выключить 💡"));
        Assert.Equal(RpDeviceCommandAction.None, RpDeviceCommandRules.Match(s, ".включить"));
        Assert.Null(RpDeviceCommandRules.Validate(s));
        s.Prefix = "#"; Assert.NotNull(RpDeviceCommandRules.Validate(s));
    }

    [Fact]
    public void SenderFilter_ExactNameOrSteamId_EmptyAllowsTeamChat()
    {
        var s = Toggle();
        Assert.True(RpDeviceCommandRules.SenderAllowed(s, new RpChatMessage(2, "Ann", "!свет на базе", EventEnv.T0)));
        Assert.True(RpDeviceCommandRules.SenderAllowed(s, new RpChatMessage(76561198000000001, "Other", "!свет на базе", EventEnv.T0)));
        Assert.False(RpDeviceCommandRules.SenderAllowed(s, new RpChatMessage(3, "Ann2", "!свет на базе", EventEnv.T0)));
        s.AllowedSenders = ""; Assert.True(RpDeviceCommandRules.SenderAllowed(s, new RpChatMessage(3, "Any", "x", EventEnv.T0)));
    }

    private static async Task<(RpEnv Env, RpDeviceCommandRepository Repo, RustPlusDeviceCommandService Service, long DeviceId)> Connected(
        RpDeviceCommandSettings? settings = null, RpDeviceKind kind = RpDeviceKind.SmartSwitch)
    {
        var e = new RpEnv(); await e.PairAsync();
        var id = await e.Devices.AddAsync(e.Server.Id, 777, kind, "Свет");
        var repo = new RpDeviceCommandRepository(e.Db);
        settings ??= Toggle(); settings.DeviceId = id;
        await repo.SaveAsync(settings, e.Clock.UtcNow);
        var service = new RustPlusDeviceCommandService(e.Manager, e.Devices, repo, e.Clock);
        await service.InitializeAsync(); await service.SetProcessingEnabledAsync(true);
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        return (e, repo, service, id);
    }

    private static RpChatMessage Message(string text = "!свет на базе", int seconds = 0, ulong steamId = 2, string name = "Ann") =>
        new(steamId, name, text, EventEnv.T0.AddSeconds(seconds));

    [Fact]
    public async Task Toggle_ReadsThenVerifies_AndPersistentDedupPreventsRepeat()
    {
        var (e, repo, service, id) = await Connected();
        await service.HandleAsync(Message());
        Assert.True(e.Created[0].SwitchState);
        var history = await repo.HistoryAsync(e.Server.Id);
        Assert.Single(history); Assert.Equal("confirmed", history[0].Status); Assert.True(history[0].TargetState);

        await service.HandleAsync(Message()); // тот же SteamID/время/текст: ни опрос, ни событие не должны выполнить повторно
        Assert.True(e.Created[0].SwitchState); Assert.Single(await repo.HistoryAsync(e.Server.Id));

        var serviceAfterRestart = new RustPlusDeviceCommandService(e.Manager, e.Devices, repo, e.Clock);
        await serviceAfterRestart.InitializeAsync();
        await serviceAfterRestart.HandleAsync(Message());
        Assert.True(e.Created[0].SwitchState); Assert.Single(await repo.HistoryAsync(e.Server.Id));

        await service.HandleAsync(Message(seconds: 1)); // новое сообщение той же команды — законный повторный toggle
        Assert.False(e.Created[0].SwitchState);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task SeparateCommands_OnlyExactOnAndOff()
    {
        var settings = Toggle(); settings.Mode = RpDeviceCommandMode.Separate;
        settings.OnCommand = "включить свет"; settings.OffCommand = "выключить свет";
        var (e, repo, service, _) = await Connected(settings);
        await service.HandleAsync(Message("!включить свет"));
        Assert.True(e.Created[0].SwitchState);
        await service.HandleAsync(Message("!выключить свет", 1));
        Assert.False(e.Created[0].SwitchState);
        await service.HandleAsync(Message("!включить свет сейчас", 2));
        Assert.False(e.Created[0].SwitchState);
        Assert.Equal(2, (await repo.HistoryAsync(e.Server.Id)).Count);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task UnauthorizedSender_PartialText_AndDisabledDevice_DoNothing()
    {
        var (e, _, service, _) = await Connected();
        await service.HandleAsync(Message(steamId: 3, name: "Mallory"));
        await service.HandleAsync(Message("!свет на базе дополнительно", 1));
        Assert.False(e.Created[0].SwitchState);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task UnavailableDevice_NeverReceivesSetCommand()
    {
        var calls = 0;
        var (e, repo, service, _) = await Connected();
        e.Created[0].DeviceRead = (_, _) => RpResult<RpDeviceState>.Fail("not_found", "entity unavailable");
        e.Created[0].SetSwitch = _ => { calls++; return RpResult.Success; };
        await service.HandleAsync(Message());
        Assert.Equal(0, calls);
        var h = Assert.Single(await repo.HistoryAsync(e.Server.Id));
        Assert.Equal("failed", h.Status); Assert.Contains("не отправлена", h.Detail);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task UnconfirmedSwitch_IsNotReportedAsSuccess()
    {
        var (e, repo, service, _) = await Connected();
        e.Created[0].ApplyOnSet = false;
        await service.HandleAsync(Message());
        var h = Assert.Single(await repo.HistoryAsync(e.Server.Id));
        Assert.Equal("failed", h.Status); Assert.Contains("не изменило состояние", h.Detail);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task UnconfirmedSwitch_SharedIndicatorBecomesUnknown()
    {
        var e = new RpEnv(); await e.PairAsync();
        var id = await e.Devices.AddAsync(e.Server.Id, 777, RpDeviceKind.SmartSwitch, "Свет");
        var repo = new RpDeviceCommandRepository(e.Db); var settings = Toggle(); settings.DeviceId = id;
        await repo.SaveAsync(settings, e.Clock.UtcNow);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var service = new RustPlusDeviceCommandService(e.Manager, e.Devices, repo, e.Clock, hub);
        await service.InitializeAsync(); await service.SetProcessingEnabledAsync(true);
        await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        hub.SetState(e.Server.Id, 777, new RpDeviceState(RpDeviceKind.SmartSwitch, false));
        hub.SetAvailability(e.Server.Id, 777, DeviceAvailability.Available, null);
        e.Created[0].ApplyOnSet = false;

        await service.HandleAsync(Message());
        Assert.Equal(DeviceAvailability.Unknown, hub.GetLive(e.Server.Id, 777)!.Availability);
        Assert.Equal("failed", Assert.Single(await repo.HistoryAsync(e.Server.Id)).Status);
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task GlobalPause_AndWrongConnection_PreventExecution()
    {
        var (e, _, service, _) = await Connected();
        await service.SetProcessingEnabledAsync(false);
        await service.HandleAsync(Message());
        Assert.False(e.Created[0].SwitchState);
        await service.SetProcessingEnabledAsync(true);
        await e.Manager.DisconnectAsync();
        await service.HandleAsync(Message());
        Assert.False(e.Created[0].SwitchState);
    }

    [Fact]
    public async Task NonSwitch_IsReadableButNeverControlledByChat()
    {
        var (e, repo, service, _) = await Connected(kind: RpDeviceKind.SmartAlarm);
        var calls = 0; e.Created[0].SetSwitch = _ => { calls++; return RpResult.Success; };
        await service.HandleAsync(Message());
        Assert.Equal(0, calls); Assert.Empty(await repo.HistoryAsync(e.Server.Id));
        await e.Manager.DisconnectAsync();
    }

    [Fact]
    public async Task SettingsPauseAndHistory_SurviveRepositoryRecreation()
    {
        var e = new RpEnv(); await e.PairAsync();
        var id = await e.Devices.AddAsync(e.Server.Id, 99, RpDeviceKind.SmartSwitch, "Лампа");
        var repo = new RpDeviceCommandRepository(e.Db); var s = Toggle(); s.DeviceId = id;
        await repo.SaveAsync(s, e.Clock.UtcNow); await repo.SetProcessingEnabledAsync(true);
        await repo.AddHistoryAsync(e.Server.Id, id, "key", Message(), "!свет на базе", true, "confirmed", "Включено", e.Clock.UtcNow);

        var loaded = await new RpDeviceCommandRepository(e.Db).GetAsync(id);
        Assert.True(loaded.Enabled); Assert.Equal("свет на базе", loaded.ToggleCommand); Assert.Equal("Ann; 76561198000000001", loaded.AllowedSenders);
        Assert.True(await new RpDeviceCommandRepository(e.Db).GetProcessingEnabledAsync());
        Assert.Single(await new RpDeviceCommandRepository(e.Db).HistoryAsync(e.Server.Id));
        await e.Servers.DeleteAsync(e.Server.Id); // каскад не оставляет настройки и историю удалённого сервера
        Assert.Empty(await new RpDeviceCommandRepository(e.Db).HistoryAsync(e.Server.Id));
    }
}
