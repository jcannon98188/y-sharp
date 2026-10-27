# YSharp.Server

Serves YSharp documents to y-websocket-compatible Yjs clients. The host owns the sockets and authentication; the server takes and emits whole y-websocket messages (sync, awareness, auth).

```csharp
using YSharp.Server;

await using var server = new YServer(new YServerOptions { Store = new MyDurableStore() });

// For each authenticated socket:
YConnection connection = await server.ConnectAsync(documentId);
// copy each incoming frame into connection.ReceiveAsync, and connection.Outgoing to the socket
```

- One mailbox per document: its work runs in order, and documents run in parallel. A slow peer is closed rather than holding the document up.
- Relay first: a document is served from its merged update, byte for byte as Yjs would serve it. A full `Doc` is built only for validation hooks and server-side edits, reads and subscribers.
- Every update is checked, persisted, then broadcast, so the log never holds a refused update and nothing is sent before it is durable.
- Storage is an `IUpdateStore`: an append-only V2 log per document with compaction. The in-memory default is not durable; check your own backend with the `YSharp.Server.Testing` package.

- [Hosting the server](https://github.com/jcannon98188/y-sharp/blob/main/docs/server.md)
- [How y-sharp works](https://github.com/jcannon98188/y-sharp/blob/main/docs/guide.md)

MIT licensed.
