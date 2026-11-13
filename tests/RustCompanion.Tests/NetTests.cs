using System.Net;
using System.Net.Sockets;
using System.Text;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using Xunit;

namespace RustCompanion.Tests;

public class NetTests
{
    private static byte[] InfoPacket(string name, string map, int players, int max)
    {
        var l = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17 };
        foreach (var s in new[] { name, map, "rust", "Rust" }) { l.AddRange(Encoding.UTF8.GetBytes(s)); l.Add(0); }
        l.AddRange(new byte[] { 0x00, 0x00, (byte)players, (byte)max, 0 });
        return l.ToArray();
    }

    [Fact]
    public void Parser_ReadsInfo()
    {
        var i = A2sParser.ParseInfo(InfoPacket("My Server", "Procedural Map", 42, 100))!;
        Assert.Equal(("My Server", "Procedural Map", 42, 100), (i.Name, i.Map, i.Players, i.MaxPlayers));
    }

    [Fact] public void Parser_Truncated_ReturnsNull() => Assert.Null(A2sParser.ParseInfo(InfoPacket("a", "b", 1, 2).Take(8).ToArray()));
    [Fact] public void Parser_WrongHeader_ReturnsNull() => Assert.Null(A2sParser.ParseInfo(new byte[] { 1, 2, 3, 4, 5, 6, 7 }));
    [Fact] public void Parser_DetectsChallenge() =>
        Assert.True(A2sParser.IsChallenge(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 1, 0, 0, 0 }, out var c) && c == 1);

    [Fact]
    public async Task A2s_NoReply_IsNoResponse_NotOffline()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var c = new A2sClient(new SettingsProvider { }.With(s => s.RequestTimeoutSeconds = 1), new RateLimiter());
        var r = await c.QueryAsync("127.0.0.1", port, default);
        Assert.Equal(SourceState.NoResponse, r.State);
    }

    [Fact]
    public async Task A2s_FakeServer_ChallengeThenInfo()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var task = Task.Run(async () =>
        {
            var first = await server.ReceiveAsync();
            await server.SendAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 9, 0, 0, 0 }, first.RemoteEndPoint);
            var second = await server.ReceiveAsync();
            await server.SendAsync(InfoPacket("Fake", "Map", 5, 50), second.RemoteEndPoint);
        });
        var c = new A2sClient(new SettingsProvider().With(s => s.RequestTimeoutSeconds = 3), new RateLimiter());
        var r = await c.QueryAsync("127.0.0.1", port, default);
        await task;
        Assert.True(r.IsOk); Assert.Equal(5, r.Value!.Players);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Fn = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Fn(r);
    }

    private static SteamClient Steam(Handler h, bool withKey = true, int timeout = 5)
    {
        var secrets = new MemorySecrets(); if (withKey) secrets.Set(SecretNames.SteamApiKey, "SECRETKEY123");
        return new SteamClient(new HttpClient(h), secrets, new SettingsProvider().With(s => { s.RequestTimeoutSeconds = timeout; s.SteamMinIntervalMs = 200; }), new RateLimiter());
    }
    private static HttpResponseMessage Json(string j) => new(HttpStatusCode.OK) { Content = new StringContent(j) };
    private const string Id = "76561198000000000";

    [Fact]
    public async Task Steam_InRust_PublicProfile()
    {
        var h = new Handler { Fn = _ => Task.FromResult(Json("{\"response\":{\"players\":[{\"personaname\":\"A\",\"personastate\":1,\"communityvisibilitystate\":3,\"gameid\":\"252490\",\"gameserverip\":\"1.2.3.4:28015\"}]}}")) };
        var r = await Steam(h).GetStatusAsync(Id, default);
        Assert.True(r.IsOk); Assert.True(r.Value!.InRust); Assert.Equal("1.2.3.4:28015", r.Value.ReportedServer);
    }

    [Fact]
    public async Task Steam_NoGameId_IsUnknown_NotFalse()
    {
        var h = new Handler { Fn = _ => Task.FromResult(Json("{\"response\":{\"players\":[{\"personastate\":1,\"communityvisibilitystate\":3}]}}")) };
        var r = await Steam(h).GetStatusAsync(Id, default);
        Assert.Null(r.Value!.InRust);
    }

    [Fact]
    public async Task Steam_PrivateProfile_Flagged()
    {
        var h = new Handler { Fn = _ => Task.FromResult(Json("{\"response\":{\"players\":[{\"personastate\":0,\"communityvisibilitystate\":1}]}}")) };
        Assert.False((await Steam(h).GetStatusAsync(Id, default)).Value!.ProfilePublic);
    }

    [Theory]
    [InlineData(401, SourceState.AccessDenied)] [InlineData(403, SourceState.AccessDenied)] [InlineData(429, SourceState.RateLimited)] [InlineData(500, SourceState.Error)]
    public async Task Steam_HttpErrors_MapToStates(int code, SourceState expected)
    {
        var h = new Handler { Fn = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)) };
        var r = await Steam(h).GetStatusAsync(Id, default);
        Assert.Equal(expected, r.State); Assert.DoesNotContain("SECRETKEY", r.Message);
    }

    [Fact]
    public async Task Steam_EmptyPlayers_And_BadJson()
    {
        var h = new Handler { Fn = _ => Task.FromResult(Json("{\"response\":{\"players\":[]}}")) };
        Assert.Equal(SourceState.NoData, (await Steam(h).GetStatusAsync(Id, default)).State);
        h.Fn = _ => Task.FromResult(Json("not json"));
        Assert.Equal(SourceState.Error, (await Steam(h).GetStatusAsync(Id, default)).State);
    }

    [Fact]
    public async Task Steam_NoKey_And_BadId_DoNotCallNetwork()
    {
        var called = false;
        var h = new Handler { Fn = _ => { called = true; return Task.FromResult(Json("{}")); } };
        Assert.Equal(SourceState.NotConfigured, (await Steam(h, withKey: false).GetStatusAsync(Id, default)).State);
        Assert.Equal(SourceState.Error, (await Steam(h).GetStatusAsync("abc", default)).State);
        Assert.False(called);
    }

    [Fact]
    public async Task Steam_Timeout_IsNoResponse()
    {
        var secrets = new MemorySecrets(); secrets.Set(SecretNames.SteamApiKey, "K");
        var client = new SteamClient(new HttpClient(new DelayHandler()), secrets, new SettingsProvider().With(s => s.RequestTimeoutSeconds = 1), new RateLimiter());
        Assert.Equal(SourceState.NoResponse, (await client.GetStatusAsync(Id, default)).State);
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { await Task.Delay(TimeSpan.FromSeconds(10), ct); return new HttpResponseMessage(); }
    }
}

internal static class SettingsExt
{
    public static SettingsProvider With(this SettingsProvider p, Action<AppSettings> f) { var s = new AppSettings(); f(s); p.Update(s); return p; }
}
