# YSharp

A pure C# port of [Yjs](https://github.com/yjs/yjs) v13, the CRDT library for building collaborative applications. A YSharp document and a Yjs document exchange updates, state vectors and sync messages byte for byte and converge on the same state.

```csharp
using YSharp;

var alice = new Doc();
alice.GetText("notes").Insert(0, "Hello world");

var bob = new Doc();
bob.ApplyUpdate(alice.EncodeStateAsUpdate());
Console.WriteLine(bob.GetText("notes").ToString()); // Hello world
```

- Shared types: `YText` (with formatting and deltas), `YArray`, `YMap` and the XML types; events and observers.
- The V1 and V2 update formats, merging and diffing updates, snapshots, relative positions, the undo manager, subdocuments and permanent user data.
- The sync, awareness and auth protocols of y-protocols.
- Thread-safe: one reader/writer lock per document. Bytes from peers are read through `Try` methods under configurable limits, and a refused update changes nothing.

Pinned to Yjs v13.6.33, lib0 v0.2.99 and y-protocols v1.0.7. For a server, see the `YSharp.Server` package.

- [How y-sharp works](https://github.com/jcannon98188/y-sharp/blob/main/docs/guide.md)
- [Repository, tests and design records](https://github.com/jcannon98188/y-sharp)

MIT licensed. YSharp ports Yjs, lib0 and y-protocols, which are MIT licensed too; their notices ship in THIRD-PARTY-NOTICES.md.
