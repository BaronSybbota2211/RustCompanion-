using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.RustPlus;
using Xunit;

namespace RustCompanion.Tests;

internal sealed class FakeGateway : IRustPlusGateway
{
    public RpResult ConnectResult = RpResult.Success;
    public Func<RpResult<RpServerInfo>> Info = () => RpResult<RpServerInfo>.Success(new RpServerInfo("Test", 10, 100, 2, 3000, "Procedural", null));
    public Func<RpResult<RpTime>> Time = () => RpResult<RpTime>.Success(new RpTime(14.5, 6, 19.5, 60, 1));
    public Func<RpResult<IReadOnlyList<RpChatMessage>>> Chat = () => RpResult<IReadOnlyList<RpChatMessage>>.Success(new List<RpChatMessage>());
    public Func<bool, RpResult> SetSwitch = _ => RpResult.Success;
    public bool? SwitchState = false; public bool ApplyOnSet = true;
    public List<string> Sent = new(); public bool Disposed; public int InfoCalls;
    public bool IsConnected { get; private set; }
    public event Action<RpChatMessage>? ChatReceived; public event Action<ulong, RpDeviceState>? DeviceChanged; public event Action<string>? ConnectionLost;
    public void RaiseChat(RpChatMessage m) => ChatReceived?.Invoke(m);
    public void RaiseLost() => ConnectionLost?.Invoke("test");
    public RpCredentials? LastCredentials;
    public Task<RpResult> ConnectAsync(RpCredentials c, CancellationToken ct) { LastCredentials = c; IsConnected = ConnectResult.Ok; return Task.FromResult(ConnectResult); }
    public Task<RpResult<RpServerInfo>> GetInfoAsync(CancellationToken ct) { InfoCalls++; return Task.FromResult(Info()); }
    public Task<RpResult<RpTime>> GetTimeAsync(CancellationToken ct) => Task.FromResult(Time());
    public Task<RpResult<RpTeam>> GetTeamAsync(CancellationToken ct) => Task.FromResult(RpResult<RpTeam>.Success(new RpTeam(1, new List<RpMember> { new(1, "Me", true, true, 100, 200) })));
    public Task<RpResult<IReadOnlyList<RpChatMessage>>> GetTeamChatAsync(CancellationToken ct) => Task.FromResult(Chat());
    public Task<RpResult> SendTeamMessageAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.FromResult(RpResult.Success); }
    public Func<bool> DeviceOk = () => true;
    public Func<ulong, RpDeviceKind, RpResult<RpDeviceState>>? DeviceRead;
    public Task<RpResult<RpDeviceState>> GetDeviceAsync(ulong id, RpDeviceKind k, CancellationToken ct) => Task.FromResult(DeviceRead?.Invoke(id, k) ?? (DeviceOk() ? RpResult<RpDeviceState>.Success(new RpDeviceState(k, SwitchState)) : RpResult<RpDeviceState>.Fail("timeout", "нет ответа устройства")));
    public Task<RpResult> SetSwitchAsync(ulong id, bool on, CancellationToken ct) { var r = SetSwitch(on); if (r.Ok && ApplyOnSet) SwitchState = on; return Task.FromResult(r); }
    public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
}

internal sealed class RpEnv
{
    public Database Db; public PairedServerRepository Servers; public RpDeviceRepository Devices; public MemorySecrets Secrets = new(); public FakeClock Clock = new(); public FakeNotifier Notifier = new(); public SettingsProvider Settings = new();
    public List<FakeGateway> Created = new(); public Func<FakeGateway>? Next; public RustPlusManager Manager; public PairedServer Server = null!;

    public RpEnv(Func<FakeGateway>? next = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Db = new Database(TempDb.NewPath()); Db.Initialize(); Servers = new PairedServerRepository(Db, Secrets); Devices = new RpDeviceRepository(Db);
        Next = next ?? (() => new FakeGateway());
        Settings.Update(new AppSettings { RustPlusPollSeconds = 5 });
        Manager = new RustPlusManager(() => { var g = Next!(); Created.Add(g); return g; }, Servers, Clock, Notifier, Settings, null, delay ?? ((t, ct) => Task.Delay(5, ct)));
    }
    public async Task PairAsync(string name = "Srv", string? rustPlusId = null) => Server = await Servers.UpsertAsync(name, "1.2.3.4", 28082, 76561198000000000UL, 123456, Clock.UtcNow, rustPlusId: rustPlusId);
    public static async Task<bool> WaitFor(Func<bool> cond, int ms = 3000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(10); } return cond(); }
}

