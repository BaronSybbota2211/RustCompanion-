using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

/// <summary>Сборка пакетов по спецификации Valve независимо от кода парсера.</summary>
internal static class Pkt
{
    public static readonly byte[] Hdr = { 0xFF, 0xFF, 0xFF, 0xFF };

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public static byte[] Str(string s) => Encoding.UTF8.GetBytes(s + "\0");
    public static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
    public static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    public static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    public static byte[] F32(float v) => BitConverter.GetBytes(v);

    public static byte[] Info(string name, int players, int max, string? keywords = "cp100,mp200,qp0", int? gamePort = 28015)
    {
        var edf = (byte)((gamePort is null ? 0 : 0x80) | (keywords is null ? 0 : 0x20));
        var body = Cat(Hdr, new byte[] { 0x49, 17 }, Str(name), Str("Procedural Map"), Str("rust"), Str("Rust"), U16(252490),
            new byte[] { (byte)players, (byte)max, 0, (byte)'d', (byte)'w', 0, 1 }, Str("2500"));
        if (edf == 0) return body;
        return Cat(body, new[] { edf }, gamePort is null ? Array.Empty<byte>() : U16(gamePort.Value), keywords is null ? Array.Empty<byte>() : Str(keywords));
    }

    public static byte[] Players(params (string Name, float Seconds)[] list)
    {
        var parts = new List<byte[]> { Hdr, new byte[] { 0x44, (byte)list.Length } };
        for (var i = 0; i < list.Length; i++) parts.Add(Cat(new[] { (byte)i }, Str(list[i].Name), I32(i * 10), F32(list[i].Seconds)));
        return Cat(parts.ToArray());
    }

    public static byte[] Challenge(int c) => Cat(Hdr, new byte[] { 0x41 }, I32(c));

    public static byte[] Split(int id, int total, int number, byte[] payload) =>
        Cat(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, I32(id), new[] { (byte)total, (byte)number }, U16(1248), payload);
}

/// <summary>Подменяет UDP: на каждый отправленный запрос responder возвращает датаграммы-ответы (или ничего — тайм-аут).</summary>
internal sealed class FakeUdp : IUdpExchange, IUdpExchangeFactory
{
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Sent = new();
    public Func<byte[], IEnumerable<byte[]>> Responder = _ => Array.Empty<byte[]>();
    public int Connects;
    public Task<IUdpExchange> ConnectAsync(string host, int port, CancellationToken ct) { Connects++; return Task.FromResult<IUdpExchange>(this); }
    public Task SendAsync(byte[] datagram, CancellationToken ct)
    {
        lock (Sent) Sent.Add(datagram);
        foreach (var r in Responder(datagram)) _inbox.Writer.TryWrite(r);
        return Task.CompletedTask;
    }
    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => await _inbox.Reader.ReadAsync(ct);
    public void Dispose() { }
}

public class A2sPacketTests
{
    [Fact] public void InfoRequest_MatchesSpec_WithAndWithoutChallenge()
    {
        var plain = A2sProtocol.BuildInfoRequest(null);
        Assert.Equal(Pkt.Cat(Pkt.Hdr, new byte[] { 0x54 }, Pkt.Str("Source Engine Query")), plain);
        var withCh = A2sProtocol.BuildInfoRequest(0x01020304);
        Assert.Equal(Pkt.Cat(plain, new byte[] { 0x04, 0x03, 0x02, 0x01 }), withCh);
    }

