using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

internal sealed class CapturingOverlay : IOverlaySink
{
    public List<(string EventId, string Text)> Shown = new();
    public void Show(string eventId, string text) => Shown.Add((eventId, text));
}

internal sealed class RecordingGameNotifier : IGameNotifier
{
    public List<GameNotification> Sent = new();
    public Task<GameNotifyResult> NotifyAsync(GameNotification n, CancellationToken ct = default)
    {
        Sent.Add(n);
        var ok = new DeliveryOutcome(true, true, "ok");
        return Task.FromResult(new GameNotifyResult(NotifyChannel.Overlay, DeliveryOutcome.NotRequested, ok));
    }
}

public class MigrationMergeTests
{
    private static string Scalar(Microsoft.Data.Sqlite.SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }

    [Fact] public void FreshDb_HasMergedSchema_AndNoLegacyBmColumns()
    {
        var path = TempDb.NewPath(); new Database(path).Initialize();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c.Open();
        Assert.Equal("10", Scalar(c, "PRAGMA user_version"));
        foreach (var t in new[] { "rp_servers", "rp_devices", "rp_device_commands", "rp_device_pairing_requests", "known_servers", "player_names", "presence_history" })
            Assert.Equal("1", Scalar(c, $"SELECT COUNT(*) FROM sqlite_master WHERE name='{t}'"));
        Assert.Equal("0", Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='bm_id'"));
        Assert.Equal("1", Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='source_key'"));
        Assert.Equal("1", Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('watch_entries') WHERE name='identity_level'"));
    }

    [Fact] public void ReopeningDb_DoesNotRecreateBmId_AndKeepsData()
    {
        var path = TempDb.NewPath(); var db = new Database(path); db.Initialize();
        using (var c = db.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO servers(name,host,port,source_key) VALUES('S','1.1.1.1',28016,'1.1.1.1:28016')"; cmd.ExecuteNonQuery(); }
        new Database(path).Initialize();       // повторный запуск: ремонт схемы не должен вернуть bm_id и не должен терять данные
        using var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c2.Open();
        Assert.Equal("0", Scalar(c2, "SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='bm_id'"));
        Assert.Equal("1.1.1.1:28016", Scalar(c2, "SELECT source_key FROM servers"));
    }

    [Fact] public void DbCreatedByFixed11AtV7_IsRepairedInPlace_WithoutLosingData()
    {
        // имитация базы fixed11 (BattleMetrics): версия 7, столбцы bm_id / bm_player_id, нет таблиц v9
        var path = TempDb.NewPath(); var db = new Database(path); db.Initialize();
        using (var c = db.Open())
        {
            void X(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            X("DROP TABLE known_servers"); X("DROP TABLE player_names"); X("DROP TABLE rp_device_pairing_requests");
            X("ALTER TABLE rp_servers DROP COLUMN rustplus_id");
            X("ALTER TABLE rp_devices DROP COLUMN added_at"); X("ALTER TABLE rp_devices DROP COLUMN last_sync_at"); X("ALTER TABLE rp_devices DROP COLUMN last_is_on");
            X("ALTER TABLE watch_entries DROP COLUMN identity_level");
            X("ALTER TABLE servers RENAME COLUMN source_key TO bm_id");
            X("ALTER TABLE watch_entries RENAME COLUMN player_key TO bm_player_id");
            X("INSERT INTO servers(name,host,port,bm_id) VALUES('Old','2.2.2.2',28016,'12345')");
            X("INSERT INTO watch_entries(server_id,bm_player_id,name) VALUES(1,'999','Enemy')");
            X("PRAGMA user_version=7");
        }
        new Database(path).Initialize();
        using var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c2.Open();
        Assert.Equal("0", Scalar(c2, "SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='bm_id'"));
        Assert.Equal("2.2.2.2:28016", Scalar(c2, "SELECT source_key FROM servers"));         // числовой внешний ID заменён ключом «хост:порт»
        Assert.Equal("Enemy", Scalar(c2, "SELECT player_key FROM watch_entries"));           // ключ игрока — ник
        Assert.Equal("Enemy", Scalar(c2, "SELECT name FROM watch_entries"));                 // данные пользователя сохранены
        Assert.Equal("1", Scalar(c2, "SELECT COUNT(*) FROM sqlite_master WHERE name='known_servers'"));
    }

    [Fact] public async Task V9RustPlusDatabase_MigratesWithoutLosingServersOrDevices()
    {
        var path = TempDb.NewPath(); var db = new Database(path); db.Initialize();
        using (var c = db.Open())
        {
            void X(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            X("DROP TABLE rp_device_pairing_requests");
            X("ALTER TABLE rp_servers DROP COLUMN rustplus_id");
            X("ALTER TABLE rp_devices DROP COLUMN added_at"); X("ALTER TABLE rp_devices DROP COLUMN last_sync_at"); X("ALTER TABLE rp_devices DROP COLUMN last_is_on");
            X("INSERT INTO rp_servers(name,ip,port,player_id,paired_at) VALUES('Old Rust','1.2.3.4',28082,'76561198000000000','2026-01-02T03:04:05.0000000+00:00')");
            X("INSERT INTO rp_devices(server_id,entity_id,kind,name,alarm_notify) VALUES(1,'55',2,'Legacy Alarm',1)");
            X("PRAGMA user_version=9");
        }

        new Database(path).Initialize();
        var secrets = new MemorySecrets(); var servers = new PairedServerRepository(new Database(path), secrets);
        var server = Assert.Single(await servers.ListAsync());
        var device = Assert.Single(await new RpDeviceRepository(new Database(path)).ListAsync(server.Id));
        Assert.Equal(("Old Rust", "Legacy Alarm", 55UL, true), (server.Name, device.Name, device.EntityId, device.AlarmNotify));
        Assert.Null(device.AddedAtUtc); Assert.Null(device.LastSyncUtc); Assert.Null(device.LastIsOn);
        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); verify.Open();
        Assert.Equal("10", Scalar(verify, "PRAGMA user_version"));
        Assert.Equal("1", Scalar(verify, "SELECT COUNT(*) FROM pragma_table_info('rp_servers') WHERE name='rustplus_id'"));
    }
}

public class FcmRoutingTests
{
    private static IReadOnlyDictionary<string, string?> P(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact] public void Expand_RaisesFieldsFromJsonBody_WithoutOverwritingTopLevel()
    {
        var d = FcmMapper.Expand(P(("Body", "{\"ip\":\"1.2.3.4\",\"port\":\"28082\",\"entityId\":55,\"entityType\":2,\"entityName\":\"Base Alarm H1\",\"type\":\"entity\"}"), ("Title", "T")));
        var ev = FcmMapper.Map("Notification", d);
        Assert.Equal((FcmEventKind.DevicePairing, 55UL, RpDeviceKind.SmartAlarm, "Base Alarm H1", "1.2.3.4", 28082), (ev.Kind, ev.EntityId, ev.DeviceKind, ev.Name, ev.Ip, ev.Port));
    }

    [Fact] public void AlarmPush_WithoutEntityId_IsStillAlarm_ByName()
    {
        var ev = FcmMapper.Map("Notification", FcmMapper.Expand(P(("Body", "{\"type\":\"alarm\",\"ip\":\"1.2.3.4\",\"port\":28082}"), ("Title", "Base Alarm H1"))));
        Assert.Equal(FcmEventKind.AlarmTriggered, ev.Kind); Assert.Null(ev.EntityId); Assert.Equal("Base Alarm H1", ev.Name);
    }

    [Fact] public void Resolver_PrefersAddressFromPush_OverConnectedOrSelectedServer()
    {
        var a = new PairedServer(1, "A", "1.1.1.1", 28082, 1, DateTimeOffset.UtcNow, null);
        var b = new PairedServer(2, "B", "2.2.2.2", 28082, 1, DateTimeOffset.UtcNow, null);
        Assert.Equal(2, FcmServerResolver.Resolve(new[] { a, b }, "2.2.2.2", 28082, a).Server!.Id);
        Assert.Null(FcmServerResolver.Resolve(new[] { a, b }, "9.9.9.9", 1, a).Server);                 // чужой сервер не приписывается подключённому
        Assert.Null(FcmServerResolver.Resolve(new[] { a, b }, null, null, null).Server);                  // неоднозначно — не угадываем
        Assert.Equal(1, FcmServerResolver.Resolve(new[] { a }, null, null, null).Server!.Id);
    }

    [Fact] public async Task DevicePairing_CreatesPendingCardOnServerFromPush_AndHubIsNotified()
    {
        var e = new RpEnv(); await e.PairAsync("A");
        var b = await e.Servers.UpsertAsync("B", "5.6.7.8", 28082, 76561198000000000UL, 1, e.Clock.UtcNow);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock); await hub.ReloadServersAsync();
        var events = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock);
        var pairings = new RpDevicePairingRepository(e.Db);
        var router = new FcmEventRouter(e.Servers, e.Devices, pairings, events, e.Manager, hub, e.Clock);
        var notified = 0; hub.PairingRequestsChanged += () => notified++;

        var r = await router.HandleAsync("OnSmartAlarmPairing", P(("EntityId", "321"), ("Name", "Base Alarm H1"), ("Ip", "5.6.7.8"), ("Port", "28082")));

        Assert.False(r.DeviceAdded);                                                       // FCM сам по себе не подтверждает доступ к устройству
        Assert.Empty(await e.Devices.ListAsync(e.Server.Id));                              // на другой сервер не попало
        Assert.Empty(await e.Devices.ListAsync(b.Id));
        var request = Assert.Single(await pairings.ListPendingAsync(b.Id));
        Assert.Equal(("Base Alarm H1", RpDeviceKind.SmartAlarm, 321UL), (request.Name, request.Kind, request.EntityId));
        Assert.Equal(1, notified);
        Assert.Equal(1, router.ReceivedCount);
    }

    [Fact] public async Task DevicePairing_ForUnknownServer_IsReportedNotGuessed()
    {
        var e = new RpEnv(); await e.PairAsync("A");
        await e.Servers.UpsertAsync("B", "5.6.7.8", 28082, 76561198000000000UL, 1, e.Clock.UtcNow);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var router = new FcmEventRouter(e.Servers, e.Devices, new RpDevicePairingRepository(e.Db), new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock), e.Manager, hub, e.Clock);
        var r = await router.HandleAsync("OnSmartSwitchPairing", P(("EntityId", "1"), ("Ip", "9.9.9.9"), ("Port", "1")));
        Assert.False(r.DeviceAdded); Assert.Contains("не сопряжён", r.Description);
    }

    [Fact] public async Task Hub_SelectionIsShared_AndConnectingSelectsServerOnce()
    {
        var e = new RpEnv(); await e.PairAsync("A");
        var b = await e.Servers.UpsertAsync("B", "5.6.7.8", 28082, 76561198000000000UL, 1, e.Clock.UtcNow);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock); await hub.ReloadServersAsync();
        var seen = new List<long?>(); hub.SelectedChanged += p => seen.Add(p?.Id);
        Assert.True(hub.Select(b.Id)); Assert.Equal(b.Id, hub.Selected!.Id);
        Assert.False(hub.Select(999));
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        Assert.Equal(e.Server.Id, hub.Selected!.Id);             // подключение выбирает сервер
        hub.Select(b.Id);                                         // пользователь выбрал другой — периодические обновления выбор не отбирают
        await Task.Delay(50);
        Assert.Equal(b.Id, hub.Selected!.Id);
        await e.Manager.DisconnectAsync();
    }
}

public class AlarmNotificationTests
{
    [Fact] public async Task BellOn_SendsExactTextWithDeviceAndServer_BellOff_OnlyHistory()
    {
        var e = new RpEnv(); await e.PairAsync("Rust EU Main");
        var game = new RecordingGameNotifier(); var ev = new RustPlusDeviceEvents(e.Devices, e.Notifier, e.Clock, game);
        var id = await e.Devices.AddAsync(e.Server.Id, 777, RpDeviceKind.SmartAlarm, "Base Alarm H1");
        var change = new RpDeviceChange(777, new RpDeviceState(RpDeviceKind.SmartAlarm, true), e.Clock.UtcNow);
        await e.Devices.SetAlarmNotifyAsync(id, false);

        Assert.False(await ev.HandleAsync(e.Server, change));                       // колокольчик выключен
        Assert.Empty(game.Sent);
        Assert.Single(await e.Devices.EventsAsync(id, 5));

        await e.Devices.SetAlarmNotifyAsync(id, true);
        e.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await ev.HandleAsync(e.Server, change with { AtUtc = e.Clock.UtcNow }));
        var n = Assert.Single(game.Sent);
        Assert.Equal(GameNotifyKind.Alarm, n.Kind); Assert.Equal(e.Server.Id, n.RustPlusServerId);
        Assert.Equal("Base Alarm H1\n\nСРАБОТАЛА СИГНАЛИЗАЦИЯ!", n.OverlayText);
        Assert.Equal("Base Alarm H1: СРАБОТАЛА СИГНАЛИЗАЦИЯ!", n.ChatText); Assert.DoesNotContain("Rust EU Main", n.OverlayText + n.ChatText);

