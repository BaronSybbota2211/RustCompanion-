using RustCompanion.Core.Data;
using RustCompanion.Core.RustPlus;
using Xunit;

namespace RustCompanion.Tests;

public class RustPlusPairingTests
{
    private static IReadOnlyDictionary<string, string?> P(params (string, string?)[] values) =>
        values.ToDictionary(x => x.Item1, x => x.Item2);

    private static FcmEvent Event(ulong entityId, RpDeviceKind kind, string persistentId, string name = "Device") =>
        new(FcmEventKind.DevicePairing, entityId, kind, name, null, null,
            PersistentId: persistentId, ServerUid: "server-guid");

    private static RustPlusDevicePairingService Service(RpEnv e, RpDevicePairingRepository requests, RustPlusHub hub,
        Func<FakeGateway>? factory = null) => new(factory ?? (() => e.Next!()), e.Manager, e.Servers, e.Devices, requests,
        new RpDeviceCommandRepository(e.Db), hub, e.Clock);

    [Fact]
    public void EntityPairing_RealNotificationPayload_IsMappedWithoutInventedFields()
    {
        var ev = FcmMapper.Map("OnEntityPairing", P(
            ("PlayerId", "76561198000000000"), ("PlayerToken", "123456"),
            ("ServerId", "7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40"), ("PersistentId", "fcm-message-1"),
            ("EntityId", "321"), ("EntityType", "Switch"), ("EntityName", "Main lights")));

        Assert.Equal(FcmEventKind.DevicePairing, ev.Kind);
        Assert.Equal(321UL, ev.EntityId);
        Assert.Equal(RpDeviceKind.SmartSwitch, ev.DeviceKind);
        Assert.Equal("Main lights", ev.Name);
        Assert.Equal("7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40", ev.ServerUid);
        Assert.Equal("fcm-message-1", ev.PersistentId);
        Assert.Equal(76561198000000000UL, ev.PlayerId);
    }

    [Theory]
    [InlineData("OnSmartAlarmPairing", RpDeviceKind.SmartAlarm)]
    [InlineData("OnSmartSwitchPairing", RpDeviceKind.SmartSwitch)]
    public void SpecializedPairing_EntityIdComesFromNotificationData(string eventName, RpDeviceKind expected)
    {
        var ev = FcmMapper.Map(eventName, P(("Data", "654"), ("ServerId", "server-guid"), ("PersistentId", "fcm-message-2")));
        Assert.Equal(FcmEventKind.DevicePairing, ev.Kind);
        Assert.Equal(654UL, ev.EntityId);
        Assert.Equal(expected, ev.DeviceKind);
        Assert.Equal("server-guid", ev.ServerUid);
    }

    [Fact]
    public void Resolver_UsesExactServerGuidAndPlayerId_NeverSelectedServer()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new PairedServer(1, "A", "1.1.1.1", 28082, 11, now, null, "guid-a");
        var b = new PairedServer(2, "B", "2.2.2.2", 28082, 22, now, null, "guid-b");

