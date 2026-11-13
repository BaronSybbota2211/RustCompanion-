using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;
using Xunit;

namespace RustCompanion.Tests;

public class SettingsAndUtilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void Settings_Defaults_AreValid() => Assert.Empty(new AppSettings().Validate());

    [Fact]
    public void Settings_RejectsTooFrequentPolling_AndHttp()
    {
        var s = new AppSettings { ServerPollSeconds = 1, SteamApiBaseUrl = "http://x" };
        Assert.Equal(2, s.Validate().Count);
    }

    [Fact]
    public void Redactor_HidesKeys() =>
        Assert.DoesNotContain("ABCDEF", Redactor.Redact("GET /x?key=ABCDEF123&steamids=1"));

    [Fact]
    public async Task RateLimiter_SpacesRequests()
    {
        var rl = new RateLimiter(); var sw = System.Diagnostics.Stopwatch.StartNew();
        await rl.WaitAsync("k", TimeSpan.FromMilliseconds(200), default);
        await rl.WaitAsync("k", TimeSpan.FromMilliseconds(200), default);
        Assert.True(sw.ElapsedMilliseconds >= 180);
    }
}