    [Fact] public void PlayerRequest_InitialChallengeIsMinusOne()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x55, 0xFF, 0xFF, 0xFF, 0xFF }, A2sProtocol.BuildPlayerRequest(A2sProtocol.InitialChallenge));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x55, 0x78, 0x56, 0x34, 0x12 }, A2sProtocol.BuildPlayerRequest(0x12345678));
    }

    [Fact] public void Challenge_IsParsed_OtherPacketsAreNot()
    {
        Assert.True(A2sProtocol.TryParseChallenge(Pkt.Challenge(-123456), out var c)); Assert.Equal(-123456, c);
        Assert.False(A2sProtocol.TryParseChallenge(Pkt.Players(), out _));
        Assert.False(A2sProtocol.TryParseChallenge(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 1 }, out _));      // короткий
    }

    [Fact] public void Info_ParsesFieldsAndRustTags()
    {
        var i = A2sProtocol.ParseInfo(Pkt.Info("Rusty | EU", 255, 255, "cp312,mp500,qp7,pve", 28015));
        Assert.Equal(("Rusty | EU", "Procedural Map", 55882), (i.Name, i.Map, i.AppId)); // в A2S_INFO AppID занимает 2 байта: младшие 16 бит 252490
        Assert.Equal(28015, i.GamePort);
        Assert.Equal((312, 500, 7), (i.ReportedPlayers, i.ReportedMaxPlayers, i.Queue));     // тег cp не ограничен байтом 255
        Assert.True(i.PasswordProtected == false && i.Vac);
    }

    [Fact] public void Info_WithoutExtraData_UsesByteCounts()
    {
        var i = A2sProtocol.ParseInfo(Pkt.Info("Plain", 12, 50, null, null));
        Assert.Null(i.GamePort); Assert.Null(i.Keywords); Assert.Equal((12, 50), (i.ReportedPlayers, i.ReportedMaxPlayers)); Assert.Null(i.Queue);
    }

    [Fact] public void Info_TruncatedOptionalBlock_KeepsMainFields()
    {
        var full = Pkt.Info("Cut", 3, 10, "cp3,mp10", 28015);
        var cut = full.Take(full.Length - 6).ToArray();                  // оборван на середине keywords
        var i = A2sProtocol.ParseInfo(cut);
        Assert.Equal("Cut", i.Name); Assert.Equal(3, i.Players);
    }

    [Fact] public void Info_BadPackets_ThrowProtocolException()
    {
        Assert.Throws<A2sProtocolException>(() => A2sProtocol.ParseInfo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        Assert.Throws<A2sProtocolException>(() => A2sProtocol.ParseInfo(Pkt.Players()));                                   // не тот тип
        Assert.Throws<A2sProtocolException>(() => A2sProtocol.ParseInfo(Pkt.Cat(Pkt.Hdr, new byte[] { 0x49, 17, (byte)'a' })));  // обрыв до конца строки
    }

    [Fact] public void Players_ParsesNamesAndDurations()
    {
        var p = A2sProtocol.ParsePlayers(Pkt.Players(("Anna", 125.5f), ("Борис", 3600f)));
        Assert.Equal(2, p.DeclaredCount); Assert.False(p.Truncated);
        Assert.Equal(("Anna", 125.5f), (p.Players[0].Name, p.Players[0].DurationSeconds));
        Assert.Equal("Борис", p.Players[1].Name); Assert.True(p.Players[1].HasValidDuration);
    }

    [Fact] public void Players_TruncatedBody_IsMarkedTruncated_NotThrown()
    {
        var full = Pkt.Players(("A", 1f), ("B", 2f), ("C", 3f));
        var p = A2sProtocol.ParsePlayers(full.Take(full.Length - 7).ToArray());
        Assert.True(p.Truncated); Assert.Equal(3, p.DeclaredCount); Assert.True(p.Players.Count < 3);
    }

    [Fact] public void Players_InvalidDuration_IsNotTrusted()
    {
        var p = A2sProtocol.ParsePlayers(Pkt.Players(("Neg", -5f), ("Nan", float.NaN)));
        Assert.All(p.Players, x => Assert.False(x.HasValidDuration));
    }
}

public class A2sSplitTests
{
    private static (byte[][] Parts, byte[] Whole) Make(int id, int count)
    {
        var whole = Pkt.Players(Enumerable.Range(0, 40).Select(i => ("Player" + i, (float)i)).ToArray());
        var size = (whole.Length + count - 1) / count;
        var parts = Enumerable.Range(0, count).Select(n => Pkt.Split(id, count, n, whole.Skip(n * size).Take(size).ToArray())).ToArray();
        return (parts, whole);
    }

    [Fact] public void InOrder_Assembles()
    {
        var (parts, whole) = Make(7, 3); var a = new A2sSplitAssembler();
        Assert.Null(a.Add(parts[0])); Assert.Null(a.Add(parts[1])); Assert.Equal(whole, a.Add(parts[2]));
    }

