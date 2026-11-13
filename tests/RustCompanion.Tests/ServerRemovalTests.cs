using RustCompanion.Core;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

/// <summary>Сервер общий для вкладок «Серверы» и «Наблюдение»: его удаление (с любой из вкладок) убирает наблюдаемых игроков, их состояние и историю.</summary>
public class ServerRemovalTests
{
    private static async Task SeedDataAsync(Env e)
    {
        await e.Repo.SaveStateAsync(new MonitorState { WatchId = e.WatchId, Status = PresenceStatus.OnServer });
        await e.Repo.AddActivityAsync(e.WatchId, DateTimeOffset.UtcNow, "join_confirmed", "");
    }

    private static async Task AssertEverythingGoneAsync(Env e)
    {
        Assert.Empty(await e.Servers.ListAsync());
        Assert.Empty(await e.Repo.ListTrackedServersAsync());
        Assert.Empty(await e.Repo.ListWatchAsync());
        Assert.Empty(await e.Repo.ListActivityAsync(null, null, null, 10));
        Assert.Equal(PresenceStatus.NoData, (await e.Repo.GetStateAsync(e.WatchId)).Status);
    }

    [Fact]
    public async Task RemoveOnWatchTab_DeletesServerAndWatchedPlayers()
    {
        var e = new Env(); await e.SeedAsync(); await SeedDataAsync(e);
        var removed = await e.Repo.RemoveTrackedServerAsync(e.ServerId);
        Assert.Equal(1, removed);
        await AssertEverythingGoneAsync(e);
    }

    [Fact]
    public async Task DeleteOnServersTab_DeletesWatchedPlayersToo()
    {
        var e = new Env(); await e.SeedAsync(); await SeedDataAsync(e);
        await e.Servers.DeleteAsync(e.ServerId);
        await AssertEverythingGoneAsync(e);
    }

    [Fact]
    public async Task OtherServersWatchedPlayers_AreKept()
    {
        var e = new Env(); await e.SeedAsync();
        var other = await e.Repo.AddTrackedServerAsync("Other", "2.2.2.2", 28016, "888");
        await e.Repo.AddWatchAsync(other, "456", null, "Friend");
        await e.Repo.RemoveTrackedServerAsync(e.ServerId);
        var left = await e.Repo.ListWatchAsync();
        Assert.Single(left); Assert.Equal("Friend", left[0].Name);
        Assert.Single(await e.Repo.ListTrackedServersAsync());
    }

    [Fact]
    public async Task GroupThresholdState_ForRemovedServer_IsCleared()
    {
        var e = new Env(); await e.SeedAsync();
        var g = await e.Repo.AddGroupAsync("Клан", "#D9653B");
        await e.Repo.SaveGroupStateAsync(g, e.ServerId, true, 3);
        await e.Repo.RemoveTrackedServerAsync(e.ServerId);
        Assert.Equal((false, 0), await e.Repo.GetGroupStateAsync(g, e.ServerId));
    }

    [Fact]
    public async Task MonitoringWrites_ForRemovedWatch_DoNotThrow()
    {
        var e = new Env(); await e.SeedAsync();
        await e.Repo.RemoveTrackedServerAsync(e.ServerId);      // сервер удалён, пока шёл цикл опроса
        await e.Repo.SaveStateAsync(new MonitorState { WatchId = e.WatchId, Status = PresenceStatus.OnServer });
        await e.Repo.AddActivityAsync(e.WatchId, DateTimeOffset.UtcNow, "exit_confirmed", "");
        Assert.Empty(await e.Repo.ListActivityAsync(null, null, null, 10));
    }
}
