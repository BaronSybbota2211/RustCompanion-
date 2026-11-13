using System.Net;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

internal sealed class FakeSource : IServerQuerySource
{
    public Queue<SourceResult<PresenceObservation>> Presence { get; } = new();
    public Func<SourceResult<PresenceObservation>>? Next;
    public Task<SourceResult<IReadOnlyList<FoundServer>>> SearchServersAsync(string q, CancellationToken ct) => Task.FromResult(SourceResult<IReadOnlyList<FoundServer>>.Ok(new List<FoundServer>()));
    public Task<SourceResult<IReadOnlyList<FoundPlayer>>> SearchPlayersAsync(string q, string? s, CancellationToken ct) => Task.FromResult(SourceResult<IReadOnlyList<FoundPlayer>>.Ok(new List<FoundPlayer>()));
    public Task<SourceResult<IReadOnlyList<PresenceSession>>> GetSessionsAsync(string p, string? s, CancellationToken ct) => Task.FromResult(SourceResult<IReadOnlyList<PresenceSession>>.Ok(new List<PresenceSession>()));
    public Task<SourceResult<ServerSnapshot>> GetServerPlayersAsync(string id, CancellationToken ct) => Task.FromResult(SourceResult<ServerSnapshot>.Fail(SourceState.NoData, "n/a"));
    public Task<SourceResult<PresenceObservation>> CheckPresenceAsync(string p, string s, CancellationToken ct) =>
        Task.FromResult(Next is not null ? Next() : Presence.Dequeue());
}

internal sealed class Env
{
    public Database Db; public WatchRepository Repo; public ServerRepository Servers; public FakeSource Bm = new(); public FakeClock Clock = new();
    public FakeNotifier Windows = new(); public SettingsProvider Settings = new(); public MonitoringService Mon; public long ServerId; public long WatchId;
    public string Path;

    public Env(string? path = null)
    {
        Path = path ?? TempDb.NewPath();
        Db = new Database(Path); Db.Initialize(); Repo = new WatchRepository(Db); Servers = new ServerRepository(Db);
        Settings.Update(new AppSettings { NotifyMinIntervalSeconds = 0 });
        Mon = Build();
    }
    public MonitoringService Build() => new(Db, Repo, Bm, new NotificationDispatcher(Windows, Array.Empty<INotificationChannel>(), Settings, Clock), Settings, Clock);
    public async Task SeedAsync()
    {
        ServerId = await Repo.AddTrackedServerAsync("S", "1.1.1.1", 28016, "999");
        WatchId = await Repo.AddWatchAsync(ServerId, "123", "76561198000000000", "Enemy");
    }
    public static SourceResult<PresenceObservation> P(PresenceKind k) => SourceResult<PresenceObservation>.Ok(new PresenceObservation(k, DateTimeOffset.UtcNow));
}

public class MonitoringServiceTests
{
    [Fact]
    public async Task ThreeAbsences_OneWindowsNotification_NoRepeat()
    {
        var e = new Env(); await e.SeedAsync();
        e.Bm.Presence.Enqueue(Env.P(PresenceKind.Present));
        for (var i = 0; i < 6; i++) e.Bm.Presence.Enqueue(Env.P(PresenceKind.Absent));
        for (var i = 0; i < 7; i++) { e.Clock.Advance(TimeSpan.FromMinutes(1)); await e.Mon.RunCycleAsync(default); }
        Assert.Single(e.Windows.Sent);
        Assert.Contains("покинул", e.Windows.Sent[0].Title);
        Assert.Contains("после 3 успешных проверок", e.Windows.Sent[0].Message); Assert.Contains("Последнее подтверждённое присутствие", e.Windows.Sent[0].Message);
    }