    [Fact] public void OutOfOrderDuplicatesAndForeignId_Assemble()
    {
        var (parts, whole) = Make(7, 3); var a = new A2sSplitAssembler();
        Assert.Null(a.Add(parts[2])); Assert.Null(a.Add(parts[2]));                         // повтор
        Assert.Null(a.Add(Pkt.Split(99, 3, 1, new byte[] { 1, 2, 3 })));                    // чужой id игнорируется
        Assert.Null(a.Add(parts[0]));
        Assert.Equal(whole, a.Add(parts[1]));
    }

    [Fact] public void Compressed_Rejected()
    {
        var a = new A2sSplitAssembler();
        Assert.Throws<A2sProtocolException>(() => a.Add(Pkt.Split(unchecked((int)0x80000001), 2, 0, new byte[] { 1 })));
    }

    [Fact] public void InvalidHeaders_Rejected()
    {
        var a = new A2sSplitAssembler();
        Assert.Throws<A2sProtocolException>(() => a.Add(Pkt.Split(1, 0, 0, new byte[] { 1 })));        // total = 0
        Assert.Throws<A2sProtocolException>(() => a.Add(Pkt.Split(1, 2, 2, new byte[] { 1 })));        // номер ≥ total
        Assert.Throws<A2sProtocolException>(() => a.Add(Pkt.Split(1, 200, 0, new byte[] { 1 })));      // слишком много частей
        Assert.Throws<A2sProtocolException>(() => a.Add(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 1 }));    // обрезанный заголовок
    }
}

public class A2sClassifyTests
{
    private static A2sInfoResponse Info(int cp) => A2sProtocol.ParseInfo(Pkt.Info("S", 0, 0, $"cp{cp},mp100", 28015));
    private static A2sPlayersResponse P(int n, bool truncated = false, int declared = -1) =>
        new(Enumerable.Range(0, n).Select(i => new A2sPlayerEntry(i, "p" + i, 0, 10f + i * 37f)).ToList(), declared < 0 ? n : declared, truncated);

    [Fact] public void EmptyList_WithZeroOnline_IsEmpty() => Assert.Equal(PlayerListStatus.Empty, A2sSnapshotBuilder.Classify(Info(0), P(0)).ListStatus);
    [Fact] public void EmptyList_WithOnlinePlayers_IsNotProvided_NotFakeEmpty()
    {
        var s = A2sSnapshotBuilder.Classify(Info(80), P(0));
        Assert.Equal(PlayerListStatus.NotProvided, s.ListStatus); Assert.Contains("80", s.Note);
    }
    [Fact] public void BlankNames_AreNotProvided()
    {
        var blank = new A2sPlayersResponse(new[] { new A2sPlayerEntry(0, "", 0, 1f), new A2sPlayerEntry(1, " ", 0, 1f) }, 2, false);
        Assert.Equal(PlayerListStatus.NotProvided, A2sSnapshotBuilder.Classify(Info(2), blank).ListStatus);
    }
    [Fact] public void ShortList_IsPartial() => Assert.Equal(PlayerListStatus.Partial, A2sSnapshotBuilder.Classify(Info(300), P(120)).ListStatus);
    [Fact] public void TruncatedResponse_IsPartial() => Assert.Equal(PlayerListStatus.Partial, A2sSnapshotBuilder.Classify(Info(10), P(4, true, 10)).ListStatus);
    [Fact] public void WithinTolerance_IsProvided()
    {
        Assert.Equal(PlayerListStatus.Provided, A2sSnapshotBuilder.Classify(Info(100), P(96)).ListStatus);     // допуск 5%
        Assert.Equal(PlayerListStatus.Provided, A2sSnapshotBuilder.Classify(null, P(3)).ListStatus);          // без INFO сравнивать не с чем
    }
}

public class A2sClientTests
{
    private static A2sQueryClient Client(FakeUdp udp, int timeoutSeconds = 8)
    {
        var st = new SettingsProvider(); st.Update(new AppSettings { RequestTimeoutSeconds = timeoutSeconds, A2sMinIntervalMs = 200 });
        return new A2sQueryClient(udp, st, new RateLimiter());
    }

