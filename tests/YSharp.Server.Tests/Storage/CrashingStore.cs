using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server.Tests.Storage;

/// <summary>A simulated process crash: the operation in flight never completes, and the caller never hears back.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string point) : base("simulated crash at " + point) => Point = point;

    public string Point { get; }
}

/// <summary>Where a crash can land in the storage path. The store itself is atomic per call, so these are the boundaries around each call.</summary>
public enum CrashPoint
{
    None,
    /// <summary>An append, before the record reaches the store.</summary>
    AppendBeforeWrite,
    /// <summary>An append, after the store recorded it but before the caller heard back.</summary>
    AppendAfterWrite,
    /// <summary>A compaction, after the log was read and merged but before the swap.</summary>
    CompactBeforeSwap,
    /// <summary>A compaction, after the swap but before the caller heard back.</summary>
    CompactAfterSwap,
    /// <summary>A read, after the first record was returned.</summary>
    ReadMidway,
}

/// <summary>
/// Wraps a store and crashes once at <see cref="Point"/>. The inner store is what survives the crash, as a durable medium would; a test
/// "restarts" by building a new <see cref="UpdateLog"/> over <see cref="Inner"/>.
/// </summary>
public sealed class CrashingStore(IUpdateStore inner) : IUpdateStore
{
    public IUpdateStore Inner { get; } = inner;

    public CrashPoint Point { get; set; }

    public bool Crashed { get; private set; }

    private void MaybeCrash(CrashPoint at)
    {
        if (Point != at || Crashed) return;
        Crashed = true;
        throw new SimulatedCrashException(at.ToString());
    }

    public async ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken = default)
    {
        MaybeCrash(CrashPoint.AppendBeforeWrite);
        var seq = await Inner.AppendAsync(docId, updateV2, cancellationToken);
        MaybeCrash(CrashPoint.AppendAfterWrite);
        return seq;
    }

    public async IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = true;
        await foreach (var r in Inner.ReadSinceAsync(docId, afterSequence, cancellationToken))
        {
            if (!first) MaybeCrash(CrashPoint.ReadMidway);
            first = false;
            yield return r;
        }
    }

    public async ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken = default)
    {
        MaybeCrash(CrashPoint.CompactBeforeSwap);
        await Inner.CompactAsync(docId, uptoSequence, mergedV2, cancellationToken);
        MaybeCrash(CrashPoint.CompactAfterSwap);
    }

    public ValueTask DeleteAsync(string docId, CancellationToken cancellationToken = default) => Inner.DeleteAsync(docId, cancellationToken);
}
