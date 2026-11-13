using Microsoft.Data.Sqlite;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using Xunit;

namespace RustCompanion.Tests;

public class DataTests
{
    [Fact]
    public async Task Migration_Idempotent_And_TimerRoundTrip()
    {
        var path = TempDb.NewPath();
        var db = new Database(path);
        Assert.False(db.Initialize()); Assert.False(new Database(path).Initialize());
        var repo = new SqliteTimerRepository(db);
        var when = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(3));
        var id = await repo.InsertAsync(new TimerEntry { Kind = TrackerKind.Bradley, Name = "B", IntervalSeconds = 600, LeadSeconds = 60, StartedAtUtc = when });
        var list = await repo.ListAsync(TrackerKind.Bradley);
        Assert.Single(list); Assert.Equal(when.ToUniversalTime(), list[0].StartedAtUtc!.Value.ToUniversalTime());
        Assert.Empty(await repo.ListAsync(TrackerKind.DeepSea));
        await repo.AddHistoryAsync(id, when, "start", "");
        Assert.Single(await repo.HistoryAsync(id, 5));
        await repo.DeleteAsync(id);
        Assert.Empty(await repo.ListAsync(null));
    }

    [Fact]
    public void CorruptDb_IsMovedAside_AndRecreated()
    {
        var path = TempDb.NewPath();
        File.WriteAllText(path, new string('x', 4096));
        Assert.True(new Database(path).Initialize());
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
    }

    [Fact]
    public async Task Servers_Validation_Duplicates_Observations()
    {
        var db = new Database(TempDb.NewPath()); db.Initialize();
        var repo = new ServerRepository(db);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AddAsync("n", "http://x", 28016));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AddAsync("n", "1.2.3.4", 0));
        var id = await repo.AddAsync("S", "1.2.3.4", 28016);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddAsync("S2", "1.2.3.4", 28016));
        await repo.AddObservationAsync(id, DateTimeOffset.UtcNow, 10, 100);
        Assert.Single(await repo.RecentObservationsAsync(id, 5));
        await repo.SetFavoriteAsync(id, true);
        Assert.True((await repo.ListAsync())[0].Favorite);
    }

    [Fact]
    public async Task Players_And_Settings()
    {
        var db = new Database(TempDb.NewPath()); db.Initialize();
        var pr = new PlayerRepository(db);
        await Assert.ThrowsAsync<ArgumentException>(() => pr.AddAsync("123", ""));
        await pr.AddAsync("76561198000000000", "friend");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pr.AddAsync("76561198000000000", "again"));

        var sr = new SettingsRepository(db);
        Assert.Equal(60, (await sr.LoadAsync()).ServerPollSeconds);
        await sr.SaveAsync(new AppSettings { ServerPollSeconds = 90 });
        Assert.Equal(90, (await sr.LoadAsync()).ServerPollSeconds);
    }
}
