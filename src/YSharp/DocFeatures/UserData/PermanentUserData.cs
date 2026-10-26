#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- permanent user data

public sealed class PermanentUserDataOptions
{
    public YMap? Store { get; init; }                      // default doc.GetMap("users")
    public Action<Action>? Defer { get; init; }            // seam for the setTimeout(0) in PUD-03/04; tests pass a synchronous flush
}

/// <summary>
/// Yjs <c>PermanentUserData</c> (PUD-01..PUD-09; utils/PermanentUserData.js): maps client ids to user descriptions and records each user's
/// deletions in a shared <c>users</c> map of <c>{ids: YArray&lt;number&gt;, ds: YArray&lt;Uint8Array&gt;}</c>, each <c>ds</c> entry a V1 delete set.
/// Every write is its own transaction, as in Yjs, so the update events match. The lookups are guarded by the document lock (CC-20); everything
/// it subscribes belongs to the document, so nothing outlives it (ADR 0018). Like Yjs, each <see cref="SetUserMapping"/> call adds listeners that
/// stay for the document's life (PUD-05).
/// </summary>
public sealed class PermanentUserData
{
    private readonly Doc _doc;
    private readonly YMap _users;
    private readonly Action<Action> _defer;
    private readonly OrderedDictionary<ulong, string> _clients = new();                          // written under the document lock
    private readonly OrderedDictionary<string, DeleteRanges> _dss = new(StringComparer.Ordinal); // insertion order: GetUserByDeletedId's order

    public PermanentUserData(Doc doc, PermanentUserDataOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _doc = doc;
        _users = options?.Store ?? doc.GetMap("users");
        _defer = options?.Defer ?? DeferToThreadPool;
        _users.Observe((e, _) =>
        {
            foreach (var key in ((YMapEvent)e).KeysChanged) InitUser(MapAt(_users, key), key);
        });
        foreach (var (key, value) in Snapshot(_users)) InitUser(value.Type as YMap, key);
    }

    /// <summary>
    /// Yjs <c>setUserMapping</c> (PUD-03, PUD-04): adds <paramref name="clientId"/> to the user's ids, creating the user if needed. Then every
    /// local transaction on <paramref name="doc"/> that deletes something and passes <paramref name="filter"/> has its delete set appended to
    /// the user's <c>ds</c>, and a user entry replaced by a concurrent creation gets this user's ids and delete sets. Both run through the
    /// <see cref="PermanentUserDataOptions.Defer"/> seam, as Yjs runs them in <c>setTimeout</c>.
    /// </summary>
    public void SetUserMapping(Doc doc, ulong clientId, string description, Func<Transaction, DeleteSet, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clientId, Lib0Reader.MaxVarint);   // a JS number holds it exactly
        var users = _users;
        var user = new StrongBox<YMap?>(MapAt(users, description));
        if (user.Value is null)   // JS: !user; an entry that is not a map is replaced (JS would throw on it)
        {
            var created = new YMap();
            created.Set("ids", new YArray());
            created.Set("ds", new YArray());
            users.Set(description, created);
            user.Value = created;
        }

        ArrayAt(user.Value, "ids")?.Push(new YEntry[] { (double)clientId });
        users.Observe((_, _) => Defer(() =>
        {
            var overwrite = MapAt(users, description);
            if (ReferenceEquals(overwrite, Volatile.Read(ref user.Value))) return;
            // the user was overwritten: port all data over to the next user object
            Volatile.Write(ref user.Value, overwrite);
            if (overwrite is null) return;   // PUD-07: the key was deleted; JS throws reading the missing user
            var (ids, ds) = Read(() =>
            {
                var mine = new List<ulong>();
                foreach (var (client, owner) in _clients)
                    if (owner == description) mine.Add(client);
                return (mine, _dss.TryGetValue(description, out var r) ? Encode(r) : null);
            });
            foreach (var client in ids) ArrayAt(overwrite, "ids")?.Push(new YEntry[] { (double)client });
            if (ds is not null) ArrayAt(overwrite, "ds")?.Push(new YEntry[] { YValue.BytesOwned(ds) });
        }));