    [Fact]
    public async Task Restart_DoesNotDuplicateProcessedExit()
    {
        var e = new Env(); await e.SeedAsync();
        e.Bm.Presence.Enqueue(Env.P(PresenceKind.Present));
        for (var i = 0; i < 3; i++) e.Bm.Presence.Enqueue(Env.P(PresenceKind.Absent));
        for (var i = 0; i < 4; i++) await e.Mon.RunCycleAsync(default);
        Assert.Single(e.Windows.Sent);

        // «перезапуск»: новая БД-обёртка и новый сервис на том же файле
        var e2 = new Env(e.Path); e2.WatchId = e.WatchId;
        e2.Bm.Next = () => Env.P(PresenceKind.Absent);
        for (var i = 0; i < 5; i++) await e2.Mon.RunCycleAsync(default);
        Assert.Empty(e2.Windows.Sent);

        // даже если состояние потеряно, ключ события уже записан → дубликата нет
        await e2.Repo.SaveStateAsync(new MonitorState { WatchId = e.WatchId, Status = PresenceStatus.OnServer, LastPresentUtc = DateTimeOffset.UtcNow }, default);
        for (var i = 0; i < 3; i++) await e2.Mon.RunCycleAsync(default);
        Assert.Empty(e2.Windows.Sent);
    }

    [Fact]
    public async Task NetworkErrors_NeverTriggerExit()
    {
        var e = new Env(); await e.SeedAsync();
        e.Bm.Presence.Enqueue(Env.P(PresenceKind.Present));
        await e.Mon.RunCycleAsync(default);
        foreach (var st in new[] { SourceState.NoResponse, SourceState.AccessDenied, SourceState.RateLimited, SourceState.Error })
        { e.Bm.Next = () => SourceResult<PresenceObservation>.Fail(st, "x"); for (var i = 0; i < 4; i++) await e.Mon.RunCycleAsync(default); }
        Assert.Empty(e.Windows.Sent);
        var s = await e.Repo.GetStateAsync(e.WatchId);
        Assert.Equal(0, s.AbsenceCount); Assert.Equal(PresenceStatus.CheckError, s.Status);
    }

    [Fact]
    public async Task GroupThreshold_NoLongerNotifies()
    {
        var e = new Env(); await e.Seed2Async();
        e.Bm.Next = () => Env.P(PresenceKind.Present);
        for (var i = 0; i < 3; i++) await e.Mon.RunCycleAsync(default);
        Assert.Empty(e.Windows.Sent);   // уведомляется только выход наблюдаемого игрока
    }

    [Fact]
    public async Task Join_AfterConfirmedExit_DoesNotNotify_OnlyExitDoes()
    {
        var e = new Env(); await e.SeedAsync();
        e.Bm.Next = () => Env.P(PresenceKind.Present); await e.Mon.RunCycleAsync(default);
        e.Bm.Next = () => Env.P(PresenceKind.Absent); for (var i = 0; i < 3; i++) await e.Mon.RunCycleAsync(default);
        e.Bm.Next = () => Env.P(PresenceKind.Present); await e.Mon.RunCycleAsync(default);
        Assert.Single(e.Windows.Sent); Assert.Contains("покинул", e.Windows.Sent[0].Title);   // возвращение не уведомляется
    }

    [Fact]
    public async Task PerGroupAndPlayerToggles_SuppressNotifications()
    {
        var e = new Env(); await e.SeedAsync();
        var gid = await e.Repo.AddGroupAsync("Враги", "#FF0000");
        await e.Repo.UpdateGroupAsync(new WatchGroup { Id = gid, Name = "Враги", Color = "#FF0000", NotifyLeave = false });
        await e.Repo.UpdateWatchAsync(new WatchEntry { Id = e.WatchId, Name = "Enemy", GroupId = gid, NotifyJoin = true, NotifyLeave = true });
        e.Bm.Next = () => Env.P(PresenceKind.Present); await e.Mon.RunCycleAsync(default);
        e.Bm.Next = () => Env.P(PresenceKind.Absent); for (var i = 0; i < 4; i++) await e.Mon.RunCycleAsync(default);
        Assert.Empty(e.Windows.Sent);
    }
}