    private static IEnumerable<byte[]> Server(byte[] req, int challenge, byte[] info, byte[] players)
    {
        if (req[4] == 0x54) return new[] { info };
        if (req[4] == 0x55)
        {
            var c = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(5, 4));
            return c == challenge ? new[] { players } : new[] { Pkt.Challenge(challenge) };
        }
        return Array.Empty<byte[]>();
    }

    [Fact] public async Task Players_ChallengeFlow_RequestsAgainWithServerChallenge()
    {
        var udp = new FakeUdp(); udp.Responder = r => Server(r, 0x0BADF00D, Pkt.Info("S", 2, 10), Pkt.Players(("Ann", 61f), ("Bob", 7f)));
        var res = await Client(udp).GetPlayersAsync("1.2.3.4", 28017, default);
        Assert.True(res.IsOk); Assert.Equal(new[] { "Ann", "Bob" }, res.Value!.Players.Select(p => p.Name));
        Assert.Equal(2, udp.Sent.Count);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(udp.Sent[0].AsSpan(5, 4)));
        Assert.Equal(0x0BADF00D, BinaryPrimitives.ReadInt32LittleEndian(udp.Sent[1].AsSpan(5, 4)));
    }

    [Fact] public async Task Info_ChallengeRequired_RetriesWithChallenge()
    {
        var udp = new FakeUdp();
        udp.Responder = r => r.Length == 25 ? new[] { Pkt.Challenge(55) } : new[] { Pkt.Info("Chal", 1, 10) };         // без challenge длина 25, с ним — 29
        var res = await Client(udp).GetInfoAsync("h.example", 28017, default);
        Assert.True(res.IsOk); Assert.Equal("Chal", res.Value!.Name);
        Assert.Equal(BitConverter.GetBytes(55), udp.Sent[1].TakeLast(4).ToArray());
    }

    [Fact] public async Task Snapshot_SplitPlayerResponse_IsAssembled()
    {
        var big = Pkt.Players(Enumerable.Range(0, 60).Select(i => ("Player" + i, (float)i * 2)).ToArray());
        var half = big.Length / 2;
        var udp = new FakeUdp();
        udp.Responder = r => r[4] == 0x54 ? new[] { Pkt.Info("Big", 60, 100, "cp60,mp100", 28015) }
            : BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5, 4)) == 9
                ? new[] { Pkt.Split(5, 2, 1, big.Skip(half).ToArray()), Pkt.Split(5, 2, 0, big.Take(half).ToArray()) }     // части в обратном порядке
                : new[] { Pkt.Challenge(9) };
        var res = await Client(udp).GetSnapshotAsync("1.2.3.4", 28017, default);
        Assert.True(res.IsOk); Assert.Equal(PlayerListStatus.Provided, res.Value!.ListStatus); Assert.Equal(60, res.Value.Players.Count);
    }

    [Fact] public async Task ForeignDatagrams_AreIgnored()
    {
        var udp = new FakeUdp(); udp.Responder = r => new[] { new byte[] { 1, 2, 3 }, Pkt.Info("Ok", 1, 2) };
        var res = await Client(udp).GetInfoAsync("1.2.3.4", 28017, default);
        Assert.True(res.IsOk); Assert.Equal("Ok", res.Value!.Name);
    }

    [Fact] public async Task InfoOk_PlayerNoResponse_GivesHonestStatus_NotFailure()
    {
        var udp = new FakeUdp(); udp.Responder = r => r[4] == 0x54 ? new[] { Pkt.Info("Quiet", 10, 50) } : Array.Empty<byte[]>();
        var res = await Client(udp, 2).GetSnapshotAsync("1.2.3.4", 28017, default);
        Assert.True(res.IsOk); Assert.Equal(PlayerListStatus.NoResponse, res.Value!.ListStatus); Assert.Empty(res.Value.Players);
    }

    [Fact] public async Task NoAnswerAtAll_IsNoResponse_NotClaimingServerOff()
    {
        var udp = new FakeUdp();
        var res = await Client(udp, 2).GetSnapshotAsync("1.2.3.4", 28017, default);
        Assert.Equal(SourceState.NoResponse, res.State); Assert.Contains("не означает", res.Message);
        Assert.True(udp.Sent.Count >= A2sQueryClient.Attempts);          // были повторы отправки
    }

    [Fact] public async Task GarbageReply_IsProtocolError()
    {
        var udp = new FakeUdp(); udp.Responder = r => new[] { Pkt.Cat(Pkt.Hdr, new byte[] { 0x49, 17, 65 }) };
        var res = await Client(udp).GetInfoAsync("1.2.3.4", 28017, default);
        Assert.Equal(SourceState.Error, res.State);
    }

    [Fact] public async Task EndlessChallenges_AreStopped()
    {
        var udp = new FakeUdp(); var n = 0; udp.Responder = r => new[] { Pkt.Challenge(++n) };
        var res = await Client(udp).GetPlayersAsync("1.2.3.4", 28017, default);
        Assert.Equal(SourceState.Error, res.State); Assert.True(udp.Sent.Count <= A2sQueryClient.MaxChallengeRounds + 1);
    }

    [Fact] public async Task Cancellation_Propagates_Quickly()
    {
        var udp = new FakeUdp(); using var cts = new CancellationTokenSource(); cts.CancelAfter(100);
        var t = Client(udp, 30).GetInfoAsync("1.2.3.4", 28017, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
    }

    [Fact] public async Task InvalidPort_IsRejected_WithoutNetwork()
    {
        var udp = new FakeUdp();
        Assert.Equal(SourceState.Error, (await Client(udp).GetInfoAsync("1.2.3.4", 0, default)).State); Assert.Equal(0, udp.Connects);
    }
}

