using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

public class SteamLiveTests
{
    private static SteamPlayerStatus St(string? server, bool? inRust = true, bool pub = true, int persona = 1) => new("76561198000000001", "nick", persona, pub, inRust, server, DateTimeOffset.UtcNow);

    [Fact] public void Match_GamePortOrQueryPort_IsThisServer()
    {
        var ips = new[] { "1.2.3.4" };
        Assert.Equal(SteamServerMatch.OnThisServer, SteamMatch.Evaluate(St("1.2.3.4:28015"), ips, 28016, 28015));   // Steam отдаёт игровой порт, хранится порт запросов
        Assert.Equal(SteamServerMatch.OnThisServer, SteamMatch.Evaluate(St("1.2.3.4:28016"), ips, 28016, null));
        Assert.Equal(SteamServerMatch.OnOtherServer, SteamMatch.Evaluate(St("1.2.3.4:30000"), ips, 28016, 28015));
        Assert.Equal(SteamServerMatch.OnOtherServer, SteamMatch.Evaluate(St("9.9.9.9:28015"), ips, 28016, 28015));
        Assert.Equal(SteamServerMatch.Inconclusive, SteamMatch.Evaluate(St(null), ips, 28016, 28015));
        Assert.Equal(SteamServerMatch.NotChecked, SteamMatch.Evaluate(null, ips, 28016, 28015));
    }

    [Fact] public void Match_ResolvedDnsAddress_Works() =>
        Assert.Equal(SteamServerMatch.OnThisServer, SteamMatch.Evaluate(St("5.6.7.8:28015"), new[] { "play.example.com", "5.6.7.8" }, 28016, 28015));

    [Fact] public void Classify_Variants()
    {
        Assert.Equal(LivePresence.OnServer, SteamLiveMonitor.Classify(St("1.1.1.1:1"), SteamServerMatch.OnThisServer));
        Assert.Equal(LivePresence.OtherServer, SteamLiveMonitor.Classify(St("1.1.1.1:1"), SteamServerMatch.OnOtherServer));
        Assert.Equal(LivePresence.InRustServerUnknown, SteamLiveMonitor.Classify(St(null, true), SteamServerMatch.Inconclusive));
        Assert.Equal(LivePresence.NotInRust, SteamLiveMonitor.Classify(St(null, false), SteamServerMatch.Inconclusive));
        Assert.Equal(LivePresence.OnlineGameHidden, SteamLiveMonitor.Classify(St(null, null, true, 1), SteamServerMatch.Inconclusive));
        Assert.Equal(LivePresence.NotInRust, SteamLiveMonitor.Classify(St(null, null, true, 0), SteamServerMatch.Inconclusive));
        Assert.Equal(LivePresence.Hidden, SteamLiveMonitor.Classify(St(null, null, false, 0), SteamServerMatch.Inconclusive));
        Assert.Equal(LivePresence.Unknown, SteamLiveMonitor.Classify(null, SteamServerMatch.NotChecked));
    }

    [Fact] public void ParseMany_ReadsAllPlayers()
    {
        var json = "{\"response\":{\"players\":[{\"steamid\":\"76561198000000001\",\"personaname\":\"A\",\"personastate\":1,\"communityvisibilitystate\":3,\"gameid\":\"252490\",\"gameserverip\":\"1.2.3.4:28015\"},"
                 + "{\"steamid\":\"76561198000000002\",\"personaname\":\"B\",\"personastate\":0,\"communityvisibilitystate\":1}]}}";
        var l = SteamClient.ParseMany(json);
        Assert.Equal(2, l.Count);
        Assert.True(l[0].InRust); Assert.Equal("1.2.3.4:28015", l[0].ReportedServer);
        Assert.Null(l[1].InRust); Assert.False(l[1].ProfilePublic);
    }

    [Fact] public void BmLookupScript_OnlyValidId()
    {
        Assert.Contains("76561198040070553", BattleMetricsLink.LookupScript("76561198040070553"));
        Assert.Null(BattleMetricsLink.LookupScript("x'); alert(1)//"));
        Assert.Equal("https://www.battlemetrics.com/players/123", BattleMetricsLink.PlayerPage("123"));
        Assert.DoesNotContain("sort=", BattleMetricsLink.ForSteamId("76561198040070553"));
    }

    [Fact] public void Interval_IsThreeSeconds() => Assert.Equal(TimeSpan.FromSeconds(3), SteamLiveMonitor.Interval);
}
