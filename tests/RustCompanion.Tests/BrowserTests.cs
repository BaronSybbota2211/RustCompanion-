using System.Text.Json;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

internal sealed class ScriptedSource : IServerQuerySource
{
    public Func<string, CancellationToken, Task<SourceResult<IReadOnlyList<FoundServer>>>> Search = (q, ct) => Task.FromResult(SourceResult<IReadOnlyList<FoundServer>>.Ok(new List<FoundServer>()));
    public Func<string, CancellationToken, Task<SourceResult<ServerSnapshot>>> Players = (id, ct) => Task.FromResult(SourceResult<ServerSnapshot>.Fail(SourceState.NoData, "n/a"));
    public List<string> Queries = new();
    public Task<SourceResult<IReadOnlyList<FoundServer>>> SearchServersAsync(string q, CancellationToken ct) { lock (Queries) Queries.Add(q); return Search(q, ct); }
    public Task<SourceResult<IReadOnlyList<FoundPlayer>>> SearchPlayersAsync(string q, string? s, CancellationToken ct) => Task.FromResult(SourceResult<IReadOnlyList<FoundPlayer>>.Ok(new List<FoundPlayer>()));
    public Task<SourceResult<PresenceObservation>> CheckPresenceAsync(string p, string s, CancellationToken ct) => Task.FromResult(SourceResult<PresenceObservation>.Fail(SourceState.NoData, "n/a"));
    public Task<SourceResult<IReadOnlyList<PresenceSession>>> GetSessionsAsync(string p, string? s, CancellationToken ct) => Task.FromResult(SourceResult<IReadOnlyList<PresenceSession>>.Ok(new List<PresenceSession>()));
    public Task<SourceResult<ServerSnapshot>> GetServerPlayersAsync(string id, CancellationToken ct) => Players(id, ct);
}

public class ServerSearchTests
{
    private static FoundServer S(string id, string name) => new(id, name, "1.2.3.4", 28015, 10, 100, "online", "Procedural", "DE");
    private static SourceResult<IReadOnlyList<FoundServer>> Ok(params FoundServer[] s) => SourceResult<IReadOnlyList<FoundServer>>.Ok(s.ToList());

    [Fact] public async Task TypingStartsSearch_AndShowsOnlyRealResults()
    {
        var bm = new ScriptedSource { Search = (q, ct) => Task.FromResult(Ok(S("111", "Rustafied EU"), S("222", "Rustafied US"))) };
        var c = new ServerSearchController(bm, TimeSpan.Zero);
        var states = new List<SearchPhase>(); c.Changed += s => states.Add(s.Phase);
        await c.SetQueryAsync("Rustafied");
        Assert.Equal(SearchPhase.Results, c.State.Phase); Assert.Equal(new[] { "111", "222" }, c.State.Results.Select(r => r.Id));
        Assert.Contains(SearchPhase.Loading, states);                                         // индикатор загрузки был показан
    }

    [Fact] public async Task EmptyResult_And_ApiError_AreDifferentStates_WithRetry()
    {
        var bm = new ScriptedSource { Search = (q, ct) => Task.FromResult(Ok()) };
        var c = new ServerSearchController(bm, TimeSpan.Zero);
        await c.SetQueryAsync("nothing here");
        Assert.Equal(SearchPhase.Empty, c.State.Phase); Assert.Equal("Серверы не найдены", c.State.Message); Assert.Null(c.State.ErrorKind);
        bm.Search = (q, ct) => Task.FromResult(SourceResult<IReadOnlyList<FoundServer>>.Fail(SourceState.RateLimited, "лимит"));
        await c.RetryAsync();
        Assert.Equal(SearchPhase.Error, c.State.Phase); Assert.Equal(SourceState.RateLimited, c.State.ErrorKind); Assert.Empty(c.State.Results);
        bm.Search = (q, ct) => Task.FromResult(Ok(S("1", "A")));
        await c.RetryAsync();                                                                  // повтор работает
        Assert.Equal(SearchPhase.Results, c.State.Phase);
    }

    [Fact] public async Task StaleResponse_NeverOverwritesNewerQuery()
    {
        var slow = new TaskCompletionSource<SourceResult<IReadOnlyList<FoundServer>>>();
        var bm = new ScriptedSource { Search = (q, ct) => q == "old" ? slow.Task : Task.FromResult(Ok(S("2", "New server"))) };
        var c = new ServerSearchController(bm, TimeSpan.Zero);
        var first = c.SetQueryAsync("old");                                                    // зависший запрос
        await Task.Delay(50);
        await c.SetQueryAsync("new");                                                          // более новый запрос завершился раньше
        slow.SetResult(Ok(S("1", "OLD SERVER")));                                              // поздний ответ старого запроса
        await first;
        Assert.Equal("new", c.State.Query); Assert.Equal(new[] { "2" }, c.State.Results.Select(r => r.Id));
    }

