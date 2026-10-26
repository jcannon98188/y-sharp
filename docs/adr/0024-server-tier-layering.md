# Server tier: layering, the per-document mailbox and validation

Decides the layout that blocks TASK-5. The server tier sits above the core and does not change it. Integration order, update encodings, ADR 0006 and the existing suite (ADR 0022) stay as they are. The four owner decisions were made on 2026-10-26 (TASK-5.1): assembly placement, the storage API, the backend and the hook shape. Each guarantee has an ID, and tests carry it in the traceability tag.

## Assemblies and packages

- **SRV-1** The relay type (TASK-5.2) lives in `src/YSharp`, in `Codecs/`, next to `YUpdate`.
  - It is a codec primitive. It folds updates and answers diffs without a `Doc`.
  - It uses the internal struct lists of `UpdateMerge`, so it does not re-parse its merged update on every fold.
  - It is tested against the oracle like the rest of the core.
  - It adds no dependency.
- **SRV-2** Everything else goes in a new project, `src/YSharp.Server`, which references `YSharp`. That covers the mailbox, the storage interface and in-memory store, validation, the hot-Doc lifecycle and connection handling.
  - Its tests go in a new `tests/YSharp.Server.Tests`.
  - It reaches the core only through public API: no `InternalsVisibleTo` crosses the package line. If it needs a core internal, a task against the owning core module makes that internal public (ADR 0021).
- **SRV-3** The two assemblies ship as two packages, `YSharp` and `YSharp.Server`.
  - `YSharp` stays pinned to the oracle (ADR 0001, 0003), and its version tracks the Yjs port.
  - `YSharp.Server` has its own version. Its dependency on `YSharp` has a lower bound and no upper bound below the next major.
  - A breaking server change does not bump the core, and a core patch needs no server release.

## The mailbox and the ADR 0006 lock

The ADR 0006 lock makes one `Doc` safe across threads. It does not order work across await points, apply backpressure or keep documents from starving each other, because it is synchronous and thread-affine (CC-15). The mailbox adds those and leaves the lock unchanged.

- **SRV-4** Each document has one mailbox. It is a single-reader channel drained by one worker loop. Apply, validate, persist and broadcast for that document run only on that loop, one message at a time, in arrival order. Different documents have different loops and run in parallel.
- **SRV-5** Only the mailbox worker takes a write transaction on a server-held `Doc`.
  - Other threads may take read transactions, for indexing or queries (CC-1 shared reads). They see the state before or after an applied update, never a partial one (CC-4).
  - A server-side edit is posted to the mailbox as a message; it does not call `Transact` directly.
- **SRV-6** No `await` happens while a transaction is open. The worker awaits I/O (persist, a slow subscriber) only between transactions. CC-15 already forbids holding a transaction across `await`; the mailbox is where this is enforced.
- **SRV-7** Observers on a server-held `Doc` still run synchronously on the committing thread (CC-11), which is the mailbox worker. They must not block or do I/O.
  - Broadcast does not come from observers. The worker broadcasts the accepted update bytes it already holds after they are persisted, and encodes them once into one shared buffer for every subscriber (TASK-5.6).
  - The `update` event is not used for fan-out, so the order of broadcasts is the order of the mailbox.
- **SRV-8** For each accepted update the order is: structural validation, then semantic validation (if a hook is registered), then persist, then apply to the hot `Doc` (if one is held), then broadcast.
  - Persisting before applying means a failed persist leaves the hot `Doc` and the log agreeing. The update is rejected, and the `Doc` never moves ahead of what was stored.
  - Persisting before broadcast means no peer sees an update that a restart would lose.
- **SRV-9** What the mailbox guarantees that the lock does not:
  - per-document FIFO across asynchronous steps;
  - durable-before-visible;
  - a bounded queue with a defined backpressure policy (TASK-5.6);
  - a slow subscriber or a slow store cannot block other documents;
  - shutdown drains accepted messages before it stops.

## Storage

- **SRV-10** The storage abstraction is an asynchronous per-document log in `YSharp.Server`. It has four operations, all returning `ValueTask`, and all taking a `CancellationToken`:
  - **`AppendAsync(docId, update)`** returns a sequence number that increases strictly per document.
    - It completes only once the record is durable.
    - The update is stored in V2.
  - **`ReadSinceAsync(docId, afterSeq)`** returns the records after `afterSeq`, in order, as an `IAsyncEnumerable` of (sequence, update).
    - The compacted snapshot, if there is one, comes first, and carries the sequence it covers.
  - **`CompactAsync(docId, uptoSeq, mergedV2)`** atomically replaces every record up to `uptoSeq` with one merged V2 update.
    - Records appended after `uptoSeq` survive.
    - A reader sees either the old records or the snapshot, never neither and never both.
  - **`DeleteAsync(docId)`** removes the document's log.
