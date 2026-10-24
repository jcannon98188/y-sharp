using System;
using System.Collections.Generic;
using Xunit;
using YSharp.TestKit.Time;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

[LibraryIndependent]
public class VirtualTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Advance_Called_MovesUtcNowAndTimestamp()
    {
        var tp = new VirtualTimeProvider(Start);
        var t0 = tp.GetTimestamp();
        tp.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(Start.AddMilliseconds(1500), tp.GetUtcNow());
        Assert.Equal(TimeSpan.FromMilliseconds(1500), tp.GetElapsedTime(t0));
    }

    [Fact]
    public void Advance_NotCalled_NeverMoves()
    {
        var tp = new VirtualTimeProvider(Start);
        Assert.Equal(tp.GetUtcNow(), tp.GetUtcNow());
    }

    [Fact]
    public void Timer_OneShot_FiresOnceWhenDue()
    {
        var tp = new VirtualTimeProvider(Start);
        var fired = 0;
        using var t = tp.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), System.Threading.Timeout.InfiniteTimeSpan);
        tp.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);
        tp.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
        tp.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Timer_Periodic_FiresForEachElapsedPeriod()
    {
        var tp = new VirtualTimeProvider(Start);
        var seen = new List<DateTimeOffset>();
        using var t = tp.CreateTimer(_ => seen.Add(tp.GetUtcNow()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        tp.Advance(TimeSpan.FromMilliseconds(3500));
        Assert.Equal(new[] { Start.AddSeconds(1), Start.AddSeconds(2), Start.AddSeconds(3) }, seen);
    }

    [Fact]
    public void Timer_DisposedOrChanged_StopsOrReschedules()
    {
        var tp = new VirtualTimeProvider(Start);
        var fired = 0;
        var t = tp.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), System.Threading.Timeout.InfiniteTimeSpan);
        t.Change(TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);
        tp.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fired);
        t.Dispose();
        tp.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Timers_DueInSameAdvance_FireInDueOrder()
    {
        var tp = new VirtualTimeProvider(Start);
        var order = new List<string>();
        using var late = tp.CreateTimer(_ => order.Add("late"), null, TimeSpan.FromSeconds(3), System.Threading.Timeout.InfiniteTimeSpan);
        using var early = tp.CreateTimer(_ => order.Add("early"), null, TimeSpan.FromSeconds(1), System.Threading.Timeout.InfiniteTimeSpan);
        tp.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "early", "late" }, order);
    }

    [Fact]
    public void Advance_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualTimeProvider(Start).Advance(TimeSpan.FromSeconds(-1)));
    }
}
