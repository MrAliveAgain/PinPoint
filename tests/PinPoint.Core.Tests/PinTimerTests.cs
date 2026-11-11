using PinPoint.Core.Timing;

namespace PinPoint.Core.Tests;

public class PinTimerTests
{
    private TimeSpan _now;

    private PinTimer Create() => new(() => _now);

    private void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Stopwatch_counts_up_and_pauses()
    {
        var t = Create();
        t.Start();
        Advance(65);
        Assert.Equal("1:05", t.DisplayText);
        t.Pause();
        Advance(100);
        Assert.Equal("1:05", t.DisplayText);
        t.Start();
        Advance(3600);
        Assert.Equal("1:01:05", t.DisplayText);
    }

    [Fact]
    public void Countdown_rounds_up_and_finishes_once_at_zero()
    {
        var t = Create();
        t.SetDuration(TimeSpan.FromSeconds(10));
        Assert.Equal("0:10", t.DisplayText);
        t.Start();
        Advance(9.2);
        Assert.Equal("0:01", t.DisplayText);
        Assert.False(t.CheckFinished());
        Advance(1);
        Assert.True(t.CheckFinished());
        Assert.False(t.CheckFinished());
        Assert.False(t.IsRunning);
        Assert.Equal("0:00", t.DisplayText);
    }

    [Fact]
    public void Starting_a_finished_countdown_starts_it_over()
    {
        var t = Create();
        t.SetDuration(TimeSpan.FromSeconds(5));
        t.Start();
        Advance(6);
        t.CheckFinished();
        t.Start();
        Assert.Equal("0:05", t.DisplayText);
    }

    [Fact]
    public void Wheel_adjusts_only_before_the_countdown_starts()
    {
        var t = Create();
        t.SetDuration(TimeSpan.FromMinutes(5));
        t.AdjustDuration(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(6), t.Duration);
        t.AdjustDuration(TimeSpan.FromMinutes(-10));
        Assert.Equal(TimeSpan.FromSeconds(1), t.Duration);
        t.Start();
        Advance(0.5);
        t.AdjustDuration(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromSeconds(1), t.Duration);
    }
}
