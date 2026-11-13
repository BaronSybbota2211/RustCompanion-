using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;
using Xunit;

namespace RustCompanion.Tests;

/// <summary>Выбор маршрута подключения к Rust+ (новый канал /socket/{ServerId} → прямое подключение), разбор отказов и защита токенов. Сеть не нужна.</summary>
public class RustPlusRoutesTests
{
    private const string Id = "7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40";
    private static RpCredentials Cred(string? id = Id, string? token = "login-token-value-123456") => new("15.204.222.24", 28082, 76561198000000000UL, 123456, id, token);
    private static RpResult Handshake(int code) => RpResult.Fail("WebSocketException", $"The server returned status code '{code}' when status code '101' was expected.");
    private static readonly RpResult Unreachable = RpResult.Fail("HttpRequestException", "Unable to connect to the remote server");

    [Fact] public void CompanionUri_And_SubProtocols_FollowTheDocumentedShape()
    {
        Assert.Equal($"wss://companion-rust.facepunch.com/socket/{Id}", RustPlusRoutes.CompanionUri(Guid.Parse(Id)).ToString());
        Assert.Equal(new[] { "rustplus.auth.v1", "dGVzdC10b2tlbg" }, RustPlusRoutes.SubProtocols("test-token"));
        Assert.Equal("Pz8_", RustPlusRoutes.ToBase64Url("???"));        // «+» → «-», «/» → «_», без «=»
        Assert.Equal("Pj4-", RustPlusRoutes.ToBase64Url(">>>"));
        Assert.DoesNotContain("=", RustPlusRoutes.ToBase64Url("a"));
    }

    [Fact] public void Plan_WithServerIdAndToken_TriesCompanionThenDirect()
    {
        var p = RustPlusRoutes.Plan(Cred());
        Assert.Equal(new[] { RpRouteKind.CompanionSocket, RpRouteKind.Direct }, p.Routes.Select(r => r.Kind).ToArray());
        Assert.Null(p.CompanionMissing); Assert.True(p.HasCompanion);
    }

    [Fact] public void Plan_WithoutServerId_OrToken_OrEmptyGuid_FallsBackToDirect_AndSaysWhatIsMissing()
    {
        var noId = RustPlusRoutes.Plan(Cred(id: null)); Assert.Equal(new[] { RpRouteKind.Direct }, noId.Routes.Select(r => r.Kind).ToArray()); Assert.Contains("ID сервера", noId.CompanionMissing); Assert.DoesNotContain("токена входа", noId.CompanionMissing);
        var noToken = RustPlusRoutes.Plan(Cred(token: null)); Assert.False(noToken.HasCompanion); Assert.Contains("токена входа", noToken.CompanionMissing); Assert.DoesNotContain("ID сервера", noToken.CompanionMissing);
        Assert.False(RustPlusRoutes.Plan(Cred(token: "  ")).HasCompanion);
        Assert.False(RustPlusRoutes.Plan(Cred(id: Guid.Empty.ToString())).HasCompanion);
        Assert.False(RustPlusRoutes.Plan(Cred(id: "not-a-guid")).HasCompanion);
        var both = RustPlusRoutes.Plan(Cred(id: null, token: null)); Assert.Contains("ID сервера", both.CompanionMissing); Assert.Contains("токена входа", both.CompanionMissing);
    }

    [Fact] public void HandshakeStatus_ParsesRealMessages()
    {
        Assert.Equal(418, RustPlusRoutes.HandshakeStatus("The server returned status code '418' when status code '101' was expected."));
        Assert.Equal(403, RustPlusRoutes.HandshakeStatus("The server returned status code '403' when status code '101' was expected."));
        Assert.Equal(404, RustPlusRoutes.HandshakeStatus("x status code '404' y"));
        Assert.Null(RustPlusRoutes.HandshakeStatus("Unable to connect to the remote server")); Assert.Null(RustPlusRoutes.HandshakeStatus(null));
        Assert.Null(RustPlusRoutes.HandshakeStatus("entity 1234187 not found"));
    }

    private static RpResult Sum(RpCredentials c, params (RpRouteKind Kind, RpResult R)[] attempts)
    {
        var plan = RustPlusRoutes.Plan(c);
        return RustPlusRoutes.Summarize(c, plan, attempts.Select(a => (new RpRoute(a.Kind, a.Kind.ToString()), a.R)).ToList(), null);
    }

