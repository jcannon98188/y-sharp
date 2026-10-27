using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server;

/// <summary>The outcome of <see cref="UpdateLog.AppendAsync"/>: the validation status, and the record's sequence number when it was appended.</summary>
public readonly record struct AppendResult(YStatus Status, long Sequence)
{
    public bool Ok => Status.Ok;
}

/// <summary>A document read back from its log: the relay holding its merged state, and the last sequence folded into it (0 for an empty log).</summary>
public sealed record LoadedDocument(YRelay Relay, long LastSequence);

/// <summary>
/// A document's update log over an <see cref="IUpdateStore"/> (ADR 0024 SRV-10, SRV-11; TASK-5.4): validated appends stored as V2, recovery by
/// folding the log into a <see cref="YRelay"/>, and compaction that merges the log through the relay tier, with no <see cref="Doc"/>.
/// </summary>
/// <remarks>
/// <para><b>Crash consistency.</b> An append is acknowledged only when the store's append completes, so an update the caller was told about is in the
/// log. A crash before that leaves no record, and the client, never acknowledged, resends. A crash after the store recorded it but before the caller
/// heard back leaves the record, and the resent copy is appended again; folding both is the same as folding one, because merging is idempotent.
/// Compaction reads the log up to a sequence, merges it, and swaps the snapshot in with the store's atomic <see cref="IUpdateStore.CompactAsync"/>:
/// a crash at any point leaves either the old records or the snapshot, and both fold to the same state.</para>
/// <para>Every member is safe to call concurrently. Appends that race a compaction survive it, because the compaction replaces only the sequences it
/// read.</para>
/// </remarks>
public sealed class UpdateLog
{
    /// <param name="store">Where the records live.</param>
    /// <param name="limits">Limits every appended update is validated against.</param>
    /// <param name="storedLimits">
    /// Limits for reading records back, the snapshot included, and for the relays <see cref="LoadAsync"/> returns. A snapshot merges a whole history,
    /// so it can exceed the size and struct limits of one incoming update. When null, <paramref name="limits"/> with no update-size or struct-count
    /// limit; depth and string limits still apply.
    /// </param>
    public UpdateLog(IUpdateStore store, YLimits? limits = null, YLimits? storedLimits = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        Limits = limits ?? YLimits.Default;
        StoredLimits = storedLimits ?? Limits with { MaxUpdateBytes = int.MaxValue, MaxStructsPerUpdate = int.MaxValue };
    }

    public IUpdateStore Store { get; }

    /// <summary>The limits an appended update is validated against.</summary>
    public YLimits Limits { get; }

    /// <summary>The limits records are read back under.</summary>
    public YLimits StoredLimits { get; }

    /// <summary>
    /// Validates <paramref name="update"/> in full against <see cref="Limits"/>, converts it to V2 and appends it. An invalid or over-limit update
    /// returns its error and writes nothing. Exceptions from the store propagate: the update was then not acknowledged.
    /// </summary>
    public async ValueTask<AppendResult> AppendAsync(string docId, ReadOnlyMemory<byte> update, UpdateFormat format = UpdateFormat.V1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        var v2 = new ArrayBufferWriter<byte>(update.Length + 16);
        var status = YUpdate.TryConvert(update.Span, format, UpdateFormat.V2, v2, Limits);
        if (!status.Ok) return new AppendResult(status, 0);
        var seq = await Store.AppendAsync(docId, v2.WrittenMemory, cancellationToken).ConfigureAwait(false);
        return new AppendResult(default, seq);
    }

    /// <summary>
    /// Recovery: folds the snapshot and every record after it into a new relay, in sequence order. A record that does not decode means the store
    /// broke its contract (a torn record must read as absent), so it fails the load with <see cref="InvalidDataException"/> rather than skipping
    /// state a peer was told about.
    /// </summary>
    public async ValueTask<LoadedDocument> LoadAsync(string docId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        var relay = new YRelay(StoredLimits);
        var last = await FoldAsync(docId, 0, relay, cancellationToken).ConfigureAwait(false);
        return new LoadedDocument(relay, last);
    }

    /// <summary>
    /// Folds the records after <paramref name="afterSequence"/> into <paramref name="relay"/> and returns the last sequence folded
    /// (<paramref name="afterSequence"/> when there were none). A relay kept in memory catches up this way after another writer appended.
    /// </summary>
    public ValueTask<long> FoldAsync(string docId, long afterSequence, YRelay relay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);
        ArgumentNullException.ThrowIfNull(relay);
        return FoldAsync(docId, afterSequence, relay, null, null, cancellationToken);
    }

    /// <summary>
    /// <see cref="FoldAsync(string, long, YRelay, CancellationToken)"/>, also applying the records to <paramref name="doc"/> when one is given. The
    /// records are merged once (Yjs <c>mergeUpdates</c> over all of them, as Yjs storage backends load a log) and the merged update is folded in
    /// one step, so a log of n records costs its size times log n rather than n folds (TASK-13); the merge of all records is the bytes folding
    /// them one at a time gives. When the merge or the merged fold is refused (a record that does not decode, or a limit the merged update breaks
    /// that no record did) the records are folded one at a time as before, which names the record that fails.
    /// </summary>
    internal async ValueTask<long> FoldAsync(string docId, long afterSequence, YRelay relay, Doc? doc, object? origin, CancellationToken cancellationToken)
    {
        var last = afterSequence;
        var records = new List<UpdateRecord>();
        // the store's contract keeps a record's bytes valid after the read moves past it (IUpdateStore), so they are held, not copied
        await foreach (var record in Store.ReadSinceAsync(docId, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            records.Add(record);
            if (record.Sequence > last) last = record.Sequence;
        }

        if (records.Count == 0) return last;
        var updates = new ReadOnlyMemory<byte>[records.Count];
        for (var i = 0; i < updates.Length; i++) updates[i] = records[i].UpdateV2;
        var merged = new ArrayBufferWriter<byte>();
        if (YUpdate.TryMerge(updates, merged, UpdateFormat.V2, StoredLimits).Ok && relay.TryApplyUpdate(merged.WrittenSpan, UpdateFormat.V2).Ok)
        {
            if (doc is not null && !doc.TryApplyUpdate(merged.WrittenSpan, origin, UpdateFormat.V2).Ok)
                foreach (var record in records) doc.ApplyUpdate(record.UpdateV2.Span, origin, UpdateFormat.V2);
            return last;
        }

        // the relay is unchanged: a refused update changes nothing
        foreach (var record in records)
        {
            var status = relay.TryApplyUpdate(record.UpdateV2.Span, UpdateFormat.V2);
            if (!status.Ok)
                throw new InvalidDataException($"Document '{docId}': record {record.Sequence}{(record.IsSnapshot ? " (snapshot)" : "")} is not a valid update ({status.Error}).");
            doc?.ApplyUpdate(record.UpdateV2.Span, origin, UpdateFormat.V2);
        }

        return last;
    }

    /// <summary>
    /// Merges the whole log into one V2 update with the relay tier and swaps it in for every record it covers. Returns the last sequence the
    /// snapshot covers, or 0 when the log is empty and nothing was written.
    /// </summary>
    public async ValueTask<long> CompactAsync(string docId, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(docId, cancellationToken).ConfigureAwait(false);
        if (loaded.LastSequence == 0) return 0;
        var merged = loaded.Relay.EncodeStateAsUpdate(default, UpdateFormat.V2);
        await Store.CompactAsync(docId, loaded.LastSequence, merged, cancellationToken).ConfigureAwait(false);
        return loaded.LastSequence;
    }
}
