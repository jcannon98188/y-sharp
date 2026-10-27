# The server tier

`YSharp.Server` serves Yjs documents to y-websocket-compatible clients. It sits above the core library and does not change it. The decisions behind it, and the guarantees with their `SRV-` and `RLY-` IDs, are in [ADR 0024](adr/0024-server-tier-layering.md). This page covers how the pieces fit together and how a host uses them.

## Layering

```
  host (ASP.NET Core, Kestrel, anything)      accepts sockets, authenticates, pumps bytes
        |  YServer.ConnectAsync / YConnection
  YSharp.Server                               one mailbox per open document
        |  structural validation -> validation hook -> relay/Doc checks -> store append -> commit -> broadcast
        |-- IUpdateStore                      append-only V2 log per document (in-memory default, or the host's)
        |-- YRelay (in YSharp)                the document as one merged update; answers sync without a Doc
        `-- Doc (in YSharp)                   only while a hook, a host edit or read, or a subscriber needs one
  YSharp                                      the Yjs port: Doc, update functions, sync and awareness protocols
```

- **`YSharp`** is the core. Two pieces of it are built for servers. `YRelay` holds a document as its merged update and answers a client's state vector with a diff, byte for byte as Yjs `mergeUpdates` and `diffUpdate` would. `YUpdate.TryDecode` gives a read-only view of an update's structs and delete set without applying it.
- **`YSharp.Server`** holds the mailbox, the connection, the storage interface and its in-memory store, the update log and validation. It reaches the core only through public API.
- **`YSharp.Server.Testing`** holds `UpdateStoreConformance`, the conformance suite for a storage backend.

### One mailbox per document

`YServer` opens a mailbox for a document on its first connection. The mailbox is a bounded queue with a single worker, so everything that happens to one document happens in order. Different documents run in parallel and do not wait on each other. Every update a client sends goes through this pipeline (SRV-8):

1. **Structural validation.** The update is decoded in full against `YServerOptions.Limits` and converted to V2. A malformed or over-limit update closes the connection with `ProtocolError`.
2. **The validation hook**, if the document has one. It runs on a read-only view of the document with a deadline. A reject, a throw, a timeout or an attempt to write all count as a reject. The client is sent a permission-denied auth message and resynced.
3. **The relay and Doc checks.** The relay prepares its fold without changing anything. An update that would push the relay's pending structs and deletes (those still waiting on a missing clock) past `Limits.MaxPendingBytes` is refused (RLY-7). If there is a hot `Doc`, the update is applied to it; a `Doc` that refuses the update is rebuilt from the relay. Either refusal closes the sender with `ProtocolError`.
4. **The store append.** It completes only once the update is durable. If it fails, the update is not committed or broadcast, the hot `Doc` is rebuilt from the relay, and the sender is closed with `StoreFailure`.
5. **Commit** the prepared fold to the relay.
6. **Broadcast.** The update is encoded once, and that one buffer goes to every other connection on the document.

So nothing reaches the store that the relay or the `Doc` would refuse. Between steps 3 and 4 the hot `Doc` is ahead of the log; only the next update's hook can see it, and it is rolled back if the append fails.

A document with no validation hook builds a `Doc` only when the host edits, reads or subscribes to it (below). Otherwise it is served from the relay alone.

### Backpressure

- **Inbound.** `YConnection.ReceiveAsync` waits while the document's mailbox is full (`MailboxCapacity`), so a fast sender slows down instead of growing memory.
- **Outbound.** The mailbox never waits for a peer. A peer whose outgoing queue reaches `SubscriberQueueCapacity` is closed with `SlowSubscriber`. It reconnects and resyncs, as a y-websocket provider does after any drop.
- **Shutdown.** `YServer.DisposeAsync` stops taking messages, finishes every update a mailbox already accepted (persisted and broadcast), and then closes every connection with `ServerShutdown`.

## Storage

The server persists through `IUpdateStore`, an asynchronous append-only log of V2 updates per document:

| Member | Contract |
| --- | --- |
| `AppendAsync(docId, updateV2)` | Returns the record's sequence number once it is durable. Sequences start at 1 and increase strictly. |
| `ReadSinceAsync(docId, afterSequence)` | The snapshot (if any) and then the records after `afterSequence`, in order. An unknown document reads as empty. |
| `CompactAsync(docId, uptoSequence, mergedV2)` | Atomically replaces the records up to `uptoSequence` with one merged snapshot. Later records survive. |
| `DeleteAsync(docId)` | Removes the log. A later append starts again at 1. |

The interface's documentation in `src/YSharp.Server/Storage/IUpdateStore.cs` gives the full contract: torn writes, corrupted records, crash behaviour during compaction, idempotent replay and thread safety.

- **The default** is `InMemoryUpdateStore`. **It is not durable: a restart loses every document.** `YServerOptions.UsesDefaultStore` tells a host whether it is still on the default.
- **A durable backend** is an `IUpdateStore` the host writes, for example over a database table or files, and sets as `YServerOptions.Store`. To check it, subclass `UpdateStoreConformance` from `YSharp.Server.Testing` and run its checks in your test project. The suite covers sequences, ordering, compaction, deletion, crashes during an append or a compaction, torn tails, mid-log corruption and concurrency.
- **Compaction** is the host's call. `new UpdateLog(store).CompactAsync(docId)` merges a document's log into one snapshot and is safe to run while the document is open.
- **Deletion** goes through `YServer.DeleteAsync(docId)`. It lets the document's mailbox finish what it had accepted, closes its connections with `ConnectionCloseReason.DocumentDeleted`, and then deletes the log from the store. A peer or server read that arrives meanwhile waits and then finds the document empty. Calling `IUpdateStore.DeleteAsync` directly on an open document leaves its mailbox serving the old state until it is evicted, and its next append starts a new log.

## Hosting

`YSharp.Server` is transport-neutral. The host accepts the socket, authenticates the user, picks the document, and moves whole messages between the socket and a `YConnection`. A minimal ASP.NET Core host:

```csharp
var server = new YServer(new YServerOptions
{
    Store = new MyDurableStore(),             // omit to use the non-durable in-memory default
    Validation = docId => docId.StartsWith("locked/") ? RejectAll : null,
});