    [Fact] public void Summarize_418_OnEitherRoute_IsDirectRemoved_WithAddress_AndNotAuth()
    {
        var onDirect = Sum(Cred(id: null, token: null), (RpRouteKind.Direct, Handshake(418)));
        Assert.Equal(RpErrors.DirectRemoved, onDirect.Code); Assert.Contains("HTTP 418", onDirect.Message); Assert.Contains("15.204.222.24:28082", onDirect.Message);
        Assert.False(RpErrors.IsAuth(onDirect)); Assert.True(RpErrors.IsDirectRemoved(onDirect)); Assert.DoesNotContain("заново", onDirect.Message);
        var onCompanion = Sum(Cred(), (RpRouteKind.CompanionSocket, Handshake(418)), (RpRouteKind.Direct, Unreachable));
        Assert.Equal(RpErrors.DirectRemoved, onCompanion.Code);
    }

    [Fact] public void Summarize_403_OnCompanion_IsLoginRejected_NotDirectRemoved_NotAuth()
    {
        var r = Sum(Cred(), (RpRouteKind.CompanionSocket, Handshake(403)), (RpRouteKind.Direct, Unreachable));
        Assert.Equal(RpErrors.CompanionLoginRejected, r.Code); Assert.Contains("HTTP 403", r.Message); Assert.Contains("Вход через Steam", r.Message);
        Assert.False(RpErrors.IsDirectRemoved(r)); Assert.False(RpErrors.IsAuth(r)); Assert.True(RpErrors.IsCompanionLoginRejected(r));
    }

    [Fact] public void Summarize_404_OnCompanion_IsServerUnknown()
    {
        var r = Sum(Cred(), (RpRouteKind.CompanionSocket, Handshake(404)), (RpRouteKind.Direct, Unreachable));
        Assert.Equal(RpErrors.CompanionServerUnknown, r.Code); Assert.Contains("HTTP 404", r.Message); Assert.Contains("15.204.222.24:28082", r.Message); Assert.False(RpErrors.IsAuth(r));
    }

    [Fact] public void Summarize_DirectUnreachable_WithoutCompanionData_ExplainsWhatToDo_AndIsNotAuth()
    {
        var r = Sum(Cred(id: null, token: null), (RpRouteKind.Direct, Unreachable));
        Assert.Equal(RpErrors.CompanionNotReady, r.Code); Assert.Contains("ID сервера", r.Message); Assert.Contains("токена входа", r.Message); Assert.Contains("Unable to connect", r.Message);
        Assert.False(RpErrors.IsDirectRemoved(r));
        Assert.DoesNotContain("токен мог устареть", r.Message);
    }

    [Fact] public void Summarize_Generic_KeepsLastError_AndPortHint_WithoutBlamingTheToken()
    {
        var c = Cred(); var plan = RustPlusRoutes.Plan(c);
        var closed = RustPlusRoutes.Summarize(c, plan, new[] { (plan.Routes[0], RpResult.Fail("timeout", "нет ответа за 20 с")), (plan.Routes[1], Unreachable) }, "connection refused");
        Assert.Equal("HttpRequestException", closed.Code); Assert.Contains("Unable to connect", closed.Message); Assert.Contains("недоступен", closed.Message); Assert.DoesNotContain("токен мог устареть", closed.Message);
        var open = RustPlusRoutes.Summarize(c, plan, new[] { (plan.Routes[1], RpResult.Fail("timeout", "нет ответа")) }, null);
        Assert.Contains("доступен, но Rust+ не ответил", open.Message); Assert.DoesNotContain("токен мог устареть", open.Message);
    }

    [Fact] public void Summarize_NeverContainsTheTokens()
    {
        var c = Cred();
        var all = new[] { Sum(c, (RpRouteKind.CompanionSocket, Handshake(403))), Sum(c, (RpRouteKind.CompanionSocket, Handshake(404))), Sum(c, (RpRouteKind.Direct, Handshake(418))), Sum(c, (RpRouteKind.Direct, Unreachable)) };
        foreach (var r in all) { Assert.DoesNotContain(c.CompanionToken!, r.Message); Assert.DoesNotContain(RustPlusRoutes.ToBase64Url(c.CompanionToken!), r.Message); Assert.DoesNotContain("123456", r.Message); }
    }
}