    [Fact] public async Task Debounce_CollapsesRapidTyping_IntoOneRequest()
    {
        var bm = new ScriptedSource { Search = (q, ct) => Task.FromResult(Ok(S("1", q))) };
        var c = new ServerSearchController(bm, TimeSpan.FromMilliseconds(150));
        var tasks = new[] { "ru", "rus", "rust", "rusta" }.Select(q => c.SetQueryAsync(q)).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(new[] { "rusta" }, bm.Queries); Assert.Equal(SearchPhase.Results, c.State.Phase);
    }

    [Fact] public async Task ShortQuery_DoesNotCallApi_AndSelectionKeepsServerId()
    {
        var bm = new ScriptedSource { Search = (q, ct) => Task.FromResult(Ok(S("9001", "Pick me"))) };
        var c = new ServerSearchController(bm, TimeSpan.Zero);
        await c.SetQueryAsync("r"); Assert.Empty(bm.Queries); Assert.Equal(SearchPhase.Idle, c.State.Phase);
        await c.SetQueryAsync("pick");
        var chosen = c.State.Results[0];
        Assert.Equal("9001", chosen.Id);                                                       // выбирается стабильный BM id, не название
    }

    [Fact] public async Task Recents_PersistByServerId_AndUpdateWithoutDuplicates()
    {
        var e = new EventEnv(); var repo = new RecentServersRepository(e.Db);
        await repo.TouchAsync(S("1", "A"), EventEnv.T0); await repo.TouchAsync(S("2", "B"), EventEnv.T0.AddMinutes(1)); await repo.TouchAsync(S("1", "A renamed"), EventEnv.T0.AddMinutes(2));
        var list = await new RecentServersRepository(new EventEnv(e.Path).Db).ListAsync();
        Assert.Equal(new[] { "1", "2" }, list.Select(x => x.SourceKey)); Assert.Equal("A renamed", list[0].Name);
    }
}

public class ConnectCommandTests
{
    [Theory] [InlineData("1.2.3.4", 28015, "net.connect 1.2.3.4:28015")] [InlineData("eu.rustafied.com", 28016, "net.connect eu.rustafied.com:28016")] [InlineData(" 195.60.166.148 ", 28015, "net.connect 195.60.166.148:28015")]
    public void ValidAddress_ProducesCommand(string host, int port, string expected) => Assert.Equal(expected, ConnectCommand.Build(host, port));

    [Theory] [InlineData(null, 28015)] [InlineData("", 28015)] [InlineData("1.2.3.4", null)] [InlineData("1.2.3.4", 0)] [InlineData("1.2.3.4", 70000)]
    [InlineData("999.1.1.1", 28015)] [InlineData("0.0.0.0", 28015)] [InlineData("not a host", 28015)] [InlineData("http://x.com", 28015)] [InlineData("::1", 28015)] [InlineData("1.2.3", 28015)]
    public void MissingOrInvalid_NeverInventsCommand(string? host, int? port) => Assert.Null(ConnectCommand.Build(host, port));

    private sealed class Clip : IClipboardService { public string? Text; public bool Fail; public void SetText(string t) { if (Fail) throw new System.Runtime.InteropServices.ExternalException("busy"); Text = t; } }

    [Fact] public void Copy_PutsCommandInClipboard_AndConfirms()
    {
        var clip = new Clip(); var msg = new ConnectCopier(clip).Copy(ConnectCommand.Build("1.2.3.4", 28015));
        Assert.Equal("net.connect 1.2.3.4:28015", clip.Text); Assert.Equal("Команда скопирована", msg);
    }

    [Fact] public void Copy_WithoutCommand_OrBusyClipboard_IsHandled()
    {
        var clip = new Clip();
        Assert.Equal(ConnectCommand.Unavailable, new ConnectCopier(clip).Copy(null)); Assert.Null(clip.Text);
        clip.Fail = true; Assert.Contains("Не удалось", new ConnectCopier(clip).Copy("net.connect 1.2.3.4:1"));
    }
}

public class ServerPlayersTests
{
    private static FoundServer Srv(string id, int? players) => new(id, "Server " + id, "1.1.1.1", 28015, players, 100, "online");
    private static SourceResult<ServerSnapshot> Snap(string id, int? reported, bool provided, params string[] names) =>
        SourceResult<ServerSnapshot>.Ok(new ServerSnapshot(Srv(id, reported), names.Select((n, i) => new OnlinePlayer(id + "-" + i, n)).ToList(), provided));

    [Fact] public async Task ShowsPlayersOfSelectedServer_Sorted()
    {
        var bm = new ScriptedSource { Players = (id, ct) => Task.FromResult(Snap(id, 3, true, "Zed", "alice", "Bob")) };
        var c = new ServerPlayersController(bm, new FakeClock());
        await c.SelectServerAsync("A");
        Assert.Equal(PlayersPhase.Ready, c.State.Phase); Assert.Equal("A", c.State.ServerId);
        Assert.Equal(new[] { "alice", "Bob", "Zed" }, c.State.Players.Select(p => p.Name));
        Assert.NotNull(c.State.LastSuccessUtc); Assert.False(c.State.Partial);
    }