public class RustPlusConnectionTests
{
    [Fact] public async Task Connects_ShowsConnectedOnlyAfterRealResponse_AndDisconnectsCleanly()
    {
        var e = new RpEnv(); await e.PairAsync();
        Assert.Equal(RustPlusState.Disconnected, e.Manager.State);
        Assert.True((await e.Manager.ConnectAsync(e.Server.Id)).Ok);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        Assert.Equal("Test", e.Manager.Snapshot.Info!.Name); Assert.Equal(14.5, e.Manager.Snapshot.Time!.Time); Assert.Single(e.Manager.Snapshot.Team!.Members);
        await e.Manager.DisconnectAsync();
        Assert.Equal(RustPlusState.Disconnected, e.Manager.State); Assert.True(e.Created[0].Disposed);
        var count = e.Created.Count; await Task.Delay(100); Assert.Equal(count, e.Created.Count);                      // после отключения повторных подключений нет
    }

    [Fact] public async Task ConnectFailure_IsNotReportedAsConnected_AndRetries()
    {
        var calls = 0;
        var e = new RpEnv(() => { calls++; return new FakeGateway { ConnectResult = calls < 3 ? RpResult.Fail("timeout", "нет ответа") : RpResult.Success }; });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Reconnecting || e.Manager.State == RustPlusState.Connecting));
        Assert.NotEqual(RustPlusState.Connected, e.Manager.State == RustPlusState.Connected && calls < 3 ? RustPlusState.Connected : RustPlusState.Disconnected);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected)); Assert.True(calls >= 3);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task NetworkDrop_Reconnects_AndNotifiesOnlyIfEnabled()
    {
        FakeGateway? first = null;
        var e = new RpEnv(() => { var g = new FakeGateway(); first ??= g; return g; });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        first!.RaiseLost();
        Assert.True(await RpEnv.WaitFor(() => e.Created.Count >= 2));
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        Assert.Contains(NotifyEventIds.RustPlusConnection, e.Notifier.EventIds);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task AuthFailure_StopsRetrying_WithClearMessage()
    {
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail("unauthorized", "invalid player token") });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.AuthFailed));
        Assert.Contains("заново", e.Manager.StatusMessage); var n = e.Created.Count; await Task.Delay(150); Assert.Equal(n, e.Created.Count);   // не долбим сервер
    }

    [Fact] public async Task AuthFailureDuringSession_IsDetected()
    {
        var bad = false;
        var e = new RpEnv(() => new FakeGateway { Info = () => bad ? RpResult<RpServerInfo>.Fail("unauthorized", "token expired") : RpResult<RpServerInfo>.Success(new RpServerInfo("S", 1, 2, null, null, null, null)) });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected)); bad = true;
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.AuthFailed));
    }

    [Fact] public async Task MissingToken_OrServer_FailsWithoutFakeConnection()
    {
        var e = new RpEnv();
        Assert.Equal("not_found", (await e.Manager.ConnectAsync(999)).Code);
        await e.PairAsync(); e.Secrets.Delete(SecretNames.RustPlusToken(e.Server.Id));
        Assert.Equal("no_token", (await e.Manager.ConnectAsync(e.Server.Id)).Code); Assert.Equal(RustPlusState.AuthFailed, e.Manager.State); Assert.Empty(e.Created);
    }

    [Fact] public async Task Token_IsStoredInSecretsOnly_NotInDatabase()
    {
        var e = new RpEnv(); await e.PairAsync();
        Assert.Equal(123456, e.Servers.GetToken(e.Server.Id)); Assert.False(File.ReadAllBytes(e.Db.ConnectionString.Split('=')[1].Split(';')[0]).Length == 0);
        var raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(e.Db.ConnectionString.Split('=')[1].Split(';')[0]));
        Assert.DoesNotContain("123456", raw.Replace("28082", ""));                                                // токена игрока в файле БД нет
        await e.Servers.DeleteAsync(e.Server.Id); Assert.Null(e.Servers.GetToken(e.Server.Id));
    }

    [Fact] public async Task SendMessage_RequiresConnection_AndTruncates()
    {
        var e = new RpEnv(); await e.PairAsync();
        Assert.Equal("not_connected", (await e.Manager.SendMessageAsync("hi")).Code);
        await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        await e.Manager.SendMessageAsync(new string('x', 500));
        Assert.Equal(RustPlusManager.MaxMessageLength, e.Created[0].Sent[0].Length); Assert.Equal("empty", (await e.Manager.SendMessageAsync("  ")).Code);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Chat_HistoryNotReplayed_NewMessagesDeliveredOnce()
    {
        var msgs = new List<RpChatMessage> { new(1, "A", "old", EventEnv.T0) };
        var e = new RpEnv(() => new FakeGateway { Chat = () => RpResult<IReadOnlyList<RpChatMessage>>.Success(msgs.ToList()) });
        var got = new List<string>(); e.Manager.ChatMessage += m => { lock (got) got.Add(m.Text); };
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        await Task.Delay(60); Assert.Empty(got);                                                                      // история при подключении — не новые сообщения
        msgs.Add(new(2, "B", "new", EventEnv.T0.AddSeconds(5)));
        Assert.True(await RpEnv.WaitFor(() => got.Contains("new"))); await Task.Delay(60);
        Assert.Single(got, "new");                                                                                    // повторные опросы не дублируют
        e.Created[0].RaiseChat(new RpChatMessage(2, "B", "new", EventEnv.T0.AddSeconds(5))); await Task.Delay(30); Assert.Single(got, "new");   // событие и опрос — один раз
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Switch_SuccessOnlyWhenStateConfirmed()
    {
        var e = new RpEnv(); await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        var ok = await e.Manager.SetSwitchVerifiedAsync(55, true); Assert.True(ok.Ok); Assert.Contains("подтверждено", ok.Message);
        e.Created[0].ApplyOnSet = false; e.Created[0].SwitchState = false;
        var bad = await e.Manager.SetSwitchVerifiedAsync(55, true); Assert.False(bad.Ok); Assert.Equal("unconfirmed", bad.Code);        // команда принята, состояние не изменилось
        e.Created[0].SetSwitch = _ => RpResult.Fail("error", "нет доступа");
        var err = await e.Manager.SetSwitchVerifiedAsync(55, true); Assert.False(err.Ok); Assert.Contains("нет доступа", err.Message);
        await e.Manager.DisconnectAsync();
        Assert.Equal("not_connected", (await e.Manager.SetSwitchVerifiedAsync(55, true)).Code);
    }
}

public class BotTests
{
    private sealed class Timers : ITimerSource { public List<(string, TimeSpan)> L = new(); public IReadOnlyList<(string Name, TimeSpan Remaining)> Active(DateTimeOffset now) => L; }
    private static BotContext Ctx(RustPlusSnapshot s, ITimerSource t, string lang = "ru") => new(s, t, EventEnv.T0, lang);
    private static BotSettings On(string prefix = "!", string lang = "ru", params string[] disabled) => new(true, prefix, lang, new HashSet<string>(disabled));
    private static RpChatMessage M(string text) => new(1, "Ann", text, EventEnv.T0);
    private static RustPlusSnapshot Snap() => new() { Info = new RpServerInfo("S", 120, 200, 5, null, null, null), Time = new RpTime(14.5, 6, 19.5, 60, 1) };

    [Fact] public void Time_Pop_Timer_Replies_AreFormatted()
    {
        var e = BotCommandEngine.CreateDefault(); var t = new Timers { L = { ("Чинук", TimeSpan.FromMinutes(12)) } };
        Assert.Equal("[RC] Время в игре 14:30, день", e.Handle(M("!time"), On(), Ctx(Snap(), t)));
        Assert.Equal("[RC] Игроков: 120/200, в очереди 5", e.Handle(M("!pop"), On(), Ctx(Snap(), t)));
        Assert.Equal("[RC] Таймеры: Чинук 0:12:00", e.Handle(M("!timer"), On(), Ctx(Snap(), t)));
        Assert.Equal("[RC] Players: 120/200, queue 5", e.Handle(M("!pop"), On(lang: "en"), Ctx(Snap(), t, "en")));
        Assert.Equal("[RC] Время в игре 23:00, ночь", e.Handle(M("!time"), On(), Ctx(new RustPlusSnapshot { Time = new RpTime(23, 6, 19.5, null, null) }, t)));
    }

    [Fact] public void DisabledBot_UnknownDisabledOrWithoutPrefix_StaySilent_AndNoLoop()
    {
        var e = BotCommandEngine.CreateDefault(); var t = new Timers();
        Assert.Null(e.Handle(M("!time"), new BotSettings(false, "!", "ru", new HashSet<string>()), Ctx(Snap(), t)));
        Assert.Null(e.Handle(M("!nonsense"), On(), Ctx(Snap(), t))); Assert.Null(e.Handle(M("time"), On(), Ctx(Snap(), t)));
        Assert.Null(e.Handle(M("!pop"), On(disabled: "pop"), Ctx(Snap(), t)));
        Assert.Null(e.Handle(M("[RC] Игроков: 1/2"), On(), Ctx(Snap(), t)));                                          // собственный ответ не обрабатывается
        Assert.NotNull(e.Handle(M("#pop"), On(prefix: "#"), Ctx(Snap(), t)));                                           // настраиваемый префикс
    }

    [Fact] public void UnsupportedCommand_ExplainsInsteadOfInventing()
    {
        var e = BotCommandEngine.CreateDefault();
        var reply = e.Handle(M("!time"), On(), Ctx(new RustPlusSnapshot(), new Timers()));
        Assert.Contains("нет данных", reply); Assert.DoesNotContain("14", reply);
    }

    private sealed class Echo : IBotCommand
    {
        public string Name => "echo"; public IReadOnlyList<string> Aliases => Array.Empty<string>(); public string Describe(string l) => "echo"; public bool IsSupported(RustPlusSnapshot s) => true;
        public string Execute(BotContext c, string args) => args;
    }
    [Fact] public void NewCommands_CanBeAdded_WithoutChangingTheEngine() =>
        Assert.Equal("[RC] hello", BotCommandEngine.CreateDefault().Register(new Echo()).Handle(M("!echo hello"), On(), Ctx(Snap(), new Timers())));

    [Fact] public void LongReplies_AreClamped() =>
        Assert.True(BotCommandEngine.CreateDefault().Register(new Echo()).Handle(M("!echo " + new string('z', 400)), On(), Ctx(Snap(), new Timers()))!.Length <= RustPlusManager.MaxMessageLength);

    [Fact] public void Settings_FromAppSettings_AndValidation()
    {
        var s = BotSettings.From(new AppSettings { BotEnabled = true, BotPrefix = "?", BotLanguage = "en", BotDisabledCommands = "pop, timer" });
        Assert.True(s.Enabled); Assert.Equal("?", s.Prefix); Assert.Contains("timer", s.Disabled);
        Assert.False(BotSettings.From(new AppSettings()).Enabled);                                                      // по умолчанию выключен
        Assert.NotEmpty(new AppSettings { BotPrefix = "" }.Validate()); Assert.NotEmpty(new AppSettings { BotLanguage = "de" }.Validate()); Assert.NotEmpty(new AppSettings { RustPlusPollSeconds = 1 }.Validate());
    }

    [Fact] public async Task BotService_RepliesThroughConnection_OnlyWhenEnabled()
    {
        var e = new RpEnv(); await e.PairAsync();
        e.Settings.Update(new AppSettings { RustPlusPollSeconds = 5, BotEnabled = true });
        var bot = new RustPlusBotService(e.Manager, BotCommandEngine.CreateDefault(), e.Settings, new Timers(), e.Clock);
        await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        Assert.StartsWith("[RC] Игроков", await bot.HandleAsync(M("!pop")));
        Assert.Contains(e.Created[0].Sent, s => s.StartsWith("[RC] Игроков"));
        e.Settings.Update(new AppSettings { RustPlusPollSeconds = 5, BotEnabled = false });
        Assert.Null(await bot.HandleAsync(M("!pop"))); await e.Manager.DisconnectAsync();
    }
}

public class RustPlusDeviceTests
{
    private static async Task<(RpEnv E, RustPlusDeviceEvents Ev, long DeviceId)> Make(bool alarmNotify)
    {
        var e = new RpEnv(); await e.PairAsync();
        var id = await e.Devices.AddAsync(e.Server.Id, 777, RpDeviceKind.SmartAlarm, "Сигнализация у входа");
        await e.Devices.UpdateAsync(new RpDevice { Id = id, Name = "Сигнализация у входа", AlarmNotify = alarmNotify });
        return (e, new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock), id);
    }
    private static RpDeviceChange On(DateTimeOffset at) => new(777, new RpDeviceState(RpDeviceKind.SmartAlarm, true), at);

    [Fact] public async Task Alarm_NotifiesOnce_WithinDedupeWindow_AndAgainAfter()
    {
        var (e, ev, id) = await Make(true);
        Assert.True(await ev.HandleAsync(e.Server, On(EventEnv.T0)));
        Assert.False(await ev.HandleAsync(e.Server, On(EventEnv.T0.AddSeconds(3))));                      // дубль (событие + push) не создаёт второе уведомление
        Assert.True(await ev.HandleAsync(e.Server, On(EventEnv.T0.AddSeconds(30))));
        Assert.Equal(2, e.Notifier.Sent.Count); Assert.Contains("Сигнализация у входа", e.Notifier.Sent[0].Title); Assert.All(e.Notifier.EventIds, x => Assert.Equal(NotifyEventIds.RustPlusAlarm, x));
        Assert.Equal(2, (await e.Devices.EventsAsync(id, 10)).Count(x => x.Kind == "alarm"));
    }

    [Fact] public async Task Alarm_IndividuallyDisabled_StillLoggedButSilent()
    {
        var (e, ev, id) = await Make(false);
        Assert.False(await ev.HandleAsync(e.Server, On(EventEnv.T0))); Assert.Empty(e.Notifier.Sent);
        Assert.Single(await e.Devices.EventsAsync(id, 10));
    }

    [Fact] public async Task Alarm_NotificationsOff_GlobalSwitch_NoShow()
    {
        var (e, ev, _) = await Make(true); e.Notifier.Choice = DeliveryChoice.Off;
        Assert.False(await ev.HandleAsync(e.Server, On(EventEnv.T0))); Assert.Empty(e.Notifier.Sent);
    }

    [Fact] public async Task UnknownDevice_AndSwitchHistory()
    {
        var (e, ev, _) = await Make(true);
        Assert.False(await ev.HandleAsync(e.Server, new RpDeviceChange(999, new RpDeviceState(RpDeviceKind.SmartAlarm, true), EventEnv.T0)));
        var sw = await e.Devices.AddAsync(e.Server.Id, 5, RpDeviceKind.SmartSwitch, "Лампа");
        await ev.HandleAsync(e.Server, new RpDeviceChange(5, new RpDeviceState(RpDeviceKind.SmartSwitch, true), EventEnv.T0));
        await ev.HandleAsync(e.Server, new RpDeviceChange(5, new RpDeviceState(RpDeviceKind.SmartSwitch, true), EventEnv.T0.AddSeconds(1)));   // то же состояние — не событие
        await ev.HandleAsync(e.Server, new RpDeviceChange(5, new RpDeviceState(RpDeviceKind.SmartSwitch, false), EventEnv.T0.AddSeconds(2)));
        Assert.Equal(new[] { "switch_off", "switch_on" }, (await e.Devices.EventsAsync(sw, 10)).Select(x => x.Kind));
    }

    [Fact] public async Task Devices_CrudGroupingAndPersistence()
    {
        var e = new RpEnv(); await e.PairAsync();
        var a = await e.Devices.AddAsync(e.Server.Id, 1, RpDeviceKind.SmartSwitch, "Дверь");
        await e.Devices.AddAsync(e.Server.Id, 1, RpDeviceKind.SmartSwitch, "Другое имя");                        // повтор по entity не плодит дубликат и не затирает имя
        var list = await e.Devices.ListAsync(e.Server.Id); Assert.Single(list); Assert.Equal("Дверь", list[0].Name);
        await e.Devices.UpdateAsync(new RpDevice { Id = a, Name = "Входная дверь", Group = "Склад", Note = "код 1234 не хранить", AlarmNotify = true });
        var again = (await e.Devices.ListAsync(e.Server.Id))[0]; Assert.Equal(("Входная дверь", "Склад", true), (again.Name, again.Group, again.AlarmNotify));
        await e.Servers.DeleteAsync(e.Server.Id); Assert.Empty(await e.Devices.ListAsync(e.Server.Id));                 // устройства удаляются вместе с сервером
    }

    [Fact] public void RustPlusEventIds_AreInCatalog_AndErrorsClassified()
    {
        Assert.Contains(NotifyCatalog.All, x => x.Id == NotifyEventIds.RustPlusAlarm); Assert.Contains(NotifyCatalog.All, x => x.Id == NotifyEventIds.RustPlusConnection);
        Assert.True(RpErrors.IsAuth("unauthorized", null)); Assert.True(RpErrors.IsAuth(null, "Invalid player token")); Assert.False(RpErrors.IsAuth("timeout", "no response"));
    }
}

