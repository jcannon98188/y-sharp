using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>
/// The default <see cref="IUpdateStore"/> (ADR 0024 SRV-12): every document's log in process memory. <b>It is not durable.</b> "Durable" in the
/// <see cref="IUpdateStore"/> contract means "recorded in this process" here, and a restart loses every document. Host a durable store by
/// implementing <see cref="IUpdateStore"/>.
/// </summary>
/// <remarks>
/// Each document has its own lock, so documents never wait on each other, and every member is safe to call concurrently. An append copies the
/// caller's bytes, and a record is added whole or not at all, so there are no partial writes or torn records to recover from.
/// </remarks>
public sealed class InMemoryUpdateStore : IUpdateStore
{
    private readonly ConcurrentDictionary<string, DocLog> _docs = new(StringComparer.Ordinal);

    public ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = updateV2.ToArray();
        while (true)
        {
            var log = _docs.GetOrAdd(docId, _ => new DocLog());
            lock (log)
            {
                if (log.Deleted) continue; // removed between the lookup and the lock: append to the document's new log
                var seq = ++log.LastSequence;
                log.Records.Add(new UpdateRecord(seq, copy, false));
                return ValueTask.FromResult(seq);
            }
        }
    }

    public async IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_docs.TryGetValue(docId, out var log)) yield break;
        UpdateRecord[] records;
        lock (log)
        {
            // one consistent view under the lock: a compaction that lands while the caller iterates cannot show it neither or both
            var list = new List<UpdateRecord>(log.Records.Count + 1);
            if (log.Snapshot is { } snapshot && snapshot.Sequence > afterSequence) list.Add(snapshot);
            foreach (var r in log.Records)
                if (r.Sequence > afterSequence) list.Add(r);
            records = list.ToArray();
        }

        foreach (var r in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_docs.TryGetValue(docId, out var log))
            throw new InvalidOperationException($"Document '{docId}' has no log to compact.");
        var copy = mergedV2.ToArray();
        lock (log)
        {
            if (log.Deleted) throw new InvalidOperationException($"Document '{docId}' has no log to compact.");
            if (uptoSequence > log.LastSequence)
                throw new ArgumentOutOfRangeException(nameof(uptoSequence), $"Sequence {uptoSequence} is past the last appended sequence {log.LastSequence}.");
            if (log.Snapshot is { } current && current.Sequence >= uptoSequence) return ValueTask.CompletedTask; // an older compaction: nothing to replace
            log.Records.RemoveAll(r => r.Sequence <= uptoSequence);
            log.Snapshot = new UpdateRecord(uptoSequence, copy, true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string docId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_docs.TryRemove(docId, out var log))
            lock (log) log.Deleted = true;
        return ValueTask.CompletedTask;
    }

    private sealed class DocLog
    {
        public readonly List<UpdateRecord> Records = new();
        public UpdateRecord? Snapshot;
        public long LastSequence;
        public bool Deleted;
    }
}
