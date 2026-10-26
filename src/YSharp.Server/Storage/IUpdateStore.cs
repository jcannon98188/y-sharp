using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>
/// One record of a document's log: an update in V2, or the snapshot a compaction left in place of every record up to <see cref="Sequence"/>.
/// </summary>
/// <param name="Sequence">The record's sequence number; for a snapshot, the last sequence it covers.</param>
/// <param name="UpdateV2">The update bytes, always in the V2 format.</param>
/// <param name="IsSnapshot">True for the compacted snapshot, which a read returns first.</param>
public readonly record struct UpdateRecord(long Sequence, ReadOnlyMemory<byte> UpdateV2, bool IsSnapshot);

/// <summary>
/// The storage of the server tier (ADR 0024 SRV-10): an asynchronous, append-only update log per document. The server ships
/// <see cref="InMemoryUpdateStore"/> as its default (SRV-12); a host plugs in a durable store by implementing this interface, and
/// <c>UpdateStoreConformance</c> in the test kit checks an implementation against the contract below.
/// </summary>
/// <remarks>
/// <para><b>Sequence numbers.</b> Each document's sequence starts at 1 and increases strictly with each append. A number is never reused, also after a
/// compaction or a failed append. A deleted document that is written again starts a new log at 1, so a reader must not resume a deleted
/// document by sequence. Documents are independent: an operation on one must not wait on another.</para>
/// <para><b>Durability.</b> <see cref="AppendAsync"/> completes only once the record survives whatever the store promises to survive (a process restart
/// for a durable store; nothing for the in-memory default). The server persists before it applies or broadcasts (SRV-8), so a completed append is the
/// acknowledgement.</para>
/// <para><b>Partial writes.</b> An append that fails or is cut off by a crash must leave either the whole record or no record: a later read never returns
/// a prefix of the bytes. A store that cannot write atomically must detect a torn record (a length and checksum frame, for example) and treat it as
/// absent. An absent record was never acknowledged, so dropping it loses nothing a peer was told about.</para>
/// <para><b>Corrupted tail records.</b> A record at the end of the log that fails its own integrity check is a torn append and is dropped on read, as
/// above, and its sequence number is not reused. A corrupted record that is followed by intact ones is not a torn append: the store must fail the read
/// with an exception rather than skip it, because the records after it may depend on it and silently skipping it would lose acknowledged state.</para>
/// <para><b>Compaction.</b> <see cref="CompactAsync"/> is atomic: a concurrent or later read sees the old records or the snapshot, never neither and
/// never both. Records appended after <c>uptoSequence</c> survive it. A crash during compaction leaves the old records.</para>
/// <para><b>Idempotence.</b> Replaying a record twice cannot change a document's state, because merging updates is idempotent, so a reader that saw a
/// record before a crash may see it again after.</para>
/// <para><b>Threads.</b> Every member may be called concurrently, for the same document or different ones.</para>
/// </remarks>
public interface IUpdateStore
{
    /// <summary>Appends one V2 update to <paramref name="docId"/>'s log and returns its sequence number once the record is durable.</summary>
    ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken = default);

    /// <summary>
    /// The records after <paramref name="afterSequence"/>, in sequence order. When the compacted snapshot covers sequences after
    /// <paramref name="afterSequence"/>, it comes first, with <see cref="UpdateRecord.IsSnapshot"/> set and the last sequence it covers. Pass 0 to read
    /// the whole log. An unknown document reads as empty.
    /// </summary>
    IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces every record up to <paramref name="uptoSequence"/> (and any earlier snapshot) with one merged V2 update. Records after
    /// <paramref name="uptoSequence"/> survive. <paramref name="uptoSequence"/> must not be past the last appended sequence.
    /// </summary>
    ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken = default);

    /// <summary>Removes the document's log, snapshot included. A later append starts a new log at sequence 1.</summary>
    ValueTask DeleteAsync(string docId, CancellationToken cancellationToken = default);
}