public class FcmMapperTests
{
    private static Dictionary<string, string?> P(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact] public void DevicePairing_IsRecognized_ByNameOrEntityType()
    {
        var a = FcmMapper.Map("OnSmartSwitchPairing", P(("EntityId", "123"), ("Name", "Лампа")));
        Assert.Equal((FcmEventKind.DevicePairing, 123UL, RpDeviceKind.SmartSwitch, "Лампа"), (a.Kind, a.EntityId, a.DeviceKind, a.Name));
        var b = FcmMapper.Map("OnEntityPairing", P(("Id", "5"), ("EntityType", "2")));
        Assert.Equal((FcmEventKind.DevicePairing, RpDeviceKind.SmartAlarm), (b.Kind, b.DeviceKind));
        Assert.Equal(RpDeviceKind.StorageMonitor, FcmMapper.Map("OnStorageMonitorPairing", P(("EntityId", "9"))).DeviceKind);
    }

    [Fact] public void ServerPairing_AlarmTrigger_AndUnknown()
    {
        var s = FcmMapper.Map("OnServerPairing", P(("Ip", "1.2.3.4"), ("Port", "28082")));
        Assert.Equal((FcmEventKind.ServerPairing, "1.2.3.4", 28082), (s.Kind, s.Ip, s.Port));
        Assert.Equal(FcmEventKind.AlarmTriggered, FcmMapper.Map("OnAlarmNotification", P(("EntityId", "77"))).Kind);
        Assert.Equal(FcmEventKind.Unknown, FcmMapper.Map("OnSomethingNew", P(("X", "1"))).Kind);
        Assert.Equal(FcmEventKind.Unknown, FcmMapper.Map("OnSmartSwitchPairing", P()).Kind);                           // без ID устройство не создаётся
    }
}