        Assert.Equal(2, FcmServerResolver.Resolve(new[] { a, b }, "9.9.9.9", 1, a, "guid-b", 22).Server!.Id);
        Assert.Null(FcmServerResolver.Resolve(new[] { a, b }, null, null, a, "guid-b", 11).Server);
        Assert.Null(FcmServerResolver.Resolve(new[] { a, b }, null, null, a, "unknown", 22).Server);
        Assert.Null(FcmServerResolver.Resolve(new[] { a, b }, null, null, a).Server);
    }

    [Fact]
    public async Task PendingCard_DeduplicatesPersistsAndDoesNotReopenForSameHandledMessage()
    {
        var e = new RpEnv(); await e.PairAsync("A");
        var server = await e.Servers.ListAsync();
        var requests = new RpDevicePairingRepository(e.Db);
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);

        var first = await requests.UpsertPendingAsync(server[0], Event(55, RpDeviceKind.SmartSwitch, "fcm-1"), e.Clock.UtcNow);
        var duplicate = await requests.UpsertPendingAsync(server[0], Event(55, RpDeviceKind.SmartSwitch, "fcm-1"), e.Clock.UtcNow.AddSeconds(1));
        Assert.NotNull(first); Assert.NotNull(duplicate); Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await new RpDevicePairingRepository(e.Db).ListPendingAsync(server[0].Id));

        var service = Service(e, requests, hub);
        Assert.True((await service.DismissAsync(first.Id)).Ok);
        Assert.Empty(await requests.ListPendingAsync(server[0].Id));
        Assert.Null(await requests.UpsertPendingAsync(server[0], Event(55, RpDeviceKind.SmartSwitch, "fcm-1"), e.Clock.UtcNow.AddSeconds(2)));
        Assert.Empty(await requests.ListPendingAsync(server[0].Id));

        var newMessage = await requests.UpsertPendingAsync(server[0], Event(55, RpDeviceKind.SmartSwitch, "fcm-2"), e.Clock.UtcNow.AddSeconds(3));
        Assert.NotNull(newMessage); Assert.NotEqual(first.Id, newMessage.Id);
        Assert.Single(await requests.ListPendingAsync(server[0].Id));
    }

    [Fact]
    public async Task FollowupSpecializedEvent_KeepsDiscoveredNameAndSingleCard()
    {
        var e = new RpEnv(); await e.PairAsync();
        var server = (await e.Servers.ListAsync())[0];
        var requests = new RpDevicePairingRepository(e.Db);
        var full = new FcmEvent(FcmEventKind.DevicePairing, 88, RpDeviceKind.SmartAlarm, "Base Alarm H1", null, null, PersistentId: "fcm-full");
        var followup = new FcmEvent(FcmEventKind.DevicePairing, 88, RpDeviceKind.SmartAlarm, null, null, null, PersistentId: "fcm-followup");

        await requests.UpsertPendingAsync(server, full, e.Clock.UtcNow);
        var result = await requests.UpsertPendingAsync(server, followup, e.Clock.UtcNow.AddSeconds(1));
        Assert.Equal("Base Alarm H1", result!.Name);
        Assert.Single(await requests.ListPendingAsync(server.Id));
    }

    [Fact]
    public async Task FailedVerification_DoesNotSaveDeviceAndLeavesActionableCard()
    {
        var e = new RpEnv(); await e.PairAsync();
        var server = (await e.Servers.ListAsync())[0];
        var requests = new RpDevicePairingRepository(e.Db);
        var request = (await requests.UpsertPendingAsync(server, Event(99, RpDeviceKind.SmartSwitch, "fcm-fail"), e.Clock.UtcNow))!;
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        FakeGateway? gateway = null;
        var service = Service(e, requests, hub, () => gateway = new FakeGateway
        {
            DeviceRead = (_, _) => RpResult<RpDeviceState>.Fail("not_found", "entity unavailable")
        });

        var result = await service.VerifyAndAddAsync(request.Id);
        Assert.False(result.Ok); Assert.Equal("not_found", result.Code);
        Assert.Empty(await e.Devices.ListAsync(server.Id));
        var pending = Assert.Single(await requests.ListPendingAsync(server.Id));
        Assert.Contains("не подтверждено", pending.LastError);
        Assert.True(gateway!.Disposed);
        Assert.Null(hub.GetLive(server.Id, request.EntityId));
    }

    [Fact]
    public async Task SuccessfulVerification_SavesDeviceDefaultsAndPublishesSharedState()
    {
        var e = new RpEnv(); await e.PairAsync();
        var server = (await e.Servers.ListAsync())[0];
        var requests = new RpDevicePairingRepository(e.Db);
        var request = (await requests.UpsertPendingAsync(server, Event(101, RpDeviceKind.SmartSwitch, "fcm-ok", "Main Switch"), e.Clock.UtcNow))!;
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var devicesChanged = 0; var statesChanged = 0; var pairingsChanged = 0;
        hub.DevicesChanged += id => { if (id == server.Id) devicesChanged++; };
        hub.DeviceStateChanged += (id, entity) => { if (id == server.Id && entity == request.EntityId) statesChanged++; };
        hub.PairingRequestsChanged += () => pairingsChanged++;
        var service = Service(e, requests, hub, () => new FakeGateway
        {
            DeviceRead = (_, _) => RpResult<RpDeviceState>.Success(new RpDeviceState(RpDeviceKind.SmartSwitch, false))
        });

        var result = await service.VerifyAndAddAsync(request.Id);
        Assert.True(result.Ok, result.Message);
        var device = Assert.Single(await e.Devices.ListAsync(server.Id));
        Assert.Equal(("Main Switch", 101UL, false), (device.Name, device.EntityId, device.LastIsOn));
        Assert.NotNull(device.AddedAtUtc); Assert.NotNull(device.LastSyncUtc);
        Assert.Single(await new RpDeviceCommandRepository(e.Db).ListAsync(server.Id));
        Assert.Empty(await requests.ListPendingAsync(server.Id));
        Assert.Equal(RpPairingRequestStatus.Added, (await requests.GetAsync(request.Id))!.Status);
        var live = hub.GetLive(server.Id, device.EntityId);
        Assert.Equal(DeviceAvailability.Available, live!.Availability);
        Assert.False(live.State!.IsOn);
        Assert.Equal(1, devicesChanged); Assert.Equal(2, statesChanged); Assert.Equal(1, pairingsChanged);
        Assert.Null(await requests.UpsertPendingAsync(server, Event(101, RpDeviceKind.SmartSwitch, "fcm-new"), e.Clock.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public async Task SmartAlarmVerification_DoesNotCreatePowerCommandSettings()
    {
        var e = new RpEnv(); await e.PairAsync();
        var server = (await e.Servers.ListAsync())[0];
        var requests = new RpDevicePairingRepository(e.Db);
        var request = (await requests.UpsertPendingAsync(server, Event(102, RpDeviceKind.SmartAlarm, "fcm-alarm", "Alarm"), e.Clock.UtcNow))!;
        var hub = new RustPlusHub(e.Servers, e.Manager, e.Clock);
        var service = Service(e, requests, hub, () => new FakeGateway
        {
            DeviceRead = (_, _) => RpResult<RpDeviceState>.Success(new RpDeviceState(RpDeviceKind.SmartAlarm, true))
        });

        Assert.True((await service.VerifyAndAddAsync(request.Id)).Ok);
        var device = Assert.Single(await e.Devices.ListAsync(server.Id));
        Assert.Equal(RpDeviceKind.SmartAlarm, device.Kind);
        Assert.Empty(await new RpDeviceCommandRepository(e.Db).ListAsync(server.Id));
        Assert.True(hub.GetLive(server.Id, device.EntityId)!.State!.IsOn);
    }
}