public class MasterServerTests
{
    [Fact] public void Query_HasRegionSeedAndFilter()
    {
        var q = MasterServerClient.BuildQuery("0.0.0.0:0", "\\appid\\252490");
        Assert.Equal(0x31, q[0]); Assert.Equal(0xFF, q[1]); Assert.Equal(0, q[^1]);
        Assert.Equal("0.0.0.0:0\0\\appid\\252490\0", Encoding.ASCII.GetString(q, 2, q.Length - 2));
    }

    [Fact] public void Response_ParsesAddresses_AndTerminator()
    {
        var p = Pkt.Cat(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x66, 0x0A }, new byte[] { 1, 2, 3, 4, 0x6D, 0x91 }, new byte[] { 5, 6, 7, 8, 0x00, 0x50 });
        var (list, done) = MasterServerClient.ParseResponse(p);
        Assert.False(done); Assert.Equal(new[] { "1.2.3.4:28049", "5.6.7.8:80" }, list.Select(a => a.Key));
        var (list2, done2) = MasterServerClient.ParseResponse(Pkt.Cat(p, new byte[] { 0, 0, 0, 0, 0, 0 }));
        Assert.True(done2); Assert.Equal(2, list2.Count);
    }

    [Fact] public void Response_Malformed_Throws()
    {
        Assert.Throws<A2sProtocolException>(() => MasterServerClient.ParseResponse(new byte[] { 1, 2, 3 }));
        Assert.Throws<A2sProtocolException>(() => MasterServerClient.ParseResponse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x66, 0x0A, 1, 2, 3 }));
    }

    [Fact] public void NameFilter_CannotInjectOwnConditions()
    {
        var f = MasterServerClient.BuildNameFilter("my\\map\\x*");
        Assert.Equal(1, f.Split("name_match").Length - 1); Assert.DoesNotContain("\\map\\", f); Assert.DoesNotContain("x*", f.Replace("*mymapx*", ""));
    }
}

public class ServerKeyTests
{
    [Theory] [InlineData("1.2.3.4:28017", true)] [InlineData("play.example.com:28015", true)] [InlineData("1.2.3.4", false)] [InlineData("1.2.3.4:0", false)]
    [InlineData("1.2.3.4:70000", false)] [InlineData("bad host:1", false)] [InlineData("", false)] [InlineData(null, false)] [InlineData("999", false)]
    public void TryParse(string? key, bool ok) => Assert.Equal(ok, ServerKey.TryParse(key, out _, out _));

    [Fact] public void Make_RoundTrips() { Assert.True(ServerKey.TryParse(ServerKey.Make("5.6.7.8", 28017), out var h, out var p)); Assert.Equal(("5.6.7.8", 28017), (h, p)); }
}