/// <summary>Facepunch закрыл прямое подключение к Rust+ (HTTP 418): распознавание, поведение менеджера, новые коды отказа.</summary>
public class RustPlusDirectRemovedTests
{
    private const string Addr418 = "The server returned status code '418' when status code '101' was expected";

    [Fact] public void Http418_IsRecognized_ByCode_AndByMessage_AndIsNotAuth()
    {
        Assert.True(RpErrors.IsDirectRemoved(RpErrors.DirectRemoved, null));
        Assert.True(RpErrors.IsDirectRemoved("WebSocketException", Addr418));
        Assert.True(RpErrors.IsDirectRemoved(RpResult.Fail("x", Addr418)));
        Assert.True(RpErrors.IsDirectRemoved(RpResult.Fail(RpErrors.DirectRemoved, RpErrors.DirectRemovedMessage)));
        Assert.False(RpErrors.IsDirectRemoved(RpResult.Success));
        Assert.False(RpErrors.IsDirectRemoved("timeout", "Unable to connect to the remote server"));
        Assert.False(RpErrors.IsDirectRemoved("x", "status code '403'"));
        // 418 — не ошибка авторизации: ни код, ни тексты не должны ловиться IsAuth (иначе менеджер остановит повторы и попросит повторное сопряжение)
        Assert.False(RpErrors.IsAuth(RpErrors.DirectRemoved, RpErrors.DirectRemovedMessage));
        Assert.False(RpErrors.IsAuth("WebSocketException", Addr418));
        Assert.False(RpErrors.IsAuth(RpResult.Fail(RpErrors.DirectRemoved, RpErrors.DirectRemovedMessage + " Сервер: 1.2.3.4:28082.")));
        Assert.Contains("HTTP 418", RpErrors.DirectRemovedMessage);
        Assert.DoesNotContain("заново", RpErrors.DirectRemovedMessage);
    }