        // тот же сигнал, пришедший по websocket и по FCM, — одно уведомление
        Assert.False(await ev.HandleAsync(e.Server, change with { AtUtc = e.Clock.UtcNow.AddSeconds(2) }));
        Assert.Single(game.Sent);
    }
}

public class GameNotifierTests
{
    private static (GameNotifier N, RpEnv E, CapturingOverlay O, GameNotifyLog L) Make(NotifyChannel ch, Func<string, string, bool>? same = null)
    {
        var e = new RpEnv();
        e.Settings.Update(new AppSettings { RustPlusPollSeconds = 5, OverlayEnabled = true, AlarmNotifyChannel = (int)ch, PlayerExitNotifyChannel = (int)ch });
        var o = new CapturingOverlay(); var l = new GameNotifyLog();
        return (new GameNotifier(e.Settings, e.Manager, o, e.Clock, l, null, same ?? ((a, b) => a == b)), e, o, l);
    }

    [Fact] public async Task Off_SendsNothing()
    {
        var (n, _, o, l) = Make(NotifyChannel.Off);
        var r = await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "c", "o"));
        Assert.False(r.Delivered); Assert.Empty(o.Shown); Assert.Single(l.Items);
    }

    [Fact] public async Task Overlay_Shows_AndRespectsOverlaySwitch()
    {
        var (n, e, o, _) = Make(NotifyChannel.Overlay);
        Assert.True((await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "c", "o"))).Delivered); Assert.Single(o.Shown);
        e.Settings.Update(new AppSettings { OverlayEnabled = false, AlarmNotifyChannel = 2 });
        var r = await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "c", "o"));
        Assert.False(r.Delivered); Assert.Contains("выключен", r.Overlay.Detail);
    }

    [Fact] public async Task TeamChat_WithoutConnection_ReportsRealFailure()
    {
        var (n, e, _, _) = Make(NotifyChannel.TeamChat); await e.PairAsync();
        var r = await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "c", "o", e.Server.Id));
        Assert.False(r.Delivered); Assert.Contains("нет активного соединения", r.Chat.Detail);
    }

    [Fact] public async Task TeamChat_SendsOnlyToMatchingServer_AndBothDeliversTwice()
    {
        var (n, e, o, _) = Make(NotifyChannel.Both); await e.PairAsync("A");
        var other = await e.Servers.UpsertAsync("B", "5.6.7.8", 28082, 76561198000000000UL, 1, e.Clock.UtcNow);
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));

        var wrong = await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "wrong", "o", other.Id));
        Assert.False(wrong.Chat.Success); Assert.Empty(e.Created[0].Sent.Where(t => t == "wrong")); Assert.True(wrong.Overlay.Success);

        var ok = await n.NotifyAsync(new GameNotification(GameNotifyKind.Alarm, "right", "o", e.Server.Id));
        Assert.True(ok.Chat.Success && ok.Overlay.Success); Assert.Contains("right", e.Created[0].Sent);

        var byHost = await n.NotifyAsync(new GameNotification(GameNotifyKind.PlayerExit, "exit", "o", null, "1.2.3.4"));       // Ip сопряжённого сервера
        Assert.True(byHost.Chat.Success);
        var otherHost = await n.NotifyAsync(new GameNotification(GameNotifyKind.PlayerExit, "exit2", "o", null, "8.8.8.8"));
        Assert.False(otherHost.Chat.Success);
        await e.Manager.DisconnectAsync();
    }
}