public class PresenceHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static (PresenceHistoryRepository R, Database Db) Make() { var db = new Database(TempDb.NewPath()); db.Initialize(); return (new PresenceHistoryRepository(db), db); }
    private static IEnumerable<(string, double?)> L(params string[] n) => n.Select(x => (x, (double?)null));

    [Fact] public async Task Continuous_Observations_ExtendOneRecord_WithEstimatedTime()
    {
        var (r, _) = Make(); var g = TimeSpan.FromMinutes(4);
        await r.RecordSnapshotAsync("1.1.1.1:1", L("Ann"), T0, g);
        await r.RecordSnapshotAsync("1.1.1.1:1", L("Ann"), T0.AddSeconds(30), g);
        await r.RecordSnapshotAsync("1.1.1.1:1", L("Ann"), T0.AddSeconds(90), g);
        var rec = Assert.Single(await r.ListRecordsAsync("1.1.1.1:1", "Ann"));
        Assert.Equal((T0, T0.AddSeconds(90), 3, 90.0), (rec.FirstSeen, rec.LastSeen, rec.Samples, rec.EstimatedSeconds));
    }

    [Fact] public async Task LongGap_StartsNewVisit_AndSummaryAggregates()
    {
        var (r, _) = Make(); var g = TimeSpan.FromMinutes(4);
        await r.RecordSnapshotAsync("s:1", L("Ann", "Bob"), T0, g);
        await r.RecordSnapshotAsync("s:1", L("Ann"), T0.AddSeconds(60), g);
        await r.RecordSnapshotAsync("s:1", L("Ann"), T0.AddHours(2), g);                      // большая пауза → новый заход
        Assert.Equal(2, (await r.ListRecordsAsync("s:1", "Ann")).Count);
        var sum = await r.ListSummaryAsync("s:1");
        var ann = sum.Single(x => x.Name == "Ann");
        Assert.Equal((T0, T0.AddHours(2), 2, 60.0), (ann.FirstSeen, ann.LastSeen, ann.Visits, ann.EstimatedSeconds));
        Assert.Single(await r.ListSummaryAsync("s:1", "bo"));                                  // фильтр без учёта регистра
    }

    [Fact] public async Task ServersAreIsolated_AndClockGoingBackwardsChangesNothing()
    {
        var (r, _) = Make(); var g = TimeSpan.FromMinutes(4);
        await r.RecordSnapshotAsync("a:1", L("Ann"), T0, g);
        await r.RecordSnapshotAsync("b:1", L("Ann"), T0, g);
        await r.RecordSnapshotAsync("a:1", L("Ann"), T0.AddSeconds(-30), g);
        Assert.Single(await r.ListSummaryAsync("a:1")); Assert.Single(await r.ListSummaryAsync("b:1"));
        var rec = Assert.Single(await r.ListRecordsAsync("a:1", "Ann")); Assert.Equal(0.0, rec.EstimatedSeconds);
    }

    [Fact] public async Task ReportedSeconds_AreStoredSeparately_FromEstimate()
    {
        var (r, _) = Make();
        await r.RecordSnapshotAsync("s:1", new[] { ("Ann", (double?)500.0) }, T0, TimeSpan.FromMinutes(4));
        var rec = Assert.Single(await r.ListRecordsAsync("s:1", "Ann"));
        Assert.Equal(500.0, rec.ReportedSeconds); Assert.Equal(0.0, rec.EstimatedSeconds);
    }

    [Fact] public async Task Persists_AcrossReopen_AndPrunes()
    {
        var path = TempDb.NewPath(); var db = new Database(path); db.Initialize();
        await new PresenceHistoryRepository(db).RecordSnapshotAsync("s:1", L("Ann"), T0, TimeSpan.FromMinutes(4));
        var db2 = new Database(path); db2.Initialize(); var r2 = new PresenceHistoryRepository(db2);
        Assert.Single(await r2.ListSummaryAsync("s:1"));
        Assert.Equal(1, await r2.PruneAsync(T0.AddDays(1)));
        Assert.Empty(await r2.ListSummaryAsync("s:1"));
    }

    [Fact] public async Task EmptyAndBlankNames_AreIgnored()
    {
        var (r, _) = Make();
        await r.RecordSnapshotAsync("s:1", L("", "  "), T0, TimeSpan.FromMinutes(4));
        Assert.Empty(await r.ListSummaryAsync("s:1"));
    }
}