internal static class EnvExt
{
    public static async Task Seed2Async(this Env e)
    {
        e.ServerId = await e.Repo.AddTrackedServerAsync("S", "1.1.1.1", 28016, "999");
        var gid = await e.Repo.AddGroupAsync("Соседи", "#00FF00");
        await e.Repo.UpdateGroupAsync(new WatchGroup { Id = gid, Name = "Соседи", Color = "#00FF00", NotifyJoin = false, NotifyLeave = false, Threshold = 2 });
        await e.Repo.AddWatchAsync(e.ServerId, "1", null, "A", gid);
        await e.Repo.AddWatchAsync(e.ServerId, "2", null, "B", gid);
    }
}

public class GroupAndFilterTests
{
    [Fact]
    public async Task Groups_CRUD_Colors_Filters_IndependentPerServer()
    {
        var e = new Env();
        var s1 = await e.Repo.AddTrackedServerAsync("A", "1.1.1.1", 1, "1"); var s2 = await e.Repo.AddTrackedServerAsync("B", "2.2.2.2", 2, "2");
        var g = await e.Repo.AddGroupAsync("Друзья", "#112233");
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Repo.AddGroupAsync("Друзья", "#fff"));
        await e.Repo.AddWatchAsync(s1, "10", null, "Bob", g); await e.Repo.AddWatchAsync(s2, "10", null, "Bob", null);
        Assert.Single(await e.Repo.ListWatchAsync(s1)); Assert.Single(await e.Repo.ListWatchAsync(s2));
        Assert.Equal(g, (await e.Repo.ListWatchAsync(s1))[0].GroupId); Assert.Null((await e.Repo.ListWatchAsync(s2))[0].GroupId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Repo.AddWatchAsync(s1, "10", null, "Bob2"));
        var gr = (await e.Repo.ListGroupsAsync())[0]; gr.Name = "Союзники"; gr.Color = "#ABCDEF"; await e.Repo.UpdateGroupAsync(gr);
        Assert.Equal("#ABCDEF", (await e.Repo.ListGroupsAsync())[0].Color);
        await e.Repo.DeleteGroupAsync(g);
        Assert.Null((await e.Repo.ListWatchAsync(s1))[0].GroupId); // игрок остаётся, группа удалена
    }
}