public class DeviceAvailabilityTests
{
    [Fact] public void Tracker_ThreeFailures_OneUnavailable_ThenSingleRestore()
    {
        var t = new DeviceAvailabilityTracker(3);
        Assert.Equal(AvailabilityTransition.None, t.Record(1, 5, false));
        Assert.Equal(AvailabilityTransition.None, t.Record(1, 5, false));
        Assert.Equal(AvailabilityTransition.BecameUnavailable, t.Record(1, 5, false));
        Assert.Equal(AvailabilityTransition.None, t.Record(1, 5, false));            // повторного уведомления нет
        Assert.Equal(AvailabilityTransition.Restored, t.Record(1, 5, true));
        Assert.Equal(AvailabilityTransition.None, t.Record(1, 5, true));
    }

    [Fact] public void Tracker_SuccessInTheMiddle_ResetsCounter()
    {
        var t = new DeviceAvailabilityTracker(3);
        t.Record(1, 5, false); t.Record(1, 5, false); t.Record(1, 5, true);
        Assert.Equal(AvailabilityTransition.None, t.Record(1, 5, false));
        Assert.False(t.IsUnavailable(1, 5));
    }

    private static async Task<(RpEnv E, FakeGateway G, RecordingGameNotifier N, DeviceAvailabilityService S, RustPlusHub H)> SetupAsync()
    {
        var e = new RpEnv(); await e.PairAsync("Rust EU Main");
        await e.Devices.AddAsync(e.Server.Id, 10, RpDeviceKind.SmartSwitch, "Свет");
        await e.Devices.AddAsync(e.Server.Id, 11, RpDeviceKind.SmartAlarm, "Base Alarm H1");
        await e.Devices.AddAsync(e.Server.Id, 12, RpDeviceKind.StorageMonitor, "Ящик");          // не контролируется
        await e.Manager.ConnectAsync(e.Server.Id);
        Assert.True(await RpEnv.WaitFor(() => e.Manager.State == RustPlusState.Connected));
        var n = new RecordingGameNotifier(); var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var s = new DeviceAvailabilityService(e.Manager, e.Devices, hub, n, e.Settings, e.Clock);
        return (e, e.Created[0], n, s, hub);
    }