public class A2sServerSourceTests
{
    private static (A2sServerSource S, FakeUdp Udp, PresenceHistoryRepository H, FakeClock Clock) Make()
    {
        var db = new Database(TempDb.NewPath()); db.Initialize(); var history = new PresenceHistoryRepository(db);
        var udp = new FakeUdp(); var st = new SettingsProvider(); st.Update(new AppSettings { RequestTimeoutSeconds = 2, A2sMinIntervalMs = 200 });
        var limiter = new RateLimiter();
        var steam = new SteamServerBrowser(new HttpClient(), new MemorySecrets(), st, limiter);
        var clock = new FakeClock();
        return (new A2sServerSource(new A2sQueryClient(udp, st, limiter), steam, new MasterServerClient(udp, st, limiter), history, clock), udp, history, clock);
    }

    private static IEnumerable<byte[]> Reply(byte[] r, byte[] info, byte[] players) =>
        r[4] == 0x54 ? new[] { info } : BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5, 4)) == 7 ? new[] { players } : new[] { Pkt.Challenge(7) };

    [Fact] public async Task SelectedServer_ShowsPlayersWithServerReportedTime_AndWritesHistory()
    {
        var (s, udp, h, clock) = Make();
        udp.Responder = r => Reply(r, Pkt.Info("Srv", 2, 100, "cp2,mp100"), Pkt.Players(("Ann", 125f), ("Bob", 9f)));
        var res = await s.GetServerPlayersAsync("1.2.3.4:28017", default);
        Assert.True(res.IsOk); var snap = res.Value!;
        Assert.True(snap.ListProvided); Assert.Equal(new[] { "Ann", "Bob" }, snap.Players.Select(p => p.Name));
        Assert.Equal(125.0, snap.Players[0].SessionSeconds);
        Assert.Equal(2, (await h.ListSummaryAsync("1.2.3.4:28017")).Count);
    }

    [Fact] public async Task HiddenPlayerList_IsNotProvided_NoHistory_PresenceUnknown()
    {
        var (s, udp, h, _) = Make();
        udp.Responder = r => Reply(r, Pkt.Info("Hidden", 50, 100, "cp50,mp100"), Pkt.Players());
        var snap = (await s.GetServerPlayersAsync("1.2.3.4:28017", default)).Value!;
        Assert.False(snap.ListProvided); Assert.Equal(PlayerListStatus.NotProvided, snap.ListStatus); Assert.Empty(snap.Players); Assert.Equal(50, snap.Server.Players);
        Assert.Empty(await h.ListSummaryAsync("1.2.3.4:28017"));
        Assert.Equal(PresenceKind.Unknown, A2sServerSource.Evaluate(snap, "Ann", DateTimeOffset.UtcNow).Kind);       // скрытый список ≠ «игрока нет»
    }

    [Fact] public async Task Presence_PresentAbsentAndUnknown()
    {
        var full = new ServerSnapshot(new FoundServer("k:1", "S", "k", 1, 1, 10, "x"), new[] { new OnlinePlayer("Ann", "Ann") }, true, PlayerListStatus.Provided);
        var partial = full with { ListStatus = PlayerListStatus.Partial };
        var empty = new ServerSnapshot(full.Server, Array.Empty<OnlinePlayer>(), true, PlayerListStatus.Empty);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(PresenceKind.Present, A2sServerSource.Evaluate(full, "Ann", now).Kind);
        Assert.Equal(PresenceKind.Absent, A2sServerSource.Evaluate(full, "Zed", now).Kind);
        Assert.Equal(PresenceKind.Unknown, A2sServerSource.Evaluate(partial, "Zed", now).Kind);       // неполный список не доказывает отсутствие
        Assert.Equal(PresenceKind.Present, A2sServerSource.Evaluate(partial, "Ann", now).Kind);
        Assert.Equal(PresenceKind.Absent, A2sServerSource.Evaluate(empty, "Ann", now).Kind);
        await Task.CompletedTask;
    }

    [Fact] public async Task SnapshotIsCached_ShortTime_ThenRequeried()
    {
        var (s, udp, _, clock) = Make();
        udp.Responder = r => Reply(r, Pkt.Info("C", 1, 10, "cp1,mp10"), Pkt.Players(("Ann", 1f)));
        await s.GetServerPlayersAsync("1.2.3.4:28017", default); var first = udp.Sent.Count;
        await s.GetServerPlayersAsync("1.2.3.4:28017", default); Assert.Equal(first, udp.Sent.Count);
        clock.Advance(A2sServerSource.SnapshotTtl + TimeSpan.FromSeconds(1));
        await s.GetServerPlayersAsync("1.2.3.4:28017", default); Assert.True(udp.Sent.Count > first);
    }

    [Fact] public async Task SwitchingServers_DoesNotMixData()
    {
        var (s, udp, h, _) = Make();
        udp.Responder = r => Reply(r, Pkt.Info("S", 1, 10, "cp1,mp10"), Pkt.Players(("Ann", 1f)));
        await s.GetServerPlayersAsync("1.1.1.1:28017", default);
        udp.Responder = r => Reply(r, Pkt.Info("T", 1, 10, "cp1,mp10"), Pkt.Players(("Zed", 1f)));
        var second = (await s.GetServerPlayersAsync("2.2.2.2:28017", default)).Value!;
        Assert.Equal("Zed", second.Players.Single().Name);
        Assert.Equal("Ann", (await h.ListSummaryAsync("1.1.1.1:28017")).Single().Name); Assert.Equal("Zed", (await h.ListSummaryAsync("2.2.2.2:28017")).Single().Name);
    }

    [Fact] public async Task BadKey_And_NoAnswer_AreReportedWithoutFakeList()
    {
        var (s, _, _, _) = Make();
        Assert.Equal(SourceState.Error, (await s.GetServerPlayersAsync("nonsense", default)).State);
        var r = await s.GetServerPlayersAsync("1.2.3.4:28017", default);               // сервер молчит
        Assert.Equal(SourceState.NoResponse, r.State);
    }

    [Fact] public async Task Cancel_WhileWaiting_Throws()
    {
        var (s, _, _, _) = Make(); using var cts = new CancellationTokenSource(); cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.GetServerPlayersAsync("1.2.3.4:28017", cts.Token));
    }
}

