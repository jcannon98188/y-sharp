# Thread-safe through one re-entrant lock per document

Yjs is single-threaded, so there is no oracle behaviour for concurrency. y-sharp keeps oracle semantics unchanged and gets thread safety by serialising them behind a document-level reader/writer lock taken through transactions, as Yrs does (`docs/research/yrs-concurrency.md`). Where Yrs and Yjs differ, Yjs wins (ADR 0003): Yrs forbids nested acquisition, Yjs nests transactions.

Each guarantee has an ID. Tests carry the ID in the traceability tag (TASK-1.8).

## Guarantees

**Lock model**

- **CC-1** One lock per `Doc`. Write transactions are exclusive; read transactions are shared. A waiting writer blocks new readers.
- **CC-2** The lock is re-entrant per thread, with owner tracking. A nested write on the owning thread joins the current transaction. Writes made from observers run in a follow-up transaction after cleanup, as in Yjs. Another thread never joins a transaction.
- **CC-3** Upgrading from a read transaction to a write transaction on the same thread throws `YConcurrencyException`; it is not atomic.
- **CC-4** A read transaction never mutates document state, caches included (search markers and similar lazily updated state are suppressed or per-transaction).
- **CC-5** The lock is built only from `Monitor`, `Interlocked` and explicit state, so a systematic schedule tester can control it. No `ReaderWriterLockSlim`, no spin-waiting on internals.

**Acquiring**

- **CC-6** Blocking `Transact` forms take an optional timeout and `CancellationToken`, and throw `TimeoutException` or `OperationCanceledException`. `TryTransact*` never blocks and returns a result struct (`Acquired` or `Contended`). Signatures belong to TASK-1.14.
- **CC-7** An uncontended acquire and release allocates nothing. `TryTransact*` allocates nothing even when contended. Both are named hot paths for TASK-1.15. The contract has no numeric latency bound.

**Handles and options**

- **CC-8** Every public member of `Doc`, the shared types and the awareness classes is safe to call from any thread. A call outside an explicit transaction takes an implicit transaction (read or write as appropriate). An explicit `Transact` batches calls.
- **CC-9** Live views and enumerators never outlive the transaction that produced them: once it ends they throw `YLifetimeException` (ADR 0005); they never return old contents. Views taken without an explicit transaction (an implicit enumerator) are snapshots and never go stale.
- **CC-10** Options such as `ClientId`, `Guid` and `ShouldLoad` are read lock-free from an immutable snapshot.

**Observers**

- **CC-11** Observers and events run synchronously on the thread that commits, inside the write lock, in the oracle's commit order. A remote update fires on the thread that called `ApplyUpdate`. There is no dispatcher and no thread hop.
- **CC-12** `Observe`, `ObserveDeep` and `Unobserve` are safe at any time from any thread, including inside a callback, and do not take the doc lock.
- **CC-13** Each trigger runs a snapshot of the handler list taken when it starts. A handler removed during dispatch still runs in that dispatch, as in Yjs. A handler added during dispatch first runs in the next trigger. The second is a Divergence (JS TX-12 may call it); a tagged test shows the JS behaviour, and the oracle is run to confirm TX-12 first. Handler order is registration order and the same handler registered twice is called twice.
- **CC-14** Observer faults are contained as in ADR 0005, whichever thread the commit runs on.

**Transactions and lifecycle**

- **CC-15** A transaction is thread-affine: disposing it on a different thread than the one that acquired it throws `YConcurrencyException`. With the `ref struct` scope this cannot be written (the compiler forbids it), so the exception is unreachable for scopes; a `Transaction` object kept past its body throws `YLifetimeException` (CC-9). The contract is sync-only; there is no async API. The primary form is a callback that cannot cross `await`; whether the scoped form is a class or a `ref struct` belongs to TASK-1.14.
- **CC-16** If a `Transact` body throws, commit and cleanup still run, the lock is released in a `finally` block, and the exception then propagates. Double-`Dispose` is idempotent. A transaction that is never disposed leaks the lock; this is a caller error, not detected.
- **CC-17** `Destroy` is a write transaction and waits its turn. A destroyed document keeps accepting acquires, as in Yjs; only `IsDestroyed` changes.

**Subdocs and other components**

- **CC-18** Each subdoc has its own lock. The hierarchy is parent before child; no code takes a lock in the opposite order. A debug-build lock-order checker turns a violation into `YConcurrencyException` instead of a hang. Observers are told not to take another document's lock against this order.
- **CC-19** `Awareness` has its own re-entrant exclusive lock and never takes a doc lock. Its events fire synchronously under it, on the calling thread or the timer thread. Its timer uses an injected `TimeProvider`.
- **CC-20** `UndoManager` and `PermanentUserData` run every public member under the doc lock with an implicit transaction; `undo` and `redo` are write transactions. `StackItem.meta` is caller-owned and not protected.

## Test design

- **Tiers.** A short stress tier (seconds, capped threads, fixed seed) lives in `YSharp.Tests` and gates CI. A long soak tier runs nightly in `YSharp.Fuzz.Tests` with random seeds. Every failure prints the seed and operation log and replays from them. A single failure on a seed is a real bug: no retry, no quarantine. Runs use one thread and more threads than cores, to catch both ordering and oversubscription effects.
- **Invariants asserted.**
  1. Serializability: N threads apply random operations to one doc; the commit order seen through the `update` stream, replayed on a fresh single-threaded doc, gives a byte-identical `EncodeStateAsUpdate`. Where the operations are deterministic it is also compared with a Node oracle fixture.
  2. No torn reads: a concurrent read sees the state from before or after a commit, never between, checked with a content checksum kept under the write lock.
  3. Convergence: docs synced across threads end up equal.
  4. No deadlock: a watchdog timeout with a stack dump applies to every test.
  5. No leaks: afterwards the lock is free and no transaction is open.
  6. Read purity (CC-4): concurrent reads leave state unchanged.
- **Systematic exploration.** A separate, non-gating job explores small scenarios (two threads, a few operations): re-entrancy, writer against reader, subscribe against commit, the subdoc ABBA case, awareness timer against `SetLocalState`. The tool is a hand-rolled, seeded, cooperative scheduler: the lock and the observer list expose yield and block points that tests drive, and a failing seed replays exactly. Microsoft Coyote was rejected for now: release 1.7.11 targets net8.0 and net10.0 support is an unmerged pull request, it does not control `ReaderWriterLockSlim`, and its use with xUnit v3 is unverified (`docs/research/schedule-exploration.md`). Revisit it if net10.0 support ships. The scheduler explores only the points the seam exposes and does not model weak memory.
- **Seams.** The `TimeProvider` (CC-19) and the lock-order checker (CC-18) are injectable so tests can run without real waits.

## Considered

- **Exclusive-only lock.** Simpler, but costs read throughput on a shared doc. Rejected because CC-4 makes shared reads provable.
- **Copy Yrs (no nesting).** Hangs on patterns the ported JS tests use. Rejected under ADR 0003.
- **Tree-wide lock for subdocs.** Removes ABBA but serialises independent subdocs. Rejected in favour of per-doc locks plus a hierarchy and a checker.
- **Dispatching observers off-lock.** Would let observers see a doc that has already moved on and changes thread behaviour from the oracle. Rejected.
- **Async transactions.** Needs a non-re-entrant, non-thread-affine lock, which conflicts with CC-2. Out of scope.