    [Fact] public async Task SwitchingServer_ClearsPreviousPlayersImmediately_AndLateResponseIsDropped()
    {
        var slowA = new TaskCompletionSource<SourceResult<ServerSnapshot>>();
        var bm = new ScriptedSource { Players = (id, ct) => id == "A" ? slowA.Task : Task.FromResult(Snap(id, 1, true, "OnlyB")) };
        var c = new ServerPlayersController(bm, new FakeClock());
        var a = c.SelectServerAsync("A");
        Assert.Equal(PlayersPhase.Loading, c.State.Phase);
        await c.SelectServerAsync("B");
        Assert.Equal(new[] { "OnlyB" }, c.State.Players.Select(p => p.Name));
        slowA.SetResult(Snap("A", 1, true, "PlayerFromA"));                                    // поздний ответ предыдущего сервера
        await a;
        Assert.Equal("B", c.State.ServerId); Assert.DoesNotContain(c.State.Players, p => p.Name == "PlayerFromA");
    }

    [Fact] public async Task Loading_Empty_Unavailable_Error_AreDistinct()
    {
        var bm = new ScriptedSource(); var c = new ServerPlayersController(bm, new FakeClock());
        bm.Players = (id, ct) => Task.FromResult(Snap(id, 0, true)); await c.SelectServerAsync("A"); Assert.Equal(PlayersPhase.Empty, c.State.Phase);
        bm.Players = (id, ct) => Task.FromResult(Snap(id, 40, false)); await c.SelectServerAsync("A");
        Assert.Equal(PlayersPhase.Unavailable, c.State.Phase); Assert.Empty(c.State.Players); Assert.Equal(40, c.State.ReportedPlayers);   // список не имитируется
        bm.Players = (id, ct) => Task.FromResult(Snap(id, 40, true)); await c.SelectServerAsync("A"); Assert.Equal(PlayersPhase.Empty, c.State.Phase);
        bm.Players = (id, ct) => Task.FromResult(SourceResult<ServerSnapshot>.Fail(SourceState.NoResponse, "нет связи")); await c.SelectServerAsync("A");
        Assert.Equal(PlayersPhase.Error, c.State.Phase); Assert.Equal("нет связи", c.State.Message);
    }

    [Fact] public async Task PartialList_IsFlagged_AndErrorKeepsLastKnownAsStale()
    {
        var clock = new FakeClock(); var bm = new ScriptedSource { Players = (id, ct) => Task.FromResult(Snap(id, 120, true, "A", "B")) };
        var c = new ServerPlayersController(bm, clock, () => TimeSpan.FromMinutes(3));
        await c.SelectServerAsync("X"); Assert.True(c.State.Partial); Assert.Contains("неполным", c.State.Message);
        bm.Players = (id, ct) => Task.FromResult(SourceResult<ServerSnapshot>.Fail(SourceState.RateLimited, "лимит"));
        clock.Advance(TimeSpan.FromMinutes(5)); await c.RefreshAsync();
        Assert.Equal(PlayersPhase.Error, c.State.Phase); Assert.Equal(2, c.State.Players.Count); Assert.True(c.State.Stale);       // прежние данные помечены устаревшими
        Assert.True(c.IsStale(clock.UtcNow));
    }
}

public class SessionViewTests
{
    [Fact] public void Durations_OnlyFromRealTimestamps_OpenSessionMarked()
    {
        var now = EventEnv.T0.AddHours(5);
        var rows = SessionView.Build(new[]
        {
            new PresenceSession(EventEnv.T0, EventEnv.T0.AddMinutes(90), "1"),
            new PresenceSession(EventEnv.T0.AddHours(3), null, "1"),
            new PresenceSession(EventEnv.T0.AddHours(10), null, "1"),     // «в будущем» относительно now: длительность не вычисляется
        }, now);
        Assert.Null(rows[0].Duration); Assert.True(rows[0].NotEndedPerSource);
        Assert.Equal(TimeSpan.FromHours(2), rows[1].Duration); Assert.True(rows[1].NotEndedPerSource);
        Assert.Equal(TimeSpan.FromMinutes(90), rows[2].Duration); Assert.False(rows[2].NotEndedPerSource);
    }
}

public class SteamFilterTests
{
    [Fact] public void Filter_StripsMasterServerSpecialCharacters()
    {
        Assert.Equal("\\appid\\252490\\name_match\\*magic rust*", SteamServerBrowser.BuildFilter("  magic rust "));
        Assert.Equal("\\appid\\252490\\name_match\\*xempty1*", SteamServerBrowser.BuildFilter("x\\empty\\1"));      // «\» и «*» вырезаются — чужие условия не подмешать
    }
}