public class MigrationV7Tests
{
    [Fact] public void FreshDb_HasPresenceHistoryAndNeutralColumns()
    {
        var path = TempDb.NewPath(); var db = new Database(path); db.Initialize();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c.Open();
        string Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='presence_history'"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='source_key'"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM pragma_table_info('servers') WHERE name='bm_id'"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM pragma_table_info('watch_entries') WHERE name='player_key'"));
    }
}

public class A2sListAuditTests
{
    private static A2sInfoResponse Info(int players) => new(1, "S", "Map", "rust", "Rust", 252490, players, 200, 0, 'd', 'w', false, true, "1", 28015, null, null, null);
    private static A2sPlayersResponse Players(IEnumerable<A2sPlayerEntry> p) { var l = p.ToList(); return new(l, l.Count, false); }

    [Fact] public void RealisticList_IsProvided()
    {
        var list = Enumerable.Range(0, 30).Select(i => new A2sPlayerEntry(i, "Real" + i, 0, 60f + i * 311f));
        Assert.Equal(PlayerListStatus.Provided, A2sSnapshotBuilder.Classify(Info(30), Players(list)).ListStatus);
    }

    [Fact] public void IdenticalDurations_AreSuspicious()
    {
        var list = Enumerable.Range(0, 20).Select(i => new A2sPlayerEntry(i, "Fake" + i, 0, 100f));
        var s = A2sSnapshotBuilder.Classify(Info(20), Players(list));
        Assert.Equal(PlayerListStatus.Suspicious, s.ListStatus); Assert.Contains("одинаковое время", s.Note);
    }

    [Fact] public void ListLargerThanReportedOnline_IsSuspicious()
    {
        var list = Enumerable.Range(0, 40).Select(i => new A2sPlayerEntry(i, "N" + i, 0, 10f + i * 90f));
        Assert.Equal(PlayerListStatus.Suspicious, A2sSnapshotBuilder.Classify(Info(10), Players(list)).ListStatus);
    }

    [Fact] public void RepeatedNames_AreSuspicious_AndSuspiciousListIsNotShown()
    {
        var list = Enumerable.Range(0, 20).Select(i => new A2sPlayerEntry(i, "Bot" + (i % 3), 0, 10f + i * 90f));
        var a = A2sSnapshotBuilder.Classify(Info(20), Players(list));
        Assert.Equal(PlayerListStatus.Suspicious, a.ListStatus);
        var snap = RustCompanion.Core.Watch.A2sServerSource.ToSnapshot("h:1", "h", 1, a);
        Assert.False(snap.ListProvided); Assert.Empty(snap.Players);
    }
}