    [Fact] public void NewCodes_AreNotAuth_AndKeepTheirMeaning()
    {
        foreach (var code in new[] { RpErrors.CompanionLoginRejected, RpErrors.CompanionServerUnknown, RpErrors.CompanionNotReady, RpErrors.DirectRemoved })
            Assert.False(RpErrors.IsAuth(code, null));
        Assert.True(RpErrors.IsPlayerTokenRejected(RpResult.Fail("not_found", null)));
        Assert.True(RpErrors.IsPlayerTokenRejected(RpResult.Fail("x", "not_found")));
        Assert.False(RpErrors.IsPlayerTokenRejected(RpResult.Fail("timeout", "нет ответа")));
        Assert.False(RpErrors.IsPlayerTokenRejected(RpResult.Success));
    }

    [Fact] public async Task Manager_On418_DoesNotGoToAuthFailed_ShowsHttp418_AndWaitsFiveMinutes()
    {
        var delays = new List<TimeSpan>();
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail(RpErrors.DirectRemoved, RpErrors.DirectRemovedMessage + " Сервер: 1.2.3.4:28082.") },
            async (t, ct) => { lock (delays) delays.Add(t); await Task.Delay(Timeout.Infinite, ct); });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => { lock (delays) return delays.Count >= 1; }));
        Assert.Equal(RustPlusManager.DirectRemovedPause, delays[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), RustPlusManager.DirectRemovedPause);
        Assert.NotEqual(RustPlusState.AuthFailed, e.Manager.State); Assert.Equal(RustPlusState.Reconnecting, e.Manager.State);
        Assert.Contains("HTTP 418", e.Manager.StatusMessage); Assert.DoesNotContain("заново", e.Manager.StatusMessage);
        Assert.Contains("5 мин", e.Manager.StatusMessage);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Manager_On418InMessageOnly_IsAlsoTreatedAsDirectRemoved()
    {
        var delays = new List<TimeSpan>();
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail("WebSocketException", Addr418) },
            async (t, ct) => { lock (delays) delays.Add(t); await Task.Delay(Timeout.Infinite, ct); });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => { lock (delays) return delays.Count >= 1; }));
        Assert.Equal(RustPlusManager.DirectRemovedPause, delays[0]); Assert.NotEqual(RustPlusState.AuthFailed, e.Manager.State);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Manager_OtherErrors_KeepShortBackoff()
    {
        var delays = new List<TimeSpan>();
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail("timeout", "нет ответа") },
            async (t, ct) => { lock (delays) delays.Add(t); await Task.Delay(Timeout.Infinite, ct); });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => { lock (delays) return delays.Count >= 1; }));
        Assert.Equal(RustPlusManager.Backoff[0], delays[0]);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Manager_CompanionLogin403_StopsRetrying_WithLoginHint_NotPairingHint()
    {
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail(RpErrors.CompanionLoginRejected, "Facepunch отклонил вход в Rust+ (HTTP 403): токен входа устарел. Нажмите «1. Вход через Steam».") });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.AuthFailed));
        Assert.Contains("HTTP 403", e.Manager.StatusMessage); Assert.Contains("Вход через Steam", e.Manager.StatusMessage);
        var n = e.Created.Count; await Task.Delay(150); Assert.Equal(n, e.Created.Count);
    }

    [Fact] public async Task Manager_CompanionServerUnknown404_StopsRetrying()
    {
        var e = new RpEnv(() => new FakeGateway { ConnectResult = RpResult.Fail(RpErrors.CompanionServerUnknown, "Facepunch не знает сервер 1.2.3.4:28082 (HTTP 404).") });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.AuthFailed)); Assert.Contains("HTTP 404", e.Manager.StatusMessage);
        var n = e.Created.Count; await Task.Delay(150); Assert.Equal(n, e.Created.Count);
    }

    [Fact] public async Task Manager_GetInfoNotFound_MeansPlayerTokenRejected_StopsRetrying()
    {
        var e = new RpEnv(() => new FakeGateway { Info = () => RpResult<RpServerInfo>.Fail("not_found", "not_found") });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.AuthFailed));
        Assert.Contains("not_found", e.Manager.StatusMessage); Assert.Contains("заново", e.Manager.StatusMessage);
        var n = e.Created.Count; await Task.Delay(150); Assert.Equal(n, e.Created.Count);
    }

    [Fact] public async Task Manager_DeviceNotFound_IsNotAnAuthProblem()
    {
        // «not_found» в ответе на запрос устройства = устройство удалено; это не повод сбрасывать сопряжение сервера
        var e = new RpEnv(() => new FakeGateway { DeviceRead = (_, k) => RpResult<RpDeviceState>.Fail("not_found", "not_found") });
        await e.PairAsync(); await e.Manager.ConnectAsync(e.Server.Id); Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        var r = await e.Manager.ReadDeviceAsync(5, RpDeviceKind.SmartSwitch);
        Assert.False(r.Ok); Assert.Equal(RustPlusState.Connected, e.Manager.State);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task Manager_PassesServerIdAndCompanionToken_ToGateway_AndRereadsThemOnRetry()
    {
        const string serverId = "7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40"; const string login = "login-token-value-123456";
        var calls = new List<FakeGateway>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var pauses = 0;
        var e = new RpEnv(() => { var g = new FakeGateway { ConnectResult = calls.Count == 0 ? RpResult.Fail(RpErrors.CompanionNotReady, "нет токена входа") : RpResult.Success }; calls.Add(g); return g; },
            async (t, ct) => { if (Interlocked.Increment(ref pauses) == 1) await gate.Task.WaitAsync(ct); else await Task.Delay(5, ct); });   // первая пауза держится, пока тест не «войдёт через Steam»
        await e.PairAsync(rustPlusId: serverId); await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => calls.Count >= 1));
        Assert.Equal(serverId, calls[0].LastCredentials!.ServerId); Assert.Null(calls[0].LastCredentials!.CompanionToken);
        e.Servers.SetCompanionToken(login);                                                                              // пользователь вошёл через Steam за время паузы
        gate.SetResult();
        Assert.True(await RpEnv.WaitFor(() => e.Manager.IsConnected));
        Assert.Equal(login, calls[^1].LastCredentials!.CompanionToken); Assert.Equal(123456, calls[^1].LastCredentials!.PlayerToken);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task CompanionToken_IsStoredInSecrets_NotInDatabase_AndRemovable()
    {
        var e = new RpEnv(); await e.PairAsync(); Assert.Null(e.Servers.GetCompanionToken());
        e.Servers.SetCompanionToken("companion-login-token-ABCDEF");
        Assert.Equal("companion-login-token-ABCDEF", e.Servers.GetCompanionToken());
        Assert.Equal("companion-login-token-ABCDEF", e.Secrets.Get(SecretNames.RustPlusCompanionToken));
        var raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(e.Db.ConnectionString.Split('=')[1].Split(';')[0]));
        Assert.DoesNotContain("companion-login-token-ABCDEF", raw);
        e.Servers.SetCompanionToken(null); Assert.Null(e.Servers.GetCompanionToken());
    }
}
