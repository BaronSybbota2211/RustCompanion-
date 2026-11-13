using RustCompanion.Core.Notifications;
using Xunit;

namespace RustCompanion.Tests;

/// <summary>Карточки Overlay: группы оформления по идентификатору события, разбор «заголовок: текст», данные в очереди.</summary>
public class OverlayToastTests
{
    private static readonly DateTimeOffset N = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("rustplus.alarm", ToastCategory.Alarm)]
    [InlineData("game.alarm", ToastCategory.Alarm)]
    [InlineData("timer.heli", ToastCategory.Timer)]
    [InlineData("timer.monument", ToastCategory.Timer)]
    [InlineData("container", ToastCategory.Timer)]
    [InlineData("rustplus.connection", ToastCategory.Warning)]
    [InlineData("game.device_unavailable", ToastCategory.Warning)]
    [InlineData("watch.exit", ToastCategory.Warning)]
    [InlineData("game.player_exit", ToastCategory.Warning)]
    [InlineData("forecast.chinook", ToastCategory.Forecast)]
    [InlineData("bradley.respawn", ToastCategory.Forecast)]
    [InlineData("decay", ToastCategory.Forecast)]
    [InlineData("game.device_restored", ToastCategory.Restored)]
    [InlineData("hotkey", ToastCategory.Info)]
    [InlineData("test", ToastCategory.Info)]
    [InlineData("что-то-новое", ToastCategory.Info)]
    public void CategoryOf_MatchesEventId(string id, ToastCategory expected) => Assert.Equal(expected, OverlayToastKinds.CategoryOf(id));

    [Fact]
    public void Split_RouterFormat_GivesTitleAndBody()
    {
        var (t, b) = OverlayToastKinds.Split("decay", "Rust Companion — Гниение: база распадётся через 10 минут");
        Assert.Equal("Гниение", t); Assert.Equal("база распадётся через 10 минут", b);
    }

    [Fact]
    public void Split_GameEvent_UsesKindTitle()
    {
        var (t, b) = OverlayToastKinds.Split(GameNotifyKind.Alarm, "Rust Companion — Сработала сигнализация на базе");
        Assert.Equal(GameNotifyKind.Title(GameNotifyKind.Alarm), t); Assert.Equal("Сработала сигнализация на базе", b);
    }

    [Fact]
    public void Split_UnknownFormat_KeepsWholeTextAsBody()
    {
        var (t, b) = OverlayToastKinds.Split("x", "просто текст");
        Assert.Equal("Rust Companion", t); Assert.Equal("просто текст", b);
    }

    [Fact]
    public void Queue_ItemsKeepKindTitleBodyAndFirstArrivalTime()
    {
        var q = new OverlayQueue(3);
        q.Add("full", N, TimeSpan.FromSeconds(8), "decay", "Гниение", "через 10 минут");
        q.Add("full", N.AddSeconds(4), TimeSpan.FromSeconds(8), "decay", "Гниение", "через 10 минут");     // повтор: показ продлён, карточка та же
        var items = q.VisibleItems(N.AddSeconds(10));                                                         // 8 с от первого показа уже прошли, но повтор продлил до 12 с
        var it = Assert.Single(items);
        Assert.Equal(("decay", "Гниение", "через 10 минут", N), (it.Kind, it.Title, it.Body, it.At));
        Assert.Empty(q.VisibleItems(N.AddSeconds(13)));
    }

    private sealed class Spy : IOverlaySink { public List<string> Shown = new(); public void Show(string id, string t) => Shown.Add(id + "|" + t); }

    [Fact]
    public void Sink_WithoutTitleSupport_GetsLegacyCombinedText()
    {
        IOverlaySink sink = new Spy();
        sink.Show("decay", "Гниение", "через 10 минут");
        Assert.Equal("decay|Rust Companion — Гниение: через 10 минут", Assert.Single(((Spy)sink).Shown));
    }
}
