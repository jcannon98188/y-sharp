# Yrs concurrency model

Research for TASK-1.19 (map: TASK-1). Feeds TASK-1.18 (thread-safety and concurrency contract).

Source: `C:\Users\Jason\source\repos\y-crdt-yffi-pendingstructs`, crate `yrs` 0.28.0 (`yrs/Cargo.toml:3`). Paths below are relative to `yrs/src/` unless prefixed. Lock semantics for `async-lock` 3.4.2 come from that crate's source in the local cargo registry (the version pinned in `Cargo.lock`). `target/` was ignored.

## TL;DR

- A `Doc` is a cloneable handle: `Arc<StoreInner>` holding one `async_lock::RwLock<Store>` plus an `ArcSwap<Options>`. `Send`/`Sync` is asserted with `unsafe impl`, not derived.
- Exactly one lock per doc. Read transaction = shared guard, mutable transaction = exclusive guard. The guard is held for the whole life of the transaction, including commit and every observer callback.
- The lock is not re-entrant and has no deadlock detection. Same-thread nesting with the blocking API hangs; the `try_` API returns an error.
- Observers run synchronously on the committing thread, inside the write lock, in `Drop`/`commit`.
- Subdocs have their own independent lock. The parent's write lock is held while the code takes the child's write lock (parent -> child order), and nothing prevents the reverse.
- Concurrency tests are thin: one multi-thread write test, three "wrap in `std::sync::RwLock`" tests, and observer re-entrancy unit tests. No stress, loom, or Miri tests.

## 1. How `Doc` is shared

- `Doc` is `#[repr(transparent)] #[derive(Clone)] struct Doc { store: DocStore }` (`doc.rs:53-57`).
- `DocStore` is `#[repr(transparent)] struct DocStore(Arc<StoreInner>)` (`store.rs:429-431`).
- `StoreInner { options: ArcSwap<Options>, store: RwLock<Store> }` (`store.rs:508-512`). `RwLock` is `async_lock::RwLock` (`store.rs:19`).
- `Doc::clone` bumps the Arc. Clones are the same document; `Doc::ptr_eq` compares Arc pointers (`doc.rs:419-421`). `PartialEq for Doc` is by guid, not identity (`doc.rs:428-432`). Subdocs are keyed by `DocAddr`, the Arc pointer address cast to `usize` (`doc.rs:625-634`).
- `unsafe impl Send for Doc {}` and `unsafe impl Sync for Doc {}` (`doc.rs:59-60`). The compiler would not derive these, because `Store` contains raw-pointer structures. The same trick is applied to the pieces of the store:
  - `ClientBlockList` (`Vec<UnsafeCell<Block>>`): `block_store.rs:15-27`
  - `ItemPtr`: `block.rs:441-442`
  - `BranchPtr` (`NonNull<Branch>`): `branch.rs:28-31`
  - `AtomicRef<T>`: `atomic.rs:33-34`
- Soundness therefore rests on one invariant: every access to the `Store` (blocks, branches, items) goes through the `RwLock` guard. The unsafe impls are only valid if that holds (see section 3 item 8 for a place where it does not).
- `Options` (client id, guid, `should_load`, ...) live outside the lock in an `ArcSwap` (`store.rs:480-505`). `Doc::client_id()`, `guid()`, `should_load()` etc. are lock-free reads (`doc.rs:167-212`). Updates use `rcu`, e.g. `set_should_load` (`store.rs:485-494`).
- FFI escape hatch: `into_raw`/`from_raw`/`as_raw` expose the Arc pointer (`doc.rs:119-138`), used by yffi.
- Shared types (`TextRef`, `MapRef`, ...) hold a `BranchPtr`: a raw pointer into the store, with no reference back to the doc and no lock. They are `Send + Sync` through `BranchPtr` (`branch.rs:30-31`). `TransactionAcqError::DocumentDropped` exists (`transact.rs:233-234`) but is not constructed anywhere in `yrs/src` in this version, so a shared type cannot start its own transaction; you always pass one in.

## 2. The transaction lock

Everything is in `transact.rs` (trait `Transact`, impl for `Doc` at `transact.rs:89-135`) plus `store.rs:433-478`.