        filter ??= static (_, _) => true;
        doc.AfterTransaction += (tx, _) =>
        {
            // JS reads the transaction inside the timer; here it has ended by then, so local, the delete set and the filter are taken now
            if (!tx.IsLocal) return;
            var ds = tx.DeleteSet;
            if (ds.Ranges.Count == 0 || !filter(tx, ds)) return;
            var encoded = Encode(ds.Ranges);
            Defer(() =>
            {
                if (Volatile.Read(ref user.Value) is { } u) ArrayAt(u, "ds")?.Push(new YEntry[] { YValue.BytesOwned(encoded) });
            });
        };
    }

    /// <summary>Yjs <c>getUserByClientId</c> (PUD-06): the description, or null (also for an empty one, as JS's <c>|| null</c>).</summary>
    public string? GetUserByClientId(ulong clientId) =>
        Read(() => _clients.TryGetValue(clientId, out var user) && user.Length > 0 ? user : null);

    /// <summary>Yjs <c>getUserByDeletedId</c> (PUD-06): the first user, in the order users were first seen, whose deletions contain <paramref name="id"/>.</summary>
    public string? GetUserByDeletedId(ItemId id) => Read(() =>
    {
        foreach (var (user, ds) in _dss)
            if (ds.Contains(id.Client, id.Clock)) return user;
        return (string?)null;
    });

    // ------------------------------------------------------------------ Yjs initUser (PUD-02, PUD-08)

    /// <summary>Reads a user's ids and delete sets and follows later additions; runs under the document lock.</summary>
    private void InitUser(YMap? user, string description)
    {
        if (user is null || ArrayAt(user, "ds") is not { } ds || ArrayAt(user, "ids") is not { } ids) return;   // PUD-07: JS throws here
        ds.Observe((e, _) =>
        {
            foreach (var entry in Added((YArrayEvent)e))
                if (Decode(entry) is { } added)
                    _dss[description] = _dss.TryGetValue(description, out var known) ? Merge(known, added) : added;
        });
        var merged = new DeleteRanges();
        foreach (var entry in ds.ToArray())
            if (Decode(entry) is { } r) merged = Merge(merged, r);   // JS decodes every entry here and fails on one that is not bytes
        _dss[description] = merged;
        ids.Observe((e, _) =>
        {
            foreach (var entry in Added((YArrayEvent)e)) AddClient(entry, description);
        });
        foreach (var entry in ids.ToArray()) AddClient(entry, description);
    }

    private void AddClient(YEntry entry, string description)
    {
        // JS keys its map by any value; only a whole number up to 2^53-1 can equal a client id
        if (entry.Kind == YEntryKind.Any && entry.Value.TryGetNumber(out var n) && n >= 0 && n <= Lib0Reader.MaxVarint && n == Math.Floor(n))
            _clients[(ulong)n] = description;
    }

    /// <summary>Yjs <c>event.changes.added</c> flattened to its content: the inserted entries, in list order.</summary>
    private static List<YEntry> Added(YArrayEvent e)
    {
        var added = new List<YEntry>();
        foreach (var op in e.Delta)
            if (op.Kind == DeltaKind.Insert) added.AddRange(op.Inserted.Span);
        return added;
    }

    // ------------------------------------------------------------------ delete sets

    private static byte[] Encode(DeleteRanges ranges)
    {
        var writer = new ArrayBufferWriter<byte>();
        new DeleteSet(ranges).Encode(writer, UpdateFormat.V1);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Yjs <c>readDeleteSet</c> with <c>DSDecoderV1</c>, or null for an entry that is not bytes or does not decode.</summary>
    private static DeleteRanges? Decode(YEntry entry)
    {
        if (entry.Kind != YEntryKind.Binary || !entry.Value.TryGetBytes(out var bytes)) return null;
        var reader = new Lib0Reader(bytes);
        if (!reader.TryReadVarUint(out var clients)) return null;
        var ranges = new DeleteRanges();
        for (ulong c = 0; c < clients; c++)
        {
            if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var count)) return null;
            for (ulong i = 0; i < count; i++)
            {
                if (!reader.TryReadVarUint(out var clock) || !reader.TryReadVarUint(out var length)) return null;
                if (clock + length > Lib0Reader.MaxVarint) return null;
                if (length > 0) ranges.Add(client, (long)clock, (long)length);
            }
        }

        return ranges;
    }

    /// <summary>Yjs <c>mergeDeleteSets</c>, into a new set.</summary>
    private static DeleteRanges Merge(DeleteRanges a, DeleteRanges b)
    {
        var merged = new DeleteRanges();
        for (var i = 0; i < a.Count; i++) merged.Add(a[i].Client, a[i].Clock, a[i].Length);
        for (var i = 0; i < b.Count; i++) merged.Add(b[i].Client, b[i].Clock, b[i].Length);
        return merged;
    }

    // ------------------------------------------------------------------ helpers

    private static YMap? MapAt(YMap map, string key) => map.TryGet(key, out var e) ? e.Type as YMap : null;

    private static YArray? ArrayAt(YMap map, string key) => map.TryGet(key, out var e) ? e.Type as YArray : null;

    private List<KeyValuePair<string, YEntry>> Snapshot(YMap map) => Read(() => TypeMap.Entries(map));

    /// <summary>Runs a deferred action unless the document is gone; a fault is reported like an observer's, never thrown into the scheduler.</summary>
    private void Defer(Action action) => _defer(() =>
    {
        if (_doc.IsDestroyed) return;
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _doc.ReportObserverFault(e, this);
        }
    });

    private static void DeferToThreadPool(Action action) => ThreadPool.UnsafeQueueUserWorkItem(static a => a(), action, preferLocal: false);

    private T Read<T>(Func<T> body)
    {
        var h = _doc.EnterRead();
        try
        {
            return body();
        }
        finally
        {
            _doc.Leave(h);
        }
    }
}