public class ChannelTests
{
    private sealed class H : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Fn = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { LastBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct); if (Fn is null) throw new HttpRequestException(); return Fn(r); }
    }
    private static SettingsProvider S(Action<AppSettings>? f = null) { var s = new AppSettings { TelegramEnabled = true, TelegramChatId = "55", DiscordEnabled = true, RequestTimeoutSeconds = 2 }; f?.Invoke(s); var p = new SettingsProvider(); p.Update(s); return p; }
    private const string Hook = "https://discord.com/api/webhooks/1/abcSECRET";

    [Fact] public async Task Telegram_Success_Then_BadToken_BadChat_RateLimit_Network()
    {
        var sec = new MemorySecrets(); sec.Set(SecretNames.TelegramBotToken, "TOKEN123");
        var h = new H(); var ch = new TelegramChannel(new HttpClient(h), sec, S());
        Assert.True((await ch.SendAsync("t", "x", default)).IsOk); Assert.Contains("\"chat_id\":\"55\"", h.LastBody);
        foreach (var (code, st) in new[] { (401, SourceState.AccessDenied), (404, SourceState.AccessDenied), (400, SourceState.Error), (429, SourceState.RateLimited), (500, SourceState.Error) })
        {
            h.Fn = _ => new HttpResponseMessage((HttpStatusCode)code);
            var r = await ch.SendAsync("t", "x", default);
            Assert.Equal(st, r.State); Assert.DoesNotContain("TOKEN123", r.Message);
        }
        var bad = new TelegramChannel(new HttpClient(new FailingHandler()), sec, S());
        var net = await bad.SendAsync("t", "x", default);
        Assert.Equal(SourceState.NoResponse, net.State); Assert.DoesNotContain("TOKEN123", net.Message);
    }

    [Fact] public async Task Telegram_NotConfigured() =>
        Assert.Equal(SourceState.NotConfigured, (await new TelegramChannel(new HttpClient(new H()), new MemorySecrets(), S()).SendAsync("t", "x", default)).State);

    [Fact] public async Task Discord_Success_Invalid_Network()
    {
        var sec = new MemorySecrets(); sec.Set(SecretNames.DiscordWebhookUrl, Hook);
        var h = new H { Fn = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var ch = new DiscordChannel(new HttpClient(h), sec, S());
        Assert.True((await ch.SendAsync("t", "x", default)).IsOk);
        h.Fn = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var r = await ch.SendAsync("t", "x", default); Assert.Equal(SourceState.AccessDenied, r.State); Assert.DoesNotContain("abcSECRET", r.Message);
        Assert.Equal(SourceState.NoResponse, (await new DiscordChannel(new HttpClient(new FailingHandler()), sec, S()).SendAsync("t", "x", default)).State);
        var bad = new MemorySecrets(); bad.Set(SecretNames.DiscordWebhookUrl, "http://evil.example/hook");
        Assert.Equal(SourceState.NotConfigured, (await new DiscordChannel(new HttpClient(h), bad, S()).SendAsync("t", "x", default)).State);
    }

    private sealed class FailingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new HttpRequestException("boom https://api.telegram.org/botTOKEN123/sendMessage"); }

    private sealed class Spy : INotificationChannel
    {
        public int Sent; public string Name => "spy"; public bool IsEnabled(AppSettings s, NotifyKind k) => k != NotifyKind.Group;
        public Task<SourceResult<bool>> SendAsync(string t, string x, CancellationToken ct) { Sent++; return Task.FromResult(SourceResult<bool>.Ok(true)); }
    }

    [Fact] public async Task Dispatcher_Throttles_And_RespectsEventToggles()
    {
        var spy = new Spy(); var win = new FakeNotifier(); var clock = new FakeClock();
        var d = new NotificationDispatcher(win, new[] { spy }, S(s => s.NotifyMinIntervalSeconds = 60), clock);
        var m = new NotificationMessage(NotifyKind.Exit, "a", "b", "exit:1");
        Assert.True(await d.DispatchAsync(m, default) > 0);
        Assert.Equal(0, await d.DispatchAsync(m, default));      // в пределах интервала
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await d.DispatchAsync(m, default) > 0);
        Assert.Equal(2, spy.Sent);
        await d.DispatchAsync(new NotificationMessage(NotifyKind.Group, "g", "g", "g:1"), default);
        Assert.Equal(2, spy.Sent);                                // тип события выключен для канала
    }
}

public class ExportAndMigrationTests
{
    [Fact]
    public async Task Export_Import_RoundTrip_NoSecrets_DuplicatesSkipped()
    {
        var e = new Env(); await e.SeedAsync();
        var gid = await e.Repo.AddGroupAsync("Враги", "#FF0000");
        await e.Repo.UpdateWatchAsync(new WatchEntry { Id = e.WatchId, Name = "Enemy", GroupId = gid, NotifyJoin = true, NotifyLeave = true });
        var secrets = new MemorySecrets(); secrets.Set(SecretNames.TelegramBotToken, "TOKEN123"); secrets.Set(SecretNames.DiscordWebhookUrl, "https://discord.com/api/webhooks/1/abcSECRET"); secrets.Set(SecretNames.SteamApiKey, "STEAMKEY");
        var svc = new ExportService(e.Db, e.Repo, e.Servers, e.Settings);
        var json = await svc.ExportAsync(true, DateTimeOffset.UtcNow);
        foreach (var secret in new[] { "TOKEN123", "abcSECRET", "STEAMKEY", "webhooks" }) Assert.DoesNotContain(secret, json);

        var e2 = new Env(); var svc2 = new ExportService(e2.Db, e2.Repo, e2.Servers, e2.Settings);
        var rep = await svc2.ImportAsync(json);
        Assert.Equal((1, 1, 1), (rep.ServersAdded, rep.GroupsAdded, rep.WatchAdded));
        var again = await svc2.ImportAsync(json);                 // повторный импорт — без дублей
        Assert.Equal((0, 0, 0), (again.ServersAdded, again.GroupsAdded, again.WatchAdded));
        Assert.Single(await e2.Repo.ListWatchAsync()); Assert.Single(await e2.Repo.ListGroupsAsync());
    }