app.UseWebSockets();
app.Map("/{docId}", async (HttpContext http, string docId) =>
{
    if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = 400; return; }
    var context = new ConnectionContext { Identity = http.User.Identity?.Name, IsReadOnly = !http.User.IsInRole("editor") };
    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    await using var connection = await server.ConnectAsync(docId, context, http.RequestAborted);

    // server -> client: the server's Step1 is already queued
    var send = Task.Run(async () =>
    {
        await foreach (var message in connection.Outgoing.ReadAllAsync(http.RequestAborted))
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, http.RequestAborted);
    });

    // client -> server: one whole WebSocket message per ReceiveAsync
    var buffer = new ArrayBufferWriter<byte>();
    while (socket.State == WebSocketState.Open && !connection.IsClosed)
    {
        var result = await socket.ReceiveAsync(buffer.GetMemory(4096), http.RequestAborted);
        if (result.MessageType == WebSocketMessageType.Close) break;
        buffer.Advance(result.Count);
        if (!result.EndOfMessage) continue;
        if (!await connection.ReceiveAsync(buffer.WrittenMemory, http.RequestAborted)) break;
        buffer.Clear();
    }

    await connection.DisposeAsync();
    await send;
    if (socket.State == WebSocketState.Open)
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, connection.CloseReason.ToString(), CancellationToken.None);
});

app.Lifetime.ApplicationStopping.Register(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());

