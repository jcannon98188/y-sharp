# The server tier

`YSharp.Server` serves Yjs documents to y-websocket-compatible clients. It sits above the core library and does not change it. The decisions behind it, and the guarantees with their `SRV-` and `RLY-` IDs, are in [ADR 0024](adr/0024-server-tier-layering.md). This page covers how the pieces fit together and how a host uses them.

## Layering

```
  host (ASP.NET Core, Kestrel, anything)      accepts sockets, authenticates, pumps bytes
        |  YServer.ConnectAsync / YConnection
  YSharp.Server                               one mailbox per open document
        |  structural validation -> validation hook -> store append -> relay -> hot Doc -> broadcast
        |-- IUpdateStore                      append-only V2 log per document (in-memory default, or the host's)
        |-- YRelay (in YSharp)                the document as one merged update; answers sync without a Doc
        `-- Doc (in YSharp)                   only for documents with a validation hook
  YSharp                                      the Yjs port: Doc, update functions, sync and awareness protocols
```

- **`YSharp`** is the core. Two pieces of it are built for servers. `YRelay` holds a document as its merged update and answers a client's state vector with a diff, byte for byte as Yjs `mergeUpdates` and `diffUpdate` would. `YUpdate.TryDecode` gives a read-only view of an update's structs and delete set without applying it.
- **`YSharp.Server`** holds the mailbox, the connection, the storage interface and its in-memory store, the update log and validation. It reaches the core only through public API.
- **`YSharp.Server.Testing`** holds `UpdateStoreConformance`, the conformance suite for a storage backend.

### One mailbox per document

`YServer` opens a mailbox for a document on its first connection. The mailbox is a bounded queue with a single worker, so everything that happens to one document happens in order. Different documents run in parallel and do not wait on each other. Every update a client sends goes through this pipeline (SRV-8):

1. **Structural validation.** The update is decoded in full against `YServerOptions.Limits` and converted to V2. A malformed or over-limit update closes the connection with `ProtocolError`.
2. **The validation hook**, if the document has one. It runs on a read-only view of the document with a deadline. A reject, a throw, a timeout or an attempt to write all count as a reject. The client is sent a permission-denied auth message and resynced.
3. **The store append.** It completes only once the update is durable. If it fails, the update is neither applied nor broadcast, and the sender is closed with `StoreFailure`.
4. **Apply** the update to the relay, and to the hot `Doc` if there is one.
5. **Broadcast.** The update is encoded once, and that one buffer goes to every other connection on the document.

A document with no validation hook never builds a `Doc`: it is served from the relay alone.

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
- **Message framing.** `ReceiveAsync` takes exactly one y-websocket message (sync, awareness, auth or query-awareness). It copies the bytes, so the host can reuse its buffer.
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

Documents with a hook hold a `Doc` in memory while they are open; documents without one hold only the relay's merged update.

## Testing

- `tests/YSharp.Server.Tests` covers the update log, compaction and recovery, storage conformance, the mailbox and validation.
- `Simulation/ServerSimulationTests` runs seeded y-websocket peers through the server. The runs include drops, offline edits, restarts and compaction, and check that every peer, and a fresh peer loading from the store, ends in the same state as a `Doc` that applied every edit directly.
- `tests/YSharp.Tests/Codecs/RelayTests.cs` pins `YRelay` to the Yjs oracle.