    [Fact]
    public async Task Import_RejectsGarbage_NewerVersion_BadSettings()
    {
        var e = new Env(); var svc = new ExportService(e.Db, e.Repo, e.Servers, e.Settings);
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.ImportAsync("not json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.ImportAsync("{\"Version\":99}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.ImportAsync("{\"Version\":1,\"Settings\":{\"ServerPollSeconds\":1}}"));
    }

    [Fact]
    public void Migration_V1_To_V2_PreservesUserData()
    {
        var path = TempDb.NewPath();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"CREATE TABLE timers(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, name TEXT NOT NULL, started_at TEXT NULL, interval_seconds INTEGER NOT NULL, lead_seconds INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '', notify_enabled INTEGER NOT NULL DEFAULT 1, lead_notified INTEGER NOT NULL DEFAULT 0, due_notified INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE timer_history(id INTEGER PRIMARY KEY AUTOINCREMENT, timer_id INTEGER NOT NULL REFERENCES timers(id) ON DELETE CASCADE, at TEXT NOT NULL, action TEXT NOT NULL, note TEXT NOT NULL DEFAULT '');
                CREATE TABLE servers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL, favorite INTEGER NOT NULL DEFAULT 0, UNIQUE(host, port));
                CREATE TABLE server_observations(id INTEGER PRIMARY KEY AUTOINCREMENT, server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE, at TEXT NOT NULL, players INTEGER NOT NULL, max_players INTEGER NOT NULL);
                CREATE TABLE players(id INTEGER PRIMARY KEY AUTOINCREMENT, steam_id TEXT NOT NULL UNIQUE, label TEXT NOT NULL DEFAULT '');
                CREATE TABLE base_profiles(name TEXT PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO servers(name,host,port,favorite) VALUES('Old','9.9.9.9',28016,1);
                INSERT INTO timers(kind,name,interval_seconds,lead_seconds) VALUES(0,'Airfield',1200,300);
                PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        var db = new Database(path); Assert.False(db.Initialize());
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak-v1"));
        var servers = new ServerRepository(db).ListAsync().Result;
        Assert.Single(servers); Assert.Equal("Old", servers[0].Name); Assert.True(servers[0].Favorite);
        Assert.Single(new SqliteTimerRepository(db).ListAsync(null).Result);
        Assert.NotEqual(0L, new WatchRepository(db).AddGroupAsync("g", "#fff").Result); // новые таблицы доступны
        Assert.Empty(new RustCompanion.Core.Data.NotificationPrefsRepository(db).LoadAsync().Result); // v3: настроек уведомлений нет → всё выключено
        Assert.Equal(0L, new RustCompanion.Core.Forecast.DecayRepository(db, new FakeClock()).ListAsync(true).Result.Count);
    }

    [Fact]
    public async Task Backup_CreatesReadableCopy()
    {
        var e = new Env(); await e.SeedAsync();
        var copy = TempDb.NewPath(); e.Db.BackupTo(copy);
        var db2 = new Database(copy); Assert.False(db2.Initialize());
        Assert.Single(await new WatchRepository(db2).ListWatchAsync());
    }
}