    [Fact] public async Task DeviceSilent_ThreeProbes_Unavailable_ThenRestored_WithSeparateNotifications()
    {
        var (e, g, n, s, hub) = await SetupAsync();
        g.DeviceOk = () => false;                                       // устройства молчат, сервер отвечает
        await s.ProbeOnceAsync(default); await s.ProbeOnceAsync(default);
        Assert.Empty(n.Sent);
        await s.ProbeOnceAsync(default);
        Assert.Equal(2, n.Sent.Count(x => x.Kind == GameNotifyKind.DeviceUnavailable));            // переключатель и сигнализация, по одному разу
        Assert.DoesNotContain(n.Sent, x => x.OverlayText.Contains("уничтожен"));                  // причину не утверждаем
        await s.ProbeOnceAsync(default);
        Assert.Equal(2, n.Sent.Count(x => x.Kind == GameNotifyKind.DeviceUnavailable));            // без повторов
        Assert.Equal(DeviceAvailability.Unavailable, hub.GetLive(e.Server.Id, 10)!.Availability);

        g.DeviceOk = () => true;
        await s.ProbeOnceAsync(default);
        Assert.Equal(2, n.Sent.Count(x => x.Kind == GameNotifyKind.DeviceRestored));
        Assert.Equal(DeviceAvailability.Available, hub.GetLive(e.Server.Id, 10)!.Availability);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task ServerLinkLoss_IsNotCountedAsDeviceUnavailability()
    {
        var (e, g, n, s, hub) = await SetupAsync();
        g.DeviceOk = () => false;
        g.Info = () => RpResult<RpServerInfo>.Fail("timeout", "сервер не отвечает");   // и сам сервер не отвечает
        for (var i = 0; i < 6; i++) await s.ProbeOnceAsync(default);
        Assert.Empty(n.Sent);
        Assert.NotEqual(DeviceAvailability.Unavailable, hub.GetLive(e.Server.Id, 10)?.Availability ?? DeviceAvailability.Unknown);
        await e.Manager.DisconnectAsync();
    }

    [Fact] public async Task NoConnection_NoProbing_NoNotifications()
    {
        var e = new RpEnv(); await e.PairAsync(); await e.Devices.AddAsync(e.Server.Id, 10, RpDeviceKind.SmartSwitch, "Свет");
        var n = new RecordingGameNotifier(); var s = new DeviceAvailabilityService(e.Manager, e.Devices, new RustPlusHub(e.Servers, e.Manager, e.Clock), n, e.Settings, e.Clock);
        for (var i = 0; i < 5; i++) await s.ProbeOnceAsync(default);
        Assert.Empty(n.Sent);
    }
}

public class PlayerIdentityTests
{
    [Fact] public void Indicator_NicknameOnly_IsAlwaysGray()
    {
        foreach (var st in Enum.GetValues<PresenceStatus>())
            Assert.Equal(PlayerIndicator.Unknown, PlayerIndicatorRules.Compute(IdentityLevel.Unconfirmed, st, SteamServerMatch.NotChecked));
    }

