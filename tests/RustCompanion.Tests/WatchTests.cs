using System.Text.Json;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Watch;
using Xunit;

namespace RustCompanion.Tests;

public class PresenceMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static PresenceObservation Present(int min) => new(PresenceKind.Present, T0.AddMinutes(min));
    private static PresenceObservation Absent(int min, int? srcMin = null) => new(PresenceKind.Absent, T0.AddMinutes(min), srcMin is null ? null : T0.AddMinutes(srcMin.Value));
    private static PresenceObservation Err(int min) => new(PresenceKind.Unknown, T0.AddMinutes(min), IsError: true);
    private static PresenceObservation Unknown(int min) => new(PresenceKind.Unknown, T0.AddMinutes(min));

    private static MonitorState Online()
    {
        var s = new MonitorState { WatchId = 1 };
        PresenceMonitor.Apply(s, Present(0));
        return s;
    }

    [Fact] public void Present_SetsOnServer_ResetsCounter()
    {
        var s = Online(); PresenceMonitor.Apply(s, Absent(1)); PresenceMonitor.Apply(s, Present(2));
        Assert.Equal(PresenceStatus.OnServer, s.Status); Assert.Equal(0, s.AbsenceCount);
    }

    [Fact] public void FirstAndSecondAbsence_NoNotification()
    {
        var s = Online();
        Assert.Empty(PresenceMonitor.Apply(s, Absent(1)).Events);
        Assert.Empty(PresenceMonitor.Apply(s, Absent(2)).Events);
        Assert.Equal(PresenceStatus.NotDetected, s.Status); Assert.Equal(2, s.AbsenceCount);
    }

    [Fact] public void ThirdConsecutiveAbsence_SingleExitEvent()
    {
        var s = Online();
        PresenceMonitor.Apply(s, Absent(1)); PresenceMonitor.Apply(s, Absent(2));
        var o = PresenceMonitor.Apply(s, Absent(3));
        Assert.Single(o.Events); Assert.Equal(WatchEventKind.Exit, o.Events[0].Kind); Assert.Equal(3, o.Events[0].Confirmations);
        Assert.Equal(PresenceStatus.ExitConfirmed, s.Status);
        Assert.Empty(PresenceMonitor.Apply(s, Absent(4)).Events); // не повторяется
        Assert.Empty(PresenceMonitor.Apply(s, Absent(5)).Events);
    }

    [Fact] public void Detected_BetweenChecks_ResetsCounter_NoExit()
    {
        var s = Online();
        PresenceMonitor.Apply(s, Absent(1)); PresenceMonitor.Apply(s, Absent(2));
        var back = PresenceMonitor.Apply(s, Present(3));
        Assert.Empty(back.Events); Assert.Equal(0, s.AbsenceCount); // не «вход»: выход не был подтверждён
        PresenceMonitor.Apply(s, Absent(4)); PresenceMonitor.Apply(s, Absent(5));
        Assert.Equal(PresenceStatus.NotDetected, s.Status);
    }

    [Fact] public void Errors_DoNotIncrementCounter()
    {
        var s = Online();
        PresenceMonitor.Apply(s, Absent(1)); PresenceMonitor.Apply(s, Absent(2));
        var o = PresenceMonitor.Apply(s, Err(3));
        Assert.Empty(o.Events); Assert.Equal(2, s.AbsenceCount); Assert.Equal(PresenceStatus.CheckError, s.Status);
        Assert.Empty(PresenceMonitor.Apply(s, Err(4)).Events);
        Assert.Equal(2, s.AbsenceCount);
    }

    [Fact] public void UnknownResponse_IsNoData_NotAbsence()
    {
        var s = Online();
        for (var i = 1; i <= 5; i++) Assert.Empty(PresenceMonitor.Apply(s, Unknown(i)).Events);
        Assert.Equal(0, s.AbsenceCount); Assert.Equal(PresenceStatus.NoData, s.Status);
    }

    [Fact] public void StaleSourceData_NotCountedAsIndependentConfirmations()
    {
        var s = Online();
        PresenceMonitor.Apply(s, Absent(1, srcMin: 1));
        for (var i = 2; i <= 6; i++) PresenceMonitor.Apply(s, Absent(i, srcMin: 1)); // тот же timestamp источника
        Assert.Equal(1, s.AbsenceCount); Assert.NotEqual(PresenceStatus.ExitConfirmed, s.Status);
        PresenceMonitor.Apply(s, Absent(7, srcMin: 7)); PresenceMonitor.Apply(s, Absent(8, srcMin: 8));
        Assert.Equal(PresenceStatus.ExitConfirmed, s.Status);
    }

    [Fact] public void NewSessionAfterConfirmedExit_ChangesEpisode_AndEmitsJoin_NewExitKeyDiffers()
    {
        var s = Online();
        for (var i = 1; i <= 3; i++) PresenceMonitor.Apply(s, Absent(i));
        var firstKey = "1:0:exit";
        var join = PresenceMonitor.Apply(s, Present(10));
        Assert.Single(join.Events); Assert.Equal(WatchEventKind.Join, join.Events[0].Kind);
        Assert.Equal(PresenceStatus.OnServer, s.Status); Assert.False(s.ExitNotified);
        PresenceMonitor.Apply(s, Absent(11)); PresenceMonitor.Apply(s, Absent(12));
        var exit2 = PresenceMonitor.Apply(s, Absent(13));
        Assert.NotEqual(firstKey, exit2.Events[0].EventKey);
    }

    [Fact] public void NeverSeenPlayer_CannotHaveExit()
    {
        var s = new MonitorState { WatchId = 5 };
        for (var i = 1; i <= 5; i++) Assert.Empty(PresenceMonitor.Apply(s, Absent(i)).Events);
        Assert.NotEqual(PresenceStatus.ExitConfirmed, s.Status);
    }

    [Fact] public void Untimestamped_Absence_CanBeDisabled()
    {
        var s = Online();
        for (var i = 1; i <= 4; i++) PresenceMonitor.Apply(s, Absent(i), 3, allowUntimestampedAbsence: false);
        Assert.Equal(0, s.AbsenceCount);
    }

    [Fact] public void Duration_OnlyWithBothTimestamps()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), ActivityStats.Duration(T0, T0.AddMinutes(90)));
        Assert.Null(ActivityStats.Duration(T0, null)); Assert.Null(ActivityStats.Duration(null, T0));
        Assert.Null(ActivityStats.Duration(T0, T0.AddMinutes(-1)));
        Assert.Null(ActivityStats.ActiveElapsed(null, T0));
    }

    [Fact] public void DailyHours_SplitsAcrossMidnight()
    {
        var tz = TimeZoneInfo.Utc;
        var s = new[] { new PresenceSession(new DateTimeOffset(2026, 10, 20, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero), null) };
        var d = ActivityStats.DailyHours(s, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), 3, tz);
        Assert.Equal(1.0, d[new DateOnly(2026, 10, 20)], 3); Assert.Equal(2.0, d[new DateOnly(2026, 10, 21)], 3);
    }

    [Theory]
    [InlineData(1, 2, false, 0, false, 0)] [InlineData(2, 2, false, 0, true, 1)] [InlineData(3, 2, true, 1, false, 1)] [InlineData(1, 2, true, 1, false, 1)] [InlineData(5, 0, false, 0, false, 0)]
    public void GroupThreshold_CrossesOncePerRise(int online, int thr, bool was, int ep, bool crossed, int newEp)
    {
        var r = GroupThreshold.Evaluate(online, thr, was, ep);
        Assert.Equal(crossed, r.Crossed); Assert.Equal(newEp, r.Episode);
    }
}