static ValidationResult RejectAll(in ValidationRequest request) => ValidationResult.Reject("read only");
```

Things the host owns:

- **Authentication and authorization.** `ConnectionContext` carries what the host decided: `Identity`, `Claims` and `IsReadOnly`. A read-only connection can sync and receive updates, but its own updates are dropped before they reach the hook, the store or other peers.
- **Message framing.** `ReceiveAsync` takes exactly one y-websocket message (sync, awareness, auth or query-awareness). It copies the bytes, so the host can reuse its buffer. An awareness message whose payload is not a well-formed awareness update closes the sender with `ProtocolError` and is not relayed.
- **Closing.** When `connection.Outgoing` completes, the server has closed the connection. `CloseReason` and `CloseDetail` say why. Disposing the connection tells the document the peer left.
- **Limits.** Message size limits at the socket, and `YServerOptions.Limits` for update contents.

### Validation hooks

`YServerOptions.Validation` is asked once per document, when it opens. A hook is a synchronous delegate:

```csharp
ValidationResult Hook(in ValidationRequest request)
```

The request carries:

- the document id;
- the document's current state, as a `Doc` the hook may read but not write;
- the update as V1 bytes and as a decoded `DecodedUpdate`;
- the sender's `ConnectionContext`;
- a `Deadline` token that fires after `ValidationDeadline`.

The hook returns `ValidationResult.Accept` or `ValidationResult.Reject(reason)`. When an update is rejected:

- the sender gets the reason in an auth permission-denied message;
- the sender gets the server's state so it can resync;
- after `MaxRejectsBeforeClose` rejects, the sender is closed with `TooManyRejects`.

A rejected edit stays in the client's own document, as y-websocket has no way to retract it. So a client that does not discard it will send it again on its next sync.

Documents with a hook hold a `Doc` in memory while they are in use; documents without one hold only the relay's merged update until the host edits, reads or subscribes.

### Server-side edits, reads and subscribers

The host never calls `Transact` on a served document itself. It posts the work to the document's mailbox, which runs it between messages (SRV-5, SRV-18). Each call loads the document from the store if no one has it open.

```csharp
await server.EditAsync("doc-1", tx => tx.Doc.GetText("text").Insert(0, "Hello from the server"));

var length = await server.ReadAsync("doc-1", doc => doc.GetText("text").Length);

await using var index = await server.SubscribeAsync("doc-1", doc => Index(doc.GetText("text").ToString()));
```

- **`EditAsync`** runs the edit in one write transaction. Its update goes through the relay check, the store append and the broadcast like an accepted peer update. It is the server's own edit, so the validation hook does not see it. The task completes once the update is stored and sent. If the edit throws or the append fails, nothing is stored or sent, the `Doc` is rebuilt from the relay, and the task fails.
- **`ReadAsync`** runs in one read transaction, after every message accepted before it.
- **`SubscribeAsync`** calls the callback once straight away, and again after every accepted update, in a read transaction on the worker. Like an observer, it must not block, keep the `Doc` or write to it. Disposing the subscription ends it.

### Idle documents

A document is idle while it has no connections and no subscribers (SRV-20).

- After `HotDocumentIdleTimeout` (one minute by default) of idle time, it drops its `Doc`. The next hook call, edit or read rebuilds it from the relay.
- After `DocumentIdleTimeout` (five minutes by default) of idle time, the mailbox closes and its relay state is dropped. The next connection, edit or read loads the document from the store again.

The store is authoritative, so nothing accepted is lost. Set either timeout to `Timeout.InfiniteTimeSpan` to keep that state until shutdown. `YServerOptions.TimeProvider` sets the clock.

`YServerOptions.DocumentUnloading` is called on the document's worker just before an idle document is unloaded, with the document id and its whole state as a V1 update, for a host that writes a snapshot then (SRV-23):

```csharp
var options = new YServerOptions
{
    Store = store,
    DocumentUnloading = (docId, state, ct) => snapshots.WriteAsync(docId, state, ct),
};
```

- Messages for the document wait while it runs. If something opens the document meanwhile, the document stays loaded, and the hook is called again when it next goes idle.
- Its token is cancelled after `DocumentUnloadingDeadline` (five seconds by default). The document is unloaded once the hook returns, throws or overruns, since the store already holds every accepted update.
- It is not called on shutdown or for a document deleted with `YServer.DeleteAsync`.

## Testing

- `tests/YSharp.Server.Tests` covers the update log, compaction and recovery, storage conformance, the mailbox and validation.
- `Simulation/ServerSimulationTests` runs seeded y-websocket peers through the server. The runs include drops, offline edits, restarts and compaction, and check that every peer, and a fresh peer loading from the store, ends in the same state as a `Doc` that applied every edit directly.
- `tests/YSharp.Tests/Codecs/RelayTests.cs` pins `YRelay` to the Yjs oracle.
