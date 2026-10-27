using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.TestKit.Time;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when <see cref="Advance"/> is called (the C# side of the
/// ops-script <c>tick {ms}</c> step, ADR 0011/0016). Timers fire synchronously on the advancing thread, in due order.
/// </summary>
public sealed class VirtualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = new();
    private DateTimeOffset _now;
    private long _ticks;

    public VirtualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new VirtualTimer(this, callback, state);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by));
        var target = _ticks + by.Ticks;
        while (true)
        {
            VirtualTimer? next;
            long due;
            // the timer is picked and rescheduled under the gate: another thread may change or dispose it meanwhile (a mailbox's Awareness timer)
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
                if (next is null) break;
                due = next.Due!.Value;
                next.Reschedule();
            }
            SetTime(due);
            next.Invoke();
        }
        SetTime(target);
    }

    private void SetTime(long ticks)
    {
        _now += TimeSpan.FromTicks(ticks - _ticks);
        Interlocked.Exchange(ref _ticks, ticks);
    }

    private long _sequence;

    private sealed class VirtualTimer : ITimer
    {
        private readonly VirtualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _period;

        public VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long? Due { get; private set; }
        public long Sequence { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                Sequence = ++_owner._sequence;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                    _owner._timers.Remove(this);
                }
                else
                {
                    Due = _owner._ticks + dueTime.Ticks;
                    if (!_owner._timers.Contains(this)) _owner._timers.Add(this);
                }
            }
            return true;
        }

        // called under the owner's gate
        public void Reschedule()
        {
            if (_period > 0) { Due += _period; Sequence = ++_owner._sequence; }
            else { Due = null; _owner._timers.Remove(this); }
        }

        public void Invoke() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Due = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
