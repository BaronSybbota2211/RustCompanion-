using RustCompanion.Core.Notifications;
using RustCompanion.Core.Overlay;
using Xunit;

namespace RustCompanion.Tests;

public class OverlayMonitorTests
{
    // Монитор A (основной) 1920×1080 слева, монитор B 2560×1440 справа; рабочие области без панели задач
    private static readonly MonitorRect A = new(new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), true);
    private static readonly MonitorRect B = new(new RectD(1920, 0, 2560, 1440), new RectD(1920, 0, 2560, 1400), false);
    private static readonly MonitorRect[] Both = { A, B };

    [Fact] public void Rust_OnSecondMonitor_IsPicked()
    {
        Assert.Equal(1, OverlayTarget.PickMonitor(Both, new RectD(1920, 0, 2560, 1440)));         // безрамочный на мониторе B
        Assert.Equal(0, OverlayTarget.PickMonitor(Both, new RectD(100, 100, 1280, 720)));         // оконный на A
    }

    [Fact] public void Rust_SpanningTwoMonitors_PicksLargerPart()
    {
        Assert.Equal(1, OverlayTarget.PickMonitor(Both, new RectD(1500, 100, 1600, 900)));        // 420 px на A, 1180 px на B
        Assert.Equal(0, OverlayTarget.PickMonitor(Both, new RectD(900, 100, 1600, 900)));         // 1020 px на A, 580 px на B
    }

    [Fact] public void NoRustWindow_OrOffscreen_NoMonitorChosen()
    {
        Assert.Equal(-1, OverlayTarget.PickMonitor(Both, null));
        Assert.Equal(-1, OverlayTarget.PickMonitor(Both, new RectD(-32000, -32000, 160, 28)));    // свёрнутое окно
    }

    [Fact] public void NoSavedPosition_RustOnB_UsesCornerOfMonitorB()
    {
        var r = OverlayTarget.Resolve(null, null, 320, 120, Both, new RectD(1920, 0, 2560, 1440), OverlayCorner.TopRight, 24);
        Assert.Equal(1, r.MonitorIndex); Assert.False(r.UsedSaved);
        Assert.Equal((4480 - 320 - 24, 24), (r.X, r.Y));                                          // правый верхний угол рабочей области B
    }

    [Fact] public void SavedPositionOnA_RustOnB_OverlayMovesToB()
    {
        var r = OverlayTarget.Resolve(1500, 100, 320, 120, Both, new RectD(1920, 0, 2560, 1440), OverlayCorner.TopLeft, 20);
        Assert.False(r.UsedSaved); Assert.Equal(1, r.MonitorIndex); Assert.Equal((1920 + 20, 20), (r.X, r.Y)); Assert.Contains("перенесён", r.Reason);
    }

    [Fact] public void SavedPositionOnRustMonitor_IsKept()
    {
        var r = OverlayTarget.Resolve(2500, 300, 320, 120, Both, new RectD(1920, 0, 2560, 1440), OverlayCorner.TopRight, 24);
        Assert.True(r.UsedSaved); Assert.Equal((2500, 300), (r.X, r.Y));
    }

    [Fact] public void RustNotFound_FallsBackToSavedOrPrimary()
    {
        var saved = OverlayTarget.Resolve(2500, 300, 320, 120, Both, null, OverlayCorner.TopRight, 24);
        Assert.True(saved.UsedSaved); Assert.Equal((2500, 300), (saved.X, saved.Y));
        var none = OverlayTarget.Resolve(null, null, 320, 120, Both, null, OverlayCorner.TopRight, 24);
        Assert.Equal(0, none.MonitorIndex); Assert.Equal((1920 - 320 - 24, 24), (none.X, none.Y));                        // основной монитор
    }

    [Fact] public void SavedPositionOnRemovedMonitor_NeverStaysOffscreen()
    {
        var only = new[] { A };
        var r = OverlayTarget.Resolve(3000, 100, 320, 120, only, null, OverlayCorner.TopLeft, 10);
        Assert.False(r.UsedSaved); Assert.Equal((10, 10), (r.X, r.Y));
        Assert.Equal(-1, OverlayTarget.Resolve(null, null, 320, 120, Array.Empty<MonitorRect>(), null, OverlayCorner.TopLeft, 10).MonitorIndex);
    }

    [Fact] public void RustMovedBetweenMonitors_PlacementFollows_AndPrimaryIsNotAlwaysFirst()
    {
        var b = new MonitorRect(new RectD(-1920, 0, 1920, 1080), new RectD(-1920, 0, 1920, 1040), false);     // второй монитор слева (отрицательные координаты)
        var monitors = new[] { b, A };
        var onLeft = OverlayTarget.Resolve(null, null, 320, 120, monitors, new RectD(-1900, 50, 1280, 720), OverlayCorner.BottomLeft, 20);
        Assert.Equal(0, onLeft.MonitorIndex); Assert.Equal((-1920 + 20, 1040 - 120 - 20), (onLeft.X, onLeft.Y));
        var onPrimary = OverlayTarget.Resolve(null, null, 320, 120, monitors, new RectD(100, 50, 1280, 720), OverlayCorner.BottomLeft, 20);
        Assert.Equal(1, onPrimary.MonitorIndex);
        Assert.Equal(1, OverlayTarget.Resolve(null, null, 320, 120, monitors, null, OverlayCorner.TopLeft, 10).MonitorIndex);   // без Rust — основной (не первый в списке)
    }

    [Fact] public void FollowRust_DefaultsOn()
    {
        Assert.True(new RustCompanion.Core.Models.AppSettings().OverlayFollowRust);
    }
}