| API | Lock call | Behaviour when contended |
|---|---|---|
| `try_transact()` | `RwLock::try_read` (`transact.rs:91`) | `Err(SharedAcqFailed)` |
| `try_transact_mut()`, `try_transact_mut_with(origin)` | `RwLock::try_write` (`transact.rs:98,108`) | `Err(ExclusiveAcqFailed)` |
| `transact()` | `read_blocking` (`transact.rs:127`) | blocks the OS thread |
| `transact_mut()`, `transact_mut_with(origin)` | `write_blocking` (`transact.rs:122,132`) | blocks the OS thread |
| `AsyncTransact::transact / transact_mut / transact_mut_with` | `read()` / `write()` futures (`transact.rs:157-186`) | `.await` |

Details:

- Primitive: `async_lock::RwLock<Store>`. Per its source (`async-lock-3.4.2/src/rwlock.rs:21-22`, `rwlock/raw.rs:26-44`) it is write-preferring and built from an inner async `Mutex<()>` taken by writers, an `AtomicUsize` state (bit 0 = writer present or pending, upper bits = reader count), and two `event_listener::Event`s (`no_readers`, `no_writer`). Writers are never starved: once a writer waits, new readers queue behind it. The `*_blocking` methods are `.wait()` on the same futures (`rwlock.rs:278-280, 573-575`), i.e. a thread park.
- No owner tracking: `raw.rs` has no thread id or owner field, so the lock cannot distinguish "same thread" from "other thread" and cannot be re-entrant. Guards are `Send` when `T: Send`/`Sync` (`rwlock.rs:722, 1219`), so a transaction can move between threads or live across an `.await`.
- Read vs write: many `Transaction` (read) may coexist; one `TransactionMut` excludes everything, readers included (`transaction.rs:453-478`). The guard is a field of the transaction (`transaction.rs:458-460, 487-488`).
- Lifetime of the lock: `TransactionMut` auto-commits in `Drop` (`transaction.rs:530-534`). `Drop::drop` runs before fields are dropped, so the lock is released only after commit and all callbacks finish.
- No upgradable read is used. A reader cannot become a writer; drop the read transaction and take a write one (non-atomic).
- wasm: on `target_family = "wasm"` the "blocking" variants are replaced with `try_read().unwrap()` / `try_write().unwrap()`, i.e. they panic on contention instead of blocking (`store.rs:453-456, 471-474`; docs at `transact.rs:61-62`).
- Doc-comment drift: `Doc::get_or_insert_text/map/array/xml_fragment` document "it will panic" if another transaction is active (`doc.rs:225-229`), but they call `transact_mut()` (`doc.rs:231`), which blocks on native targets and only panics on wasm. The test comment at `transact.rs:257` says `try_transact_mut().unwrap()` "will hang forever"; `try_` cannot hang, it would panic with `ExclusiveAcqFailed`. Treat these comments as unreliable and the code as the contract.
- Root types: creating one takes a short-lived write transaction (`doc.rs:230-295`), so doing it while another transaction is open on the same thread self-deadlocks. Docs recommend declaring all root types up front (`doc.rs:229`, `lib.rs:65-69`).
- Doc-level observer registration takes the write lock: `Doc::observe_*` uses `try_transact_mut()?` (`doc.rs:73-111`), so it returns `Err` rather than blocking when a transaction is live, including from inside a callback on that doc.
- Cargo feature `sync` is off by default (`Cargo.toml:13-17`). It only changes observer closure bounds from `FnMut + 'static` to `FnMut + Send + Sync + 'static` (`doc.rs:80-101`, `store.rs:537-544`, `branch.rs:197-204`, `types/mod.rs:286-339`, `undo.rs:24-96`). It does not change the lock or `Doc`'s unsafe `Send`/`Sync`: `Doc` is `Send + Sync` even with the feature off, so a build without `sync` can still share a `Doc` across threads while its callbacks escape thread-safety checks.

## 3. Re-entrancy and deadlock cases

The lock is non-re-entrant and unmonitored. Cases derived from the code:

1. Same-thread nested write. `doc.transact_mut()` while a `TransactionMut` for that doc is live on the same thread hangs forever (blocking), panics (wasm), or returns `ExclusiveAcqFailed` (`try_`).
2. Read while writing. `doc.transact()` while the same thread holds a `TransactionMut` hangs. Because the lock is write-preferring, a nested read can also hang if a writer is queued behind an existing reader on the same thread.
3. Inside observers. Callbacks run under the write lock (section 4). Calling `doc.transact()`, `doc.transact_mut()`, any `get_or_insert_*`, or `Doc::observe_*` (returns `Err`) on the same doc from a callback self-deadlocks (or errs for `try_`). Callbacks get `&TransactionMut`, so reading through that reference is the supported path. The same applies to `Doc::parent_doc()`, `Doc::branch_id()` and `Doc::load(...)`, which take `self.transact()` internally (`doc.rs:359, 409-417`).
4. UndoManager. `undo()/redo()` (async) and `undo_blocking()/redo_blocking()` take the write lock via `transact_mut_with(origin)` (`undo.rs:585, 619`). Docs warn "no other transaction on that same document can be active ... Otherwise, it may cause a deadlock" (`undo.rs:502-506, 520-524, 543-547, 561-565`). `UndoManager` also takes read transactions (`undo.rs:428`).
5. Subdoc lock order (ABBA risk). Under the parent's write lock the code takes the child's write lock:
   - on integrate of a `Doc` item: `doc.transact_mut()` (`block.rs:867-872`);
   - in commit step 10, for each added subdoc: `subdoc.transact_mut()` (`transaction.rs:1153-1161`);
   - in `Doc::destroy`: `self.transact_mut()` then recursion into children (`doc.rs:371-377`), called from the parent's commit (`transaction.rs:1181-1183`).

   The order is parent -> child. Nothing stops user code taking child -> parent (for example a child observer calling `child.parent_doc()` then `parent.transact_mut()`), which deadlocks against another thread running the parent -> child path. There is no lock hierarchy and no documentation of this.
6. Cross-doc deadlocks in user code: two docs, two threads, opposite lock order is a plain ABBA. Yrs does nothing about it.
7. Guard held across `.await` or moved between threads is allowed (guards are `Send`), so an async task can hold the write lock over a suspension point and starve everyone else. A usage hazard, not a library bug.
8. Observer registration race (type-level). `Observable::observe`/`observe_deep` on shared types take no lock: they build a `BranchPtr` from the type and call `Branch::observe`, which does a `HashMap::insert` through the raw pointer (`types/mod.rs:299-315, 399-411`; `branch.rs:502-538`; `observer.rs:26-30`). A subscribe on one thread racing a commit on another is a data race, contradicting the "all access under the lock" invariant. (`Observer::unsubscribe` takes `&self` and only touches an `ArcSwapOption`, so unsubscribe is safe, `observer.rs:33-40`.) Doc-level `observe_*` do not have this problem, since they take the write lock.
9. Panic in a callback. async-lock does not poison. A panic unwinds out of `commit` (inside `Drop`) and releases the guard, but the store's `events` were `take()`n out before firing and are put back only afterwards (`transaction.rs:1091, 1105, 1145, 1167`), so afterwards the store may be left half-committed with `events == None`. Inferred from the code, not tested.

## 4. Observers: which thread, which lock

- Observers are stored per store (`StoreEvents`, `store.rs:556-582`) and per branch (`branch.rs:192-194`) in `Observer<F>` (`observer.rs:7-59`): a `HashMap<Origin, ArcSwapOption<F>>`. The key is a user-supplied `Origin`; subscribing with an existing key replaces the callback.
- Trigger path: `TransactionMut::commit` (`transaction.rs:1084-1185`), called from `Drop` (`transaction.rs:530-534`) or explicitly. Order:
  1. before-observer-calls (`:1091`)
  2. type observers, then deep observers (`call_observers`, `:1031-1075`, `:1097`)
  3. after-transaction (`:1105`)
  4. GC, delete-set squash, block merge (`:1111-1142`)
  5. transaction-cleanup, update v1, update v2 (`:1145-1149`)
  6. subdocs added/removed, subdocs event, destroy (`:1153-1183`)
- Thread: always the thread that drops/commits the `TransactionMut` (or the executor thread polling the task, for the async API). No dispatcher, queue or thread-pool hop. A remote update applied via `apply_update` on thread X fires callbacks on thread X.
- Lock: the exclusive write guard is still held (section 2). Callbacks receive `&TransactionMut` (and `&mut TransactionMut` for after-transaction only, `store.rs:550`). So observers see a consistent, quiescent store but cannot take another transaction on this doc (section 3).
- `Observer::trigger` takes `&mut self`, walks callbacks, then `drain_pending` drops entries whose `ArcSwapOption` became `None` (`observer.rs:43-58`). It converts the `ArcSwap` content to `&mut F` through `as_raw().as_mut()`, commenting that it "should never be at risk of multiple concurrent calls" (`observer.rs:47-49`); that is only true because commit holds the exclusive lock.
- Unsubscribe/replace during trigger is pinned by unit tests: a callback replaced during trigger does not run again in the same trigger (`observer.rs:150-174`); a callback unsubscribed by another callback in the same trigger is skipped (`observer.rs:176-201`).
- Events are moved out of the store while firing (`transaction.rs:1091-1093, 1105-1107, 1145-1150`), so a callback cannot see the doc's own subscription list through the store while it fires.
- Callback bounds: only with feature `sync` must callbacks be `Send + Sync` (section 2).