    [Fact] public void Indicator_UserConfirmed_GreenOnServer_RedOnlyAfterConfirmedExit()
    {
        Assert.Equal(PlayerIndicator.Present, PlayerIndicatorRules.Compute(IdentityLevel.UserConfirmed, PresenceStatus.OnServer, SteamServerMatch.NotChecked));
        Assert.Equal(PlayerIndicator.Unknown, PlayerIndicatorRules.Compute(IdentityLevel.UserConfirmed, PresenceStatus.NotDetected, SteamServerMatch.NotChecked));
        Assert.Equal(PlayerIndicator.Absent, PlayerIndicatorRules.Compute(IdentityLevel.UserConfirmed, PresenceStatus.ExitConfirmed, SteamServerMatch.NotChecked));
        Assert.Equal(PlayerIndicator.Unknown, PlayerIndicatorRules.Compute(IdentityLevel.UserConfirmed, PresenceStatus.CheckError, SteamServerMatch.NotChecked));
    }

    [Fact] public void SteamMatch_ComparesAddressStrictly()
    {
        SteamPlayerStatus S(string? srv) => new("7656119800000000" + "0", "n", 1, true, true, srv, DateTimeOffset.UtcNow);
        Assert.Equal(SteamServerMatch.OnThisServer, SteamMatch.Evaluate(S("1.2.3.4:28016"), "1.2.3.4", 28016));
        Assert.Equal(SteamServerMatch.OnOtherServer, SteamMatch.Evaluate(S("5.5.5.5:28016"), "1.2.3.4", 28016));
        Assert.Equal(SteamServerMatch.Inconclusive, SteamMatch.Evaluate(S("1.2.3.4:28015"), "1.2.3.4", 28016));   // тот же IP, другой порт — не гадаем
        Assert.Equal(SteamServerMatch.Inconclusive, SteamMatch.Evaluate(S(null), "1.2.3.4", 28016));            // отсутствие данных не доказывает отсутствие
        Assert.Equal(SteamServerMatch.NotChecked, SteamMatch.Evaluate(null, "1.2.3.4", 28016));
        Assert.Equal(PlayerIndicator.Absent, PlayerIndicatorRules.Compute(IdentityLevel.SteamConfirmed, PresenceStatus.OnServer, SteamServerMatch.OnOtherServer));
        Assert.Equal(IdentityLevel.SteamConfirmed, SteamMatch.Effective(IdentityLevel.UserConfirmed, S("1.2.3.4:28016")));
        Assert.Equal(IdentityLevel.Unconfirmed, SteamMatch.Effective(IdentityLevel.Unconfirmed, S("1.2.3.4:28016")));   // уровни не смешиваются
    }

