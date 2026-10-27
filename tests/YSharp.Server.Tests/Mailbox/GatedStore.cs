using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server.Tests.Mailbox;

/// <summary>
/// An in-memory store whose appends to chosen documents wait on a gate or fail, and whose deletes can wait on a gate, to show what a slow or
/// broken store does to a mailbox.
/// </summary>
internal sealed class GatedStore : IUpdateStore
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failing = new(StringComparer.Ordinal);

    public InMemoryUpdateStore Inner { get; } = new();

    /// <summary>Signalled each time an append starts waiting on a gate.</summary>
    public SemaphoreSlim Waiting { get; } = new(0);

    public void Close(string docId) => _gates[docId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open(string docId)
    {
        if (_gates.TryRemove(docId, out var gate)) gate.TrySetResult();
    }

    public void Fail(string docId, bool failing = true) => _failing[docId] = failing;

    public async ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken = default)
    {
        if (_gates.TryGetValue(docId, out var gate))
        {
            Waiting.Release();
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (_failing.TryGetValue(docId, out var failing) && failing) throw new System.IO.IOException("disk full");
        return await Inner.AppendAsync(docId, updateV2, cancellationToken);
    }

    public IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, CancellationToken cancellationToken = default) =>
        Inner.ReadSinceAsync(docId, afterSequence, cancellationToken);

    public ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken = default) =>
        Inner.CompactAsync(docId, uptoSequence, mergedV2, cancellationToken);

    /// <summary>While set, a delete waits on it after signalling <see cref="Deleting"/>.</summary>
    public TaskCompletionSource? DeleteGate { get; set; }

    public SemaphoreSlim Deleting { get; } = new(0);

    public async ValueTask DeleteAsync(string docId, CancellationToken cancellationToken = default)
    {
        if (DeleteGate is { } gate)
        {
            Deleting.Release();
            await gate.Task.WaitAsync(cancellationToken);
        }

        await Inner.DeleteAsync(docId, cancellationToken);
    }
}