## 5. Subdoc handling

- A subdoc is an independent `Doc` (own Arc, own lock, own `Store`) embedded as `ItemContent::Doc(Option<Doc> /*parent*/, Doc)` (`block.rs:867`, `doc.rs:153-157, 440-450, 609-620`). `Store.parent: Option<ItemPtr>` marks it (`store.rs:58-60, 107-109`).
- `Store.subdocs: HashMap<DocAddr, Doc>` (`store.rs:54`) is the parent's registry. It is mutated only in commit step 10 (`transaction.rs:1161-1164`).
- Integration under the parent lock: integrating a `Doc` item sets `parent_doc = Some(txn.doc().clone())` and takes `doc.transact_mut()` to set `store.parent` (`block.rs:867-872`), then records `added`/`loaded` into the transaction's `Subdocs` (`block.rs:873-877`). Commit step 10 again takes `subdoc.transact_mut()` to copy the parent's client id (`transaction.rs:1153-1161`) and calls `set_subdoc_data`, an rcu on `Options` (`store.rs:496-505`). A subdoc shares the parent's client id but has its own guid (`doc.rs:163-177`).
- Strong parent reference: `parent_doc` is a full `Doc` clone held inside the parent's own store item, so a parent containing a subdoc holds a strong reference to itself and its Arc count cannot reach zero by dropping user handles (inferred; no `Weak` found, no test). `Doc::parent_doc()` (`doc.rs:407-412`, `transaction.rs:265-273`) needs a read transaction on the child.
- Events: `observe_subdocs` -> `SubdocsEvent {added, removed, loaded}` triggered under the parent's write lock at the end of commit (`transaction.rs:1167-1171`), then `destroy` on removed subdocs (`:1181-1183`).
- Load/destroy: `Doc::load(&mut parent_txn)` sets `should_load` via rcu (lock-free), then takes `self.transact()` on the child and writes into the parent txn's `Subdocs.loaded` (`doc.rs:353-367`). `Doc::destroy` takes the child's write lock, recursively destroys its subdocs, optionally swaps a fresh `Doc::subdoc` into the parent item, and fires `destroy_events` (`doc.rs:371-405`).
- Nothing is subdoc-aware for threading: no lock hierarchy, no `try_` fallback, and a child can be cloned out of the parent (`Out::YDoc(doc.clone())`, `block.rs:1822-1825`) and used from any thread.
- Tests (single-threaded, events only, no locking): `subdoc` (`doc.rs:1436`), `subdoc_load_edge_cases` (`doc.rs:1606`), `subdoc_auto_load_edge_cases` (`doc.rs:1703`).

## 6. Concurrency tests that exist

- `transact::test::multi_thread_transact_mut` (`transact.rs:243-274`): 3 threads clone `Doc` and `TextRef`, each calls blocking `transact_mut()`, sleeps 0-400 ms holding the lock, inserts "a" and drops; main waits on a `Barrier` and asserts `"aaa"`. The only test that exercises contention on the lock and the blocking write path.
- `multi_threading` in `types/text.rs:2607-2646`, `types/array.rs:1240-1271`, `types/map.rs:1335-1366`: 2 threads, `Arc<std::sync::RwLock<Doc>>` with `write().unwrap()` around each `get_or_insert_* + transact_mut`. They serialise through an outer std lock, so they do not test Yrs' own concurrency (they do show `Doc` is `Send + Sync` enough to be placed there).
- `observer.rs:67-250`: single-threaded unit tests for subscribe/unsubscribe/replace/drop, including mutate-during-trigger.
- `undo.rs` tests call `transact_mut()` in sequence on one thread.
- Hosts: yffi and ywasm use `try_transact*` and surface "busy" to the caller (`yffi/src/lib.rs:711, 739, 746`; `ywasm/src/lib.rs:84, 264, 298, 326-366`), the pattern for single-threaded hosts.
- Not found: stress tests, loom/Miri setup, fairness tests, `try_` under contention, observer-vs-subscribe races, subdoc lock ordering, async transaction contention.

## 7. What a C# port could copy