    [Fact] public async Task Exit_Notifies_ViaGameChannel_WithServerHost_AndFlagsUnconfirmed_JoinDoesNot()
    {
        var e = new Env();
        e.ServerId = await e.Repo.AddTrackedServerAsync("S", "1.1.1.1", 28016, "1.1.1.1:28016");     // ключ сервера — «хост:порт запросов», как в рабочей базе
        e.WatchId = await e.Repo.AddWatchAsync(e.ServerId, "Enemy", null, "Enemy");
        var game = new RecordingGameNotifier();
        var mon = new MonitoringService(e.Db, e.Repo, e.Bm, new NotificationDispatcher(e.Windows, Array.Empty<INotificationChannel>(), e.Settings, e.Clock), e.Settings, e.Clock, null, null, game);
        e.Bm.Next = () => Env.P(PresenceKind.Present); await mon.RunCycleAsync(default);
        Assert.Empty(game.Sent);                                                  // вход не уведомляется
        e.Bm.Next = () => Env.P(PresenceKind.Absent); for (var i = 0; i < 3; i++) await mon.RunCycleAsync(default);
        var n = Assert.Single(game.Sent);
        Assert.Equal(GameNotifyKind.PlayerExit, n.Kind); Assert.Equal("1.1.1.1", n.ServerHost);
        Assert.StartsWith("Enemy покинул S", n.ChatText); Assert.Contains("не подтверждена", n.OverlayText);
        e.Bm.Next = () => Env.P(PresenceKind.Present); await mon.RunCycleAsync(default);
        for (var i = 0; i < 3; i++) await mon.RunCycleAsync(default);
        Assert.Single(game.Sent);                                                 // возвращение — тоже без уведомления; повторов нет
        Assert.Equal(PlayerIndicator.Unknown, mon.IndicatorFor(e.WatchId).Indicator);
    }

