# How y-sharp works

This guide is for people who use y-sharp, either in an application or on a server. It covers the core library first: documents, shared types, transactions, updates and the sync protocol. Then it covers the server tier, and how the project shows that y-sharp interoperates with Yjs. [server.md](server.md) has the server's full hosting reference. The [architecture decision records](adr/) explain why each part is built the way it is.

The packages are `YSharp` (the core), `YSharp.Server` and `YSharp.Server.Testing`. Once the first preview is published ([releasing.md](releasing.md)), install them with:

```bash
dotnet add package YSharp --prerelease
dotnet add package YSharp.Server --prerelease   # only for a server
```

Until then, reference the projects from source:

```xml
<ProjectReference Include="path/to/y-sharp/src/YSharp/YSharp.csproj" />
<ProjectReference Include="path/to/y-sharp/src/YSharp.Server/YSharp.Server.csproj" /> <!-- only for a server -->
```

## Contents

- [The model in one page](#the-model-in-one-page)
- [Documents and shared types](#documents-and-shared-types)
- [Transactions and threads](#transactions-and-threads)
- [Observing changes](#observing-changes)
- [Updates and state vectors](#updates-and-state-vectors)
- [The sync protocol](#the-sync-protocol)
- [Awareness](#awareness)
- [Undo, snapshots and positions](#undo-snapshots-and-positions)
- [Errors, limits and hostile input](#errors-limits-and-hostile-input)
- [Working with updates without a document](#working-with-updates-without-a-document)
- [The server tier](#the-server-tier)
- [How interop is guaranteed](#how-interop-is-guaranteed)
- [Where to read more](#where-to-read-more)

## The model in one page

y-sharp is a port of [Yjs](https://github.com/yjs/yjs) v13, a CRDT (conflict-free replicated data type). Every replica holds a `Doc`. A `Doc` contains named shared types: text, arrays, maps and XML. Any replica can edit at any time, even offline. Each edit becomes an **update**, a compact binary record of the inserted items and deleted ranges. Replicas exchange updates in any order, any number of times, and they all converge on the same content. No server has to decide who wins.

These ideas carry the rest of the guide:

- **Items and IDs.** Each inserted piece of content is an item with an ID of `(client, clock)`. The client is a random 53-bit number per document session. The clock counts up from 0 for that client. An item also records its left and right neighbours at the time of insertion, and concurrent inserts at the same place are ordered from those.
- **Deletes are tombstones.** A delete marks items as deleted; it does not remove them. With garbage collection on (the default), the content of a deleted item is dropped and only its ID range is kept.
- **State vector.** A map from each client to the next clock this replica expects from it. It tells another replica exactly which updates you are missing.
- **Update formats.** Updates come in two wire formats, V1 and V2. V1 is the one y-websocket and most providers send. V2 compresses better. y-sharp reads and writes both, byte for byte as Yjs does.

y-sharp's aim is that a y-sharp `Doc` and a Yjs `Doc` can exchange updates, state vectors and sync messages and converge on the same state, with the same bytes.

## Documents and shared types

```csharp
using YSharp;

var doc = new Doc();
var text = doc.GetText("notes");     // collaborative text with formatting
var list = doc.GetArray("items");    // an ordered list
var meta = doc.GetMap("meta");       // a key-value map

doc.Transact(tx =>
{
    text.Insert(0, "Hello world");
    text.Format(0, 5, YValue.Object([new("bold", true)]));
    list.Push(["one", 2.0, true]);
    meta.Set("title", "Draft");
    meta.Set("tags", new YArray());  // types nest
});

Console.WriteLine(text.ToString()); // "Hello world"
YValue json = doc.ToJson();         // the whole document as a JSON-like value
```

- **Root types** are fetched by name with `GetText`, `GetArray`, `GetMap`, `GetXmlFragment`, `GetXmlElement` and `GetXmlText`. Every replica that asks for the same name gets the same shared type. As in Yjs, asking for one name as two different kinds throws `YTypeConflictException`.
- **`YText`** supports `Insert`, `Delete`, `Format`, embeds and Quill-style deltas (`ToDelta`, `ApplyDelta`). Indexes and lengths count UTF-16 code units, which is what JavaScript counts, so a position means the same on both sides.
- **`YArray` and `YMap`** hold `YEntry` values. A `YEntry` is a plain value (`YValue`), a nested shared type, or a subdocument. Strings, numbers, booleans, `YValue`s and types convert to `YEntry` implicitly.
- **`YValue`** is the JSON-like value type that Yjs calls "Any": null, undefined, booleans, numbers, bigints, strings, byte arrays, arrays and objects. Its mapping to JavaScript values is pinned in ADR 0002 and ADR 0007.
- **XML types** (`YXmlFragment`, `YXmlElement`, `YXmlText`) model the trees that editors such as ProseMirror and TipTap bind to through y-prosemirror.
- **Subdocuments**, **permanent user data** and **garbage collection** (`DocOptions.Gc`, `GcFilter`) work as in Yjs.

`DocOptions` sets the client ID, the GUID, garbage collection and the decoding limits (see [Errors, limits and hostile input](#errors-limits-and-hostile-input)).

## Transactions and threads

Every change happens inside a transaction. A transaction groups changes into one update and one round of events. A call made outside an explicit transaction runs in its own implicit one, so `text.Insert(0, "x")` on its own works. Use `Transact` to batch:

```csharp
doc.Transact(tx => { text.Delete(0, 5); text.Insert(0, "Howdy"); }, origin: "local");
```

The **origin** is any object you choose. Observers, the `Update` event and the undo manager receive it, which is how you tell your own changes from remote ones.

Unlike Yjs, y-sharp is safe to use from many threads (ADR 0006):

- Each `Doc` has one re-entrant reader/writer lock. A write transaction is exclusive, and read transactions (`ReadTransact`, `BeginReadTransaction`) share the lock.
- Every public member of `Doc`, the shared types and `Awareness` can be called from any thread.
- `Transact` can take a timeout and a `CancellationToken`. `TryTransact` never blocks: it reports `Contended` instead of waiting.
- Observers run synchronously on the committing thread, inside the lock. There is no thread hop.
- An enumerator or live view taken inside a transaction throws `YLifetimeException` once that transaction ends. It never returns stale content.

The API is synchronous. A server that wants asynchronous, ordered access per document should use `YSharp.Server`, which serializes work per document for you.

## Observing changes

```csharp
using var sub = text.Observe((e, tx) =>
{
    if (!tx.IsLocal) Console.WriteLine("a remote change arrived");
});

doc.Update += (update, origin, d, tx) =>
{
    // update is the V1 bytes of this transaction; send them to other replicas
};
```

- `Observe` fires for changes to the type itself, and `ObserveDeep` for changes anywhere below it. Each returns a `Subscription`; dispose it to unsubscribe.
- Events expose what Yjs events do: `Delta` on text and arrays, `Keys` on maps, `GetPath()`, and the `Transaction` they came from.
- `Doc.Update` and `Doc.UpdateV2` deliver each committed transaction as an update. These are what you send over the network or append to storage. The document also raises `BeforeTransaction`, `AfterTransaction`, `SubdocsChanged` and the other Yjs lifecycle events.
- An exception thrown by an observer does not corrupt the document. It is caught, counted, and reported through `Doc.ObserverFaulted` (ADR 0005).

## Updates and state vectors

Two documents converge by exchanging updates:

```csharp
var bob = new Doc();

// Full state: everything alice has.
bob.ApplyUpdate(alice.EncodeStateAsUpdate());

// Only what bob is missing: alice diffs against bob's state vector.
var sv = new ArrayBufferWriter<byte>();
bob.EncodeStateVector(sv);
byte[] missing = alice.EncodeStateAsUpdate(sv.WrittenSpan);
bob.ApplyUpdate(missing, origin: "network");
```

- Updates are **commutative, associative and idempotent**. You can apply them in any order, merge them, and apply them twice. An update whose dependencies have not arrived yet waits in the document's pending state and is integrated when they do.
- Writers take an `IBufferWriter<byte>`, so a caller can encode into pooled memory. Each also has a `byte[]` convenience overload.
- Every method that reads update bytes takes an `UpdateFormat` (V1 by default). Use V2 only if both ends agreed on it.

## The sync protocol

y-websocket and most Yjs providers speak the y-protocols sync protocol. There are three message types:

1. **Step 1** carries the sender's state vector: "here is what I have".
2. **Step 2** answers a step 1 with the update the other side is missing.
3. **Update** carries a live change.

`YSync` reads and writes these messages, and handles one complete exchange per call:

```csharp
// When the connection opens, tell the other side what you have.
var outgoing = new ArrayBufferWriter<byte>();
YSync.WriteStep1(outgoing, doc);
socket.Send(outgoing.WrittenSpan);

// For each incoming sync message:
ReadOnlySpan<byte> input = message;
var reply = new ArrayBufferWriter<byte>();
YStatus status = YSync.TryReadMessage(ref input, reply, doc, origin: connection, out SyncReadResult result);
if (result.ReplyWritten) socket.Send(reply.WrittenSpan); // a step 2 answering their step 1

// For each local change, forward it as an Update message.
doc.Update += (update, origin, d, tx) =>
{
    if (origin == connection) return; // don't echo a change back to where it came from
    var w = new ArrayBufferWriter<byte>();
    YSync.WriteUpdate(w, update);
    socket.Send(w.WrittenSpan);
};
```

On a y-websocket connection each message starts with an outer type: 0 for sync, 1 for awareness, 2 for auth. `YAuth` handles the auth messages. y-sharp has no built-in WebSocket provider for clients; the code above is the whole of what one does. On the server side, `YSharp.Server` handles the outer framing for you.

## Awareness

Awareness carries short-lived per-user state, such as cursors, selections, names and colours. It is not part of the document's history.

```csharp
using var awareness = new Awareness(doc);
awareness.SetLocalStateField("user", YValue.Object([new("name", "Alice"), new("color", "#d33")]));
awareness.Change += (change, origin) => { /* added, updated, removed client IDs */ };
```

`EncodeUpdate` and `TryApplyUpdate` move awareness state over the wire, byte-compatible with y-protocols. States time out after `Awareness.OutdatedTimeout` (30 seconds) without a renewal, as in JS. The timer uses an injectable `TimeProvider`, so tests can control time.

## Undo, snapshots and positions

**Undo.** `UndoManager` tracks changes to one or more types and undoes them as Yjs does. By default it tracks changes made with no origin (`TrackedOrigins` in `UndoManagerOptions`). It groups edits made within `CaptureTimeout` of each other, and `StopCapturing()` starts a new group. Remote changes are not undone, but undoing your own edit still works after remote edits arrive.

```csharp
var undo = new UndoManager(text);
text.Insert(0, ">> ");
undo.Undo();   // removes ">> "
undo.Redo();   // puts it back
```

**Snapshots.** `YSnapshot.Create(doc)` records a state vector and a delete set. `YText.ToDelta(snapshot, previous)` renders text as it was at a snapshot, and can mark what changed between two snapshots. `YSnapshot.CreateDoc` rebuilds a document as of a snapshot; this needs garbage collection off.

**Relative positions.** An index such as "character 6" goes stale as soon as someone types before it. `type.CreateRelativePosition(index)` returns a position tied to an item instead. `position.Resolve(doc)` turns it back into the current index, even after concurrent edits. This is how remote cursors stay in place. Relative positions encode to the same bytes and JSON as Yjs.

## Errors, limits and hostile input

A collaborative document reads bytes from other people, so y-sharp assumes those bytes can be wrong or malicious (ADR 0005, ADR 0013):

- **Try methods.** Each method that reads untrusted bytes has a `Try` form. It returns a `YStatus` or `ApplyResult` with a `YError` (`Malformed`, `LimitExceeded`, `Rejected`, `UnknownMessage`) and never throws on bad input. The throwing forms (`ApplyUpdate`, `StateVector.Decode`) throw `YDecodeException`, which carries the same `YError`.
- **Atomic refusal.** A refused update changes nothing. The document is not left half-applied.
- **Limits.** `YLimits` bounds update size, structs per update, string bytes, nesting depth and the pending state that waits on missing dependencies. Set them through `DocOptions.Limits`. The defaults are generous (64 MB updates, strings and pending state, nesting depth 128); a server should set its own.
- **No unbounded recursion.** Deeply nested input fails with `LimitExceeded`. It never overflows the stack.
- **Faulted state.** If an internal invariant breaks anyway, the document is marked faulted (`IsFaulted`). It stays readable and encodable but refuses further changes, rather than carrying on with corrupt state. Recover by applying its `EncodeStateAsUpdate` to a fresh `Doc`.

```csharp
ApplyResult r = doc.TryApplyUpdate(bytesFromThePeer, origin: "peer");
if (!r.Ok) Console.WriteLine($"refused: {r.Error}"); // the document is unchanged
```

## Working with updates without a document

The `YUpdate` functions work on update bytes directly, as Yjs's `mergeUpdates`, `diffUpdate` and similar functions do:

| Method | What it does |
| --- | --- |
| `TryMerge(updates, output)` | Merges many updates into one, for example to compact a log. |
| `TryDiff(update, stateVector, output)` | Keeps only what a state vector is missing. |
| `TryStateVectorFromUpdate(update, output)` | Reads the state vector an update would produce. |
| `TryConvert(update, from, to, output)` | Converts between V1 and V2. |
| `TryValidate(update)` | Checks an update against the limits without applying it. |
| `TryDecode(update, out decoded)` | A read-only view of an update's structs and deletes, for inspection or validation. |

**`YRelay`** builds on these. It holds a document as one merged update and serves sync from it, byte for byte as Yjs would, without building a `Doc`. A relay is much cheaper than a `Doc` for a server that only stores and forwards updates. Its methods come only in `Try` forms, because its input always comes from a peer.

## The server tier

`YSharp.Server` serves documents to y-websocket-compatible clients. [server.md](server.md) is the full reference; this is the outline.

```csharp
using YSharp.Server;

await using var server = new YServer(new YServerOptions { Store = new MyDurableStore() });

// For each authenticated socket:
YConnection connection = await server.ConnectAsync(documentId);
// copy socket frames into connection.ReceiveAsync, and connection.Outgoing into the socket
```

- **Transport-neutral.** The host owns the sockets and authentication, for example through ASP.NET Core WebSockets. A `YConnection` only takes and emits whole y-websocket messages.
- **One mailbox per document.** Each document has a bounded queue and a single worker, so its work runs in order without locks. Different documents run in parallel.
- **Relay first.** A document is served from a `YRelay`. A full `Doc` is built only when something needs one: a validation hook, `EditAsync`, `ReadAsync` or a `SubscribeAsync` subscriber. It is dropped again when the document goes idle.
- **Check, persist, then broadcast.** An incoming update is decoded once and checked against the limits, the optional validation hook, the relay's pending bound and the hot `Doc`. Then it is appended to the store, and only after that is it sent to the other peers. The log never holds a refused update, and nothing is broadcast before it is durable.
- **Storage.** `IUpdateStore` is an append-only V2 log per document, with compaction. The in-memory default is not durable. Write your own backend and check it with the conformance suite in `YSharp.Server.Testing`.
- **Server-side access.** `EditAsync`, `ReadAsync` and `SubscribeAsync` let server code work with a document through its mailbox. `DeleteAsync` closes a document's connections and removes it from the store.
- **Backpressure.** A peer that falls too far behind is closed, so it cannot hold the document up. It reconnects and resyncs.

## How interop is guaranteed

"Wire-compatible" is a claim the test suite checks. It does not rely on reading the code:

- **The real Yjs is the oracle.** Yjs v13.6.33, lib0 and y-protocols are pinned as git submodules (ADR 0001). Wherever y-sharp could differ from them, it does what JS does, unless that is unsafe. Each deliberate difference is recorded with a test that shows the JS behaviour (ADR 0003).
- **Golden fixtures.** About 140 fixtures are generated by running scripts on the real Yjs. They record exact update bytes, state vectors and document dumps, and y-sharp must reproduce them byte for byte. CI regenerates them and fails on any drift.
- **Live differential testing.** Random operation scripts run in both Node (Yjs) and y-sharp. The bytes must match, and the replicas must converge under shuffled, duplicated and dropped delivery.
- **Ported upstream tests.** All 209 upstream Yjs tests are accounted for. The lib0 random generator is ported bit for bit, so upstream seeds reproduce.
- **Hostile input, concurrency and memory.** Fuzzing, stress and soak runs, and allocation budgets each have their own tier.

The README's [How correctness is tested](../README.md#how-correctness-is-tested) section describes each tier.

## Where to read more

| Topic | Where |
| --- | --- |
| Hosting the server, storage, validation hooks | [server.md](server.md) |
| Why the API looks the way it does, and open decisions | [api-decisions.md](api-decisions.md), [ADR 0008](adr/0008-public-api-shape.md) |
| Error model and crash safety | [ADR 0005](adr/0005-error-model-and-crash-safety.md) |
| Thread safety | [ADR 0006](adr/0006-concurrency-contract.md) |
| Value mapping to JavaScript | [ADR 0002](adr/0002-yvalue-pinned-any-mapping.md), [ADR 0007](adr/0007-yvalue-contract.md) |
| Memory layout and budgets | [ADR 0009](adr/0009-allocation-and-memory-budgets.md), [ADR 0018](adr/0018-memory-layout-requirements.md), [ADR 0023](adr/0023-storage-model-object-per-item.md) |
| Server layering | [ADR 0024](adr/0024-server-tier-layering.md) |
| The Yjs quirks y-sharp reproduces | [research/quirks.md](research/quirks.md) |
| Every behaviour and its test | [inventory/](inventory/) |