| Yrs | C# equivalent | Notes |
|---|---|---|
| `RwLock<Store>`, write-preferring | `ReaderWriterLockSlim` (`NoRecursion`) | `NoRecursion` makes same-thread re-entry throw `LockRecursionException`, much better than a silent hang. Thread-affine (release on the acquiring thread) and not async-friendly. It also favours waiting writers, matching Yrs. |
| Async `transact().await` | `SemaphoreSlim(1,1)` for writers, or a hand-rolled/Nito-style async reader-writer lock | `SemaphoreSlim` is a mutex, not a reader/writer lock; if shared read transactions are not needed, a plain async mutex is the simplest sound choice. |
| `try_transact*` returning `Result<_, TransactionAcqError>` | `TryEnterReadLock(0)` / `TryEnterWriteLock(0)`, or `SemaphoreSlim.Wait(0)` | Keep the explicit shared/exclusive acquisition-failed error kinds. |
| Guard inside the transaction, commit in `Drop`, lock released after observers | `IDisposable` transaction; `Dispose()` commits then exits the lock in `finally` | Same ordering: commit first, release last. |
| `Doc` as `Arc` handle | A class (reference type) | Identity = reference identity. Do not copy `PartialEq` by guid. |
| `ArcSwap<Options>` | Immutable record in a `volatile` field, `Interlocked.CompareExchange` for rcu | Lock-free reads of client id, guid, should-load. |
| `Observer<F>` keyed callbacks | Copy-on-write array/dictionary swapped with `Volatile`/`Interlocked`, or `ConcurrentDictionary` | A snapshot-per-trigger gives the same "unsubscribe during trigger is skipped / replace not re-run" behaviour and makes subscribe thread-safe, closing section 3 item 8. |
| `sync` feature (Send/Sync bounds) | Nothing | .NET has no equivalent; document which thread callbacks run on. |

Suggested contract choices (inputs for TASK-1.18):

- One lock per `Doc`; non-re-entrant, but make violations detectable (`NoRecursion` or an owner check) instead of hanging.
- Offer `TryTransact*` and blocking variants; async variant optional.
- Run observers synchronously on the committing thread, under the write lock, in the commit order in section 4.
- Define an explicit lock hierarchy for subdocs (parent before child) and document that observers must not take another doc's lock in the opposite order.
- Make type-level observer subscription safe at all times (do not copy the unlocked `HashMap` insert).

## 8. Yrs behaviours that are accidents of Rust, not a contract

- Blocking `transact_mut()` versus the documented panic: the docs claim a panic (`doc.rs:225-229`) while the code blocks on native and panics only on wasm (`store.rs:448-474`). Not a stable contract.
- Unconditional `unsafe impl Send/Sync` for `Doc`, `BranchPtr`, `ItemPtr`, `ClientBlockList`: the author asserts what the borrow checker cannot see; `sync` only affects callback bounds. A C# port has no equivalent and must choose its own promise (probably "thread-safe via the doc lock; handles are only valid inside a transaction").
- RAII-driven semantics: commit-on-drop and "lock held through callbacks" fall out of `Drop`. In C#, `Dispose` can be skipped or called twice, and finalisers do not release locks; the port must handle double-dispose and exceptions thrown by observers explicitly. Rust's panic behaviour in `Drop` is not a guarantee either (section 3 item 9).
- Compile-time exclusivity: `&mut TransactionMut` prevents two writers being used at once on a thread; shared types are pointer-like handles valid only while the doc lives. None of that carries to C#.
- Observer re-entrancy "works" through unsafe `&mut F` from an `ArcSwap` guard relying on the lock (`observer.rs:47-49`); the skip-on-unsubscribe / no-rerun-on-replace outcomes are tested (`observer.rs:150-201`) but incidental to that design.
- Which `Doc` members need a transaction (`parent_doc()`, `branch_id()`) versus which are lock-free (`client_id()`, `guid()`) is an implementation detail.
- The `parent_doc` strong clone inside the child item (section 5) looks like an Arc cycle; a GC'd runtime should not copy it.
- Not accidents (worth copying): write transactions exclusive against readers; the commit/event order; observers see post-integration state under the lock; update v1/v2 events fire only when there were changes (`store.rs:585-593`); subdoc event contents.

## Open questions for later tickets

- Does the C# port need concurrent shared read transactions at all, or is a single mutex enough? (Affects the cost of read paths.)
- Should the port expose per-type `observe` without a transaction, as Yrs does? That needs the lock-free subscription design above.
- Should transactions be allowed to cross `await`? Yrs allows it; the cost is write-lock starvation.