    [Fact] public async Task LinkedPlayer_GetsGreenIndicator_OnServer()
    {
        var e = new Env(); await e.SeedAsync();
        await e.Repo.SetIdentityAsync(e.WatchId, "76561198000000000");
        var mon = new MonitoringService(e.Db, e.Repo, e.Bm, new NotificationDispatcher(e.Windows, Array.Empty<INotificationChannel>(), e.Settings, e.Clock), e.Settings, e.Clock);
        e.Bm.Next = () => Env.P(PresenceKind.Present); await mon.RunCycleAsync(default);
        var i = mon.IndicatorFor(e.WatchId);
        Assert.Equal((PlayerIndicator.Present, IdentityLevel.UserConfirmed), (i.Indicator, i.Level));
    }
}

public class PlayerDirectoryTests
{
    [Fact] public async Task SearchAcrossServers_FindsNickOnEveryKnownServer_CaseInsensitive()
    {
        var db = new Database(TempDb.NewPath()); db.Initialize();
        var dir = new PlayerDirectoryRepository(db); var hist = new PresenceHistoryRepository(db); var t = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await dir.UpsertKnownServerAsync("1.1.1.1:28016", "Alpha", t); await dir.UpsertKnownServerAsync("2.2.2.2:28016", "Beta", t);
        await hist.RecordSnapshotAsync("1.1.1.1:28016", new[] { ("Player123", (double?)null) }, t, TimeSpan.FromMinutes(4));
        await hist.RecordSnapshotAsync("2.2.2.2:28016", new[] { ("player123", (double?)null), ("Other", (double?)null) }, t.AddHours(1), TimeSpan.FromMinutes(4));
        var found = await dir.SearchByNameAsync(new[] { "PLAYER123" });
        Assert.Equal(new[] { "Beta", "Alpha" }, found.Select(f => f.ServerName).ToArray());
        Assert.Equal(2, (await dir.VisitedServersAsync("Player123")).Count);
    }