/// <summary>Токены (токен входа Facepunch, токен игрока) не попадают в журнал и сообщения об ошибках.</summary>
public class RustPlusSecretsLeakTests
{
    private const string Login = "login-token-value-123456";

    [Fact] public void Credentials_ToString_HidesBothTokens()
    {
        var text = new RpCredentials("1.2.3.4", 28082, 76561198000000000UL, 987654321, "7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40", Login).ToString();
        Assert.DoesNotContain(Login, text); Assert.DoesNotContain("987654321", text); Assert.Contains("***", text); Assert.Contains("1.2.3.4", text);
        Assert.DoesNotContain("login-token", new RpCredentials("1.2.3.4", 1, 1, 5).ToString());
    }

    [Fact] public void Redactor_MasksRegisteredToken_InAnyForm_AndPrefixedParameterNames()
    {
        Redactor.Register(Login);
        Assert.DoesNotContain(Login, Redactor.Redact($"connect failed for {Login}"));
        Assert.DoesNotContain(RustPlusRoutes.ToBase64Url(Login), Redactor.Redact("Sec-WebSocket-Protocol: rustplus.auth.v1, " + RustPlusRoutes.ToBase64Url(Login)));
        Assert.DoesNotContain("SECRETVALUE", Redactor.Redact("AuthToken=SECRETVALUE&x=1"));
        Assert.DoesNotContain("SECRETVALUE", Redactor.Redact("PlayerToken=SECRETVALUE"));
        Assert.Equal("GET /x?key=***&steamids=1", Redactor.Redact("GET /x?key=ABCDEF123&steamids=1"));
        Assert.Equal("ok 12345", Redactor.Redact("ok 12345"));                                    // короткие числа не маскируются
        Assert.Equal("", Redactor.Redact(null));
    }

    [Fact] public void Diagnostics_NeverStoresTheRegisteredToken()
    {
        Redactor.Register(Login);
        var d = new DiagnosticsService();
        d.Record("Rust+", SourceState.Error, $"ConnectAsync failed: {Login} / {RustPlusRoutes.ToBase64Url(Login)}");
        Assert.All(d.Snapshot(), x => { Assert.DoesNotContain(Login, x.Message); Assert.DoesNotContain(RustPlusRoutes.ToBase64Url(Login), x.Message); });
    }

    [Fact] public async Task Manager_LogsGatewayErrors_WithoutTheToken()
    {
        const string token = "another-login-token-9988776655";
        var diag = new DiagnosticsService();
        var db = new RustCompanion.Core.Data.Database(TempDb.NewPath()); db.Initialize();
        var secrets = new MemorySecrets(); var servers = new PairedServerRepository(db, secrets);
        var srv = await servers.UpsertAsync("S", "1.2.3.4", 28082, 76561198000000000UL, 111, DateTimeOffset.UtcNow, rustPlusId: "7d3e1e42-1c9f-4f0e-a6a4-49b82f58de40");
        servers.SetCompanionToken(token);
        var gw = new FakeGateway { ConnectResult = RpResult.Fail("WebSocketException", $"handshake failed, protocols: rustplus.auth.v1, {RustPlusRoutes.ToBase64Url(token)} and {token}") };
        var settings = new SettingsProvider(); settings.Update(new AppSettings { RustPlusPollSeconds = 5 });
        var m = new RustPlusManager(() => gw, servers, new FakeClock(), new FakeNotifier(), settings, diag, async (t, ct) => await Task.Delay(Timeout.Infinite, ct));
        await m.ConnectAsync(srv.Id);
        Assert.True(await RpEnv.WaitFor(() => diag.Snapshot().Count > 0 && m.State == RustPlusState.Reconnecting && m.StatusMessage.StartsWith("Нет соединения")));
        var status = m.StatusMessage;
        await m.DisconnectAsync();
        Assert.All(diag.Snapshot(), x => { Assert.DoesNotContain(token, x.Message); Assert.DoesNotContain(RustPlusRoutes.ToBase64Url(token), x.Message); });
        Assert.DoesNotContain(token, status); Assert.DoesNotContain(RustPlusRoutes.ToBase64Url(token), status);   // и в статусе, который видит пользователь
    }
}
