using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Time;

namespace YSharp.Tests.Protocols;

/// <summary>One recorded awareness event.</summary>
internal sealed record AwEvent(string Name, long AtMs, uint[] Added, uint[] Updated, uint[] Removed, object? Origin)
{
    public override string ToString() =>
        $"{Name}@{AtMs} +[{string.Join(",", Added)}] ~[{string.Join(",", Updated)}] -[{string.Join(",", Removed)}] origin={Origin ?? "null"}";
}

/// <summary>An awareness with a virtual clock and the ordered event log (change, update and destroy) recorded from construction on.</summary>
internal sealed class AwRig : IDisposable
{
    private readonly DateTimeOffset _epoch;

    public AwRig(uint clientId = 1, VirtualTimeProvider? clock = null, TimeProvider? provider = null)
    {
        Clock = clock ?? new VirtualTimeProvider();
        _epoch = Clock.GetUtcNow();
        Doc = new Doc(new DocOptions { ClientId = clientId });
        Awareness = new Awareness(Doc, provider ?? Clock);
        Awareness.Change += (c, o) => Events.Add(Of("change", c, o));
        Awareness.Update += (c, o) => Events.Add(Of("update", c, o));
        Awareness.Destroyed += () => Events.Add(new AwEvent("destroy", Now, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), null));
    }

    public VirtualTimeProvider Clock { get; }
    public Doc Doc { get; }
    public Awareness Awareness { get; }
    public List<AwEvent> Events { get; } = new();
    public long Now => (long)(Clock.GetUtcNow() - _epoch).TotalMilliseconds;

    private AwEvent Of(string name, AwarenessChange c, object? origin) =>
        new(name, Now, c.Added.ToArray(), c.Updated.ToArray(), c.Removed.ToArray(), origin);

    public void Tick(long ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms));

    public ApplyResult Apply(byte[] update, object? origin = null) => Awareness.TryApplyUpdate(update, origin);

    public long ClockOf(uint id) => Awareness.Meta[id].Clock;

    public long LastUpdatedMs(uint id) => (long)(Awareness.Meta[id].LastUpdated - _epoch).TotalMilliseconds;

    /// <summary>Events after the last <see cref="Mark"/>, as text.</summary>
    public string[] Log(int from = 0) => Events.Skip(from).Select(e => e.ToString()).ToArray();

    public void Dispose() => Awareness.Dispose();
}

internal static class AwBytes
{
    /// <summary>An awareness update with exactly these entries; the state is JSON text, written as UTF-8 with a byte-length prefix.</summary>
    public static byte[] Update(params (uint Id, long Clock, string Json)[] entries)
    {
        var b = new List<byte>();
        VarUint(b, (ulong)entries.Length);
        foreach (var (id, clock, json) in entries)
        {
            VarUint(b, id);
            VarUint(b, (ulong)clock);
            var utf8 = Encoding.UTF8.GetBytes(json);
            VarUint(b, (ulong)utf8.Length);
            b.AddRange(utf8);
        }
        return b.ToArray();
    }

    public static void VarUint(List<byte> to, ulong value)
    {
        while (value > 127) { to.Add((byte)((value & 127) | 128)); value >>= 7; }
        to.Add((byte)value);
    }

    public static ulong ReadVarUint(byte[] bytes, ref int pos)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[pos++];
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
            shift += 7;
        }
    }

    /// <summary>Decodes an awareness update with a reader written from the documented layout (not from y-sharp).</summary>
    public static List<(uint Id, long Clock, string Json)> Parse(byte[] bytes)
    {
        var pos = 0;
        var count = (int)ReadVarUint(bytes, ref pos);
        var list = new List<(uint, long, string)>();
        for (var i = 0; i < count; i++)
        {
            var id = (uint)ReadVarUint(bytes, ref pos);
            var clock = (long)ReadVarUint(bytes, ref pos);
            var len = (int)ReadVarUint(bytes, ref pos);
            list.Add((id, clock, Encoding.UTF8.GetString(bytes, pos, len)));
            pos += len;
        }
        Assert.Equal(bytes.Length, pos);
        return list;
    }

    public static byte[] Encode(Awareness aw, params uint[] clients)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = aw.TryEncodeUpdate(w, clients);
        Assert.True(status.Ok, $"TryEncodeUpdate failed with {status.Error}");
        return w.WrittenSpan.ToArray();
    }
}

internal static class Js
{
    public static YValue Obj(params (string Key, YValue Value)[] members) =>
        YValue.Object(members.Select(m => new KeyValuePair<string, YValue>(m.Key, m.Value)).ToArray());

    public static YValue Parse(string json)
    {
        var status = YValue.TryParseJson(Encoding.UTF8.GetBytes(json), null, out var v);
        Assert.True(status.Ok, $"{status.Error}: {json}");
        return v;
    }

    public static string Json(YValue v)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = v.TryWriteJson(w);
        Assert.True(status.Ok, status.Error.ToString());
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    public static string StateJson(Awareness aw, uint id) => aw.GetStates().TryGetValue(id, out var v) ? Json(v) : "absent";

    public static string[] Keys(Awareness aw) => aw.GetStates().Keys.OrderBy(k => k).Select(k => k.ToString()).ToArray();
}

/// <summary>A <see cref="TimeProvider"/> that records every timer created through it and when each is disposed.</summary>
internal sealed class CountingTimeProvider : TimeProvider
{
    private readonly VirtualTimeProvider _inner;

    public CountingTimeProvider(VirtualTimeProvider inner) => _inner = inner;

    public List<(TimeSpan Due, TimeSpan Period)> Created { get; } = new();
    public int Disposed { get; private set; }

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Created.Add((dueTime, period));
        return new Counted(this, _inner.CreateTimer(callback, state, dueTime, period));
    }

    private sealed class Counted : ITimer
    {
        private readonly CountingTimeProvider _owner;
        private readonly ITimer _timer;
        private bool _disposed;

        public Counted(CountingTimeProvider owner, ITimer timer) { _owner = owner; _timer = timer; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);

        public void Dispose()
        {
            if (!_disposed) { _disposed = true; _owner.Disposed++; }
            _timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
