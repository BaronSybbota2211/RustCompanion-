using RustCompanion.Core.Models;
using RustCompanion.Core.Services;
using Xunit;

namespace RustCompanion.Tests;

public class TimerTests
{
    private static TimerEntry T(DateTimeOffset? start, int interval = 1200, int lead = 300) =>
        new() { Id = 1, Name = "t", StartedAtUtc = start, IntervalSeconds = interval, LeadSeconds = lead };

    [Fact] public void NotStarted_IsIdle() => Assert.Equal(TimerPhase.Idle, TimerCalculator.Phase(T(null), DateTimeOffset.UtcNow));

    [Fact]
    public void Phases_Progress()
    {
        var s = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t = T(s);
        Assert.Equal(TimerPhase.Running, TimerCalculator.Phase(t, s.AddMinutes(5)));
        Assert.Equal(TimerPhase.LeadWindow, TimerCalculator.Phase(t, s.AddMinutes(16)));
        Assert.Equal(TimerPhase.Due, TimerCalculator.Phase(t, s.AddMinutes(20)));
    }

    [Fact]
    public void CrossesMidnight_And_TimeZoneIndependent()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 23, 50, 0, TimeSpan.Zero);
        var moscow = new DateTimeOffset(2026, 1, 2, 2, 50, 0, TimeSpan.FromHours(3)); // тот же момент
        Assert.Equal(TimerCalculator.DueAt(T(utc)), TimerCalculator.DueAt(T(moscow)));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 10, 0, TimeSpan.Zero), TimerCalculator.DueAt(T(utc)));
    }

    [Fact]
    public void Notifications_FireOnce()
    {
        var s = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t = T(s);
        Assert.Empty(NotificationPlanner.Evaluate(t, s.AddMinutes(1)));
        Assert.Single(NotificationPlanner.Evaluate(t, s.AddMinutes(16)));
        Assert.Empty(NotificationPlanner.Evaluate(t, s.AddMinutes(17)));
        var due = NotificationPlanner.Evaluate(t, s.AddMinutes(21));
        Assert.Single(due); Assert.True(due[0].IsDue);
        Assert.Empty(NotificationPlanner.Evaluate(t, s.AddMinutes(22)));
    }

    [Fact]
    public void AppReopenedAfterDue_SendsOnlyDue()
    {
        var s = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var r = NotificationPlanner.Evaluate(T(s), s.AddHours(2));
        Assert.Single(r); Assert.True(r[0].IsDue);
    }

    [Fact]
    public void Disabled_NoNotifications()
    {
        var t = T(DateTimeOffset.UtcNow.AddHours(-1)); t.NotifyEnabled = false;
        Assert.Empty(NotificationPlanner.Evaluate(t, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("", 60, 0)] [InlineData("x", 0, 0)] [InlineData("x", 60, -1)] [InlineData("x", 60, 60)] [InlineData("x", 99999999, 0)]
    public void Validate_RejectsBadInput(string name, int interval, int lead) =>
        Assert.Throws<ArgumentException>(() => TimerService.Validate(name, interval, lead));

    [Fact]
    public async Task Service_StartResetsFlags_AndTickPersistsAndNotifiesOnce()
    {
        var repo = new MemoryTimerRepo(); var clock = new FakeClock(); var n = new FakeNotifier();
        var svc = new TimerService(repo, clock, n);
        var t = await svc.CreateAsync(TrackerKind.Monument, "Airfield", 1200, 300);
        await svc.StartAsync(t);
        clock.Advance(TimeSpan.FromMinutes(16));
        await svc.TickAsync(new[] { t }); await svc.TickAsync(new[] { t });
        Assert.Single(n.Sent);
        clock.Advance(TimeSpan.FromMinutes(5));
        await svc.TickAsync(new[] { t });
        Assert.Equal(2, n.Sent.Count);
        await svc.MarkAsync(t, "mark", restart: true);
        Assert.False(t.DueNotified); Assert.NotNull(t.StartedAtUtc);
        await svc.MarkAsync(t, "mark", restart: false);
        Assert.Null(t.StartedAtUtc);
        Assert.Contains(repo.History, h => h.Action == "mark");
    }
}