    [Fact] public async Task NameHistory_KeepsEveryNickPerSteamId()
    {
        var db = new Database(TempDb.NewPath()); db.Initialize(); var dir = new PlayerDirectoryRepository(db); var t = DateTimeOffset.UtcNow;
        await dir.RecordNameAsync("76561198000000000", "Old", t); await dir.RecordNameAsync("76561198000000000", "New", t.AddDays(1)); await dir.RecordNameAsync("76561198000000000", "Old", t.AddDays(2));
        var names = await dir.NamesAsync("76561198000000000");
        Assert.Equal(2, names.Count); Assert.Equal("Old", names[0].Name);
    }

    [Fact] public async Task Crawler_PollsDueServersOnly_AndRespectsIntervalAndBatchLimit()
    {
        var e = new Env(); var dir = new PlayerDirectoryRepository(e.Db); var hist = new PresenceHistoryRepository(e.Db);
        e.Settings.Update(new AppSettings { CrawlerEnabled = true, CrawlerPollSeconds = 120, CrawlerMaxServersPerCycle = 2, NotifyMinIntervalSeconds = 0 });
        for (var i = 1; i <= 3; i++) await dir.UpsertKnownServerAsync($"9.9.9.{i}:28016", $"S{i}", e.Clock.UtcNow);
        var calls = new List<string>();
        var src = new CountingSource(calls);
        var crawler = new ServerCrawler(dir, e.Repo, src, hist, e.Settings, e.Clock);
        await crawler.RunCycleAsync(default); Assert.Equal(2, calls.Count);                         // лимит за цикл
        await crawler.RunCycleAsync(default); Assert.Equal(3, calls.Count);                         // остальные — следующим циклом, опрошенные повторно не опрашиваются
        await crawler.RunCycleAsync(default); Assert.Equal(3, calls.Count);                         // интервал ещё не прошёл
        e.Clock.Advance(TimeSpan.FromSeconds(130)); await crawler.RunCycleAsync(default); Assert.Equal(5, calls.Count);
    }

    private sealed class CountingSource : IServerQuerySource
    {
        private readonly List<string> _calls; public CountingSource(List<string> calls) { _calls = calls; }
        public Task<SourceResult<IReadOnlyList<FoundServer>>> SearchServersAsync(string q, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceResult<IReadOnlyList<FoundPlayer>>> SearchPlayersAsync(string q, string? k, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceResult<PresenceObservation>> CheckPresenceAsync(string p, string k, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceResult<IReadOnlyList<PresenceSession>>> GetSessionsAsync(string p, string? k, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceResult<ServerSnapshot>> GetServerPlayersAsync(string key, CancellationToken ct)
        {
            _calls.Add(key);
            var snap = new ServerSnapshot(new FoundServer(key, key, "h", null, 1, 100, null), new[] { new OnlinePlayer("a", "a") }, true);
            return Task.FromResult(SourceResult<ServerSnapshot>.Ok(snap));
        }
    }
}
