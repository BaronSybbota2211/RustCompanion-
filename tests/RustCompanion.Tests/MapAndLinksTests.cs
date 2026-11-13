using RustCompanion.Core.Net;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

public class MapAndLinksTests
{
    [Fact] public void ToPixel_FlipsY_AndAppliesOceanMargin()
    {
        // карта 4000 м, картинка 2000×2000, океан 100 px: рабочая область 1800 px
        var (x0, y0) = RpMapGeometry.ToPixel(0, 0, 4000, 2000, 2000, 100);
        Assert.Equal(100, x0, 3); Assert.Equal(1900, y0, 3);
        var (x1, y1) = RpMapGeometry.ToPixel(4000, 4000, 4000, 2000, 2000, 100);
        Assert.Equal(1900, x1, 3); Assert.Equal(100, y1, 3);
    }

    [Fact] public void ToPixel_ZeroMapSize_DoesNotThrow() => Assert.Equal((0.0, 0.0), RpMapGeometry.ToPixel(10, 10, 0, 100, 100, 0));

    [Fact] public void Labels_CoverEveryKind_AndEventsExcludePlayers()
    {
        foreach (RpMarkerKind k in Enum.GetValues<RpMarkerKind>()) Assert.False(string.IsNullOrWhiteSpace(RpMapGeometry.Label(new RpMapMarker(1, k, 0, 0))));
        Assert.Equal("Deep Sea", RpMapGeometry.Label(new RpMapMarker(1, RpMarkerKind.Unknown, 0, 0, "Deep Sea")));
        Assert.False(RpMapGeometry.IsEvent(RpMarkerKind.Player));
        Assert.True(RpMapGeometry.IsEvent(RpMarkerKind.Unknown)); Assert.True(RpMapGeometry.IsEvent(RpMarkerKind.Radius));
    }

    [Fact] public void ServerDetails_ParsesWipeFromBornTag_AndHidesCounters()
    {
        var info = new A2sInfoResponse(1, "Srv", "Procedural Map", "rust", "Rust", 252490, 40, 100, 0, 'd', 'w', false, true, "2600",
            28015, null, "mp200,cp153,qp4,born1700000000,pve,monthly,v2600", null);
        var d = ServerDetails.From(info, 42);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), d.WipeUtc);
        Assert.Equal(153, d.Players); Assert.Equal(200, d.MaxPlayers); Assert.Equal(4, d.Queue);
        Assert.Contains("pve", d.Tags); Assert.DoesNotContain(d.Tags, t => t.StartsWith("born") || t.StartsWith("cp") || t.StartsWith("mp"));
        var lines = d.Lines(DateTimeOffset.FromUnixTimeSeconds(1700000000).AddDays(3));
        Assert.Contains(lines, l => l.Contains("153/200") && l.Contains("в очереди 4"));
        Assert.Contains(lines, l => l.Contains("42 мс")); Assert.Contains(lines, l => l.Contains("3 дн."));
    }

    [Fact] public void ServerDetails_NoBornTag_SaysUnknownWipe()
    {
        var info = new A2sInfoResponse(1, "S", "", "", "", 0, 1, 2, 0, 'd', 'w', false, false, "", null, null, "mp2", null);
        Assert.Contains(ServerDetails.From(info, 5).Lines(DateTimeOffset.UtcNow), l => l.Contains("не сообщает"));
    }

    [Fact] public void BattleMetricsLogin_RecognizesLoginAndLoggedPages()
    {
        Assert.False(BattleMetricsLogin.IsLoggedInPage("https://www.battlemetrics.com/account/login"));
        Assert.False(BattleMetricsLogin.IsLoggedInPage("https://steamcommunity.com/openid/login?x=1"));
        Assert.False(BattleMetricsLogin.IsLoggedInPage(null));
        Assert.True(BattleMetricsLogin.IsLoggedInPage("https://www.battlemetrics.com/account"));
        Assert.True(BattleMetricsLogin.IsLoggedInPage("https://www.battlemetrics.com/"));
    }

    [Fact] public void RustBansScript_ContainsOnlyValidId()
    {
        var s = RustBansLink.FillScript(" 76561198040070553 ");
        Assert.NotNull(s); Assert.Contains("'76561198040070553'", s);
        Assert.Null(RustBansLink.FillScript("1'; alert(1); '")); Assert.Null(RustBansLink.FillScript(null)); Assert.Null(RustBansLink.FillScript("123"));
    }
}