- **SRV-11** Compaction merges with the relay tier, with no `Doc` loaded (TASK-5.4). Because merging is idempotent and commutative in effect, replaying a record twice after a crash cannot change the state (TASK-5.4 #5).
- **SRV-12** y-sharp ships **no concrete backend** (owner decision, 2026-10-26).
  - Users plug in their own store by implementing the interface (a database, blob storage, files, or whatever their host uses).
  - y-sharp ships the interface, the in-memory store used by tests (TASK-5.4), and a reusable conformance suite.
  - The conformance suite runs every crash-at-each-step, partial-write, corrupted-tail and concurrency test against any implementation. A user's backend is correct when it passes.
  - TASK-5.5 changes accordingly: from "first concrete backend" to that conformance suite and its contract for torn and corrupted records.

## Validation

- **SRV-13** There are two tiers.
  - **Structural** validation runs on every write, through the relay tier alone. It checks `YLimits`, well-formedness and the protocol message shape, and needs no `Doc`.
  - **Semantic** validation (authorization, schema) is opt-in, per document. It needs a hot `Doc` held by the mailbox.
  - A document with no hook registered never materializes a `Doc` for validation.
- **SRV-14** The hook is a synchronous delegate (owner decision, 2026-10-26).
  - **Inputs:**
    - the current `Doc`, read-only;
    - the incoming update, as its bytes plus a decoded read-only view of its structs;
    - the connection context (identity, claims, read-only flag);
    - a `CancellationToken` that fires at a configured deadline.
  - **Result:** `Accept`, or `Reject(reason)`.
  - **How it runs:** on the mailbox worker, inside one read transaction (ADR 0006: synchronous and thread-affine).
  - **Faults:** a hook that throws or overruns its deadline is treated as `Reject`. The deadline is cooperative: a hook that ignores the token is caught when it returns, and is then rejected. The exact type shapes belong to TASK-5.6.
- **SRV-15** The hook runs before persist and broadcast. A merged CRDT edit cannot be rolled back, and an update applied and then undone would still leave its tombstones and clock in every peer.
- **SRV-16** When an update is rejected:
  - it is dropped: never persisted, applied or broadcast;
  - the sender receives a y-protocols auth `permission-denied` message with the reason;
  - the sender is resynced: the server sends Step1 and Step2 from its own state.
  - **The edit is not undone on the client.** A resync cannot remove an edit the client has already integrated. The client keeps it, and resends it the next time it syncs, so it is rejected again.
  - **Getting the client back in line:** a client that wants to converge must discard its local `Doc`, or the server closes the connection after repeated rejects. The policy is configurable.
- **SRV-17** Read-only connections are enforced at the protocol layer, with no `Doc`:
  - sync `Update` and `Step2` messages from such a connection are dropped;
  - `Step1` is still answered, so the connection can read;
  - awareness is unaffected.
  - Finer rules, such as writing only within a subtree or keeping to a schema, need the semantic tier and so a hot `Doc`.

## Hot-Doc lifecycle

- **SRV-18** A `Doc` is materialized for a document only when something needs content:
  - a semantic hook is registered;
  - a server-side edit or read is posted;
  - an indexing subscriber asks for one.
  - The relay state alone serves sync and fan-out.
- **SRV-19** Rehydration builds the `Doc` inside the mailbox from storage: the compacted snapshot, then the records after it, applied in sequence order. Messages that arrive meanwhile wait in the mailbox, so the `Doc` never misses an update.
- **SRV-20** Idle eviction:
  - A hot `Doc` is evicted after a configurable idle time with no connections and no queued messages.
  - Eviction drops the `Doc` reference inside the mailbox. The relay state and the log stay authoritative, so nothing is lost.
  - The relay state of a document with no connections may be evicted too, and is rebuilt from storage on next open.
- **SRV-21** While a `Doc` is hot, its state and the relay state agree after every accepted update: both are built from the same accepted sequence. A test checks that `EncodeStateAsUpdate` of the hot `Doc` matches the relay's merged update in content.

## Considered

- **Everything in `YSharp.Server`.** The relay would get only the public `YUpdate` functions, so it would re-parse the whole merged update on every fold. Rejected.
- **Everything in `YSharp`.** Server concerns and their dependencies would ship to every client. Rejected.
- **A synchronous storage interface.** It would hold a thread per in-flight write across many documents. Rejected.
- **A concrete backend in the box (SQLite or files).** The owner chose a pluggable interface with a conformance suite instead. A reference backend can be added later as its own package without changing this ADR.
- **An asynchronous hook on a snapshot.** It could await external services, but needs a `Doc` copy per update. Rejected. An external check can run before the update is posted to the mailbox.
- **Broadcasting from `update` observers.** That would put I/O on the commit thread and fan out before persist. Rejected (SRV-7, SRV-8).
- **Validating after apply and undoing on reject.** CRDT edits cannot be truly rolled back. Rejected (SRV-15).
