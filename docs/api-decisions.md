# Open API decisions

**For the project owner. Section A is decided (below); sections B to D are open (except D2, resolved) and nothing in them has been changed.** Each item is a question the test suite hit while compiling against the API stub (ADR 0008, 0019) and could not settle alone. Sources: the TASK-2 implementation notes (`backlog task view 2 --plain`), `// API gap` comments in `tests/`, and the registry (`docs/inventory/`). Where a test works around a gap, the workaround is named so it can be undone when you decide. Recommendations are the suite author's view, not decisions.

IDs: `Q`, `ENC`, `AW`, `CC`, `ERR`, `AU` are registry entries (ADR 0010).

## A. Public API shape (ADR 0008)

Section A is decided; the decisions are recorded in ADR 0008 (and ADR 0003, 0018 where noted) and applied to the API stub and the tests. Each item keeps its question and the places that were affected.

### A1. Client ids: `uint` or 53-bit
**Decided: `ulong` everywhere.**
- **Question.** `StateVector`, `ItemId` and `DocOptions.ClientId` used `uint`, but the wire (varuint) and Yjs accept client ids up to 2^53-1 (Q12, ENC-37, AW-34); Yjs itself generates 32-bit ids only.
- **Decision.** The public type is `ulong` in every signature: `ItemId`, `StateVector`, `DocOptions.ClientId`, `Doc.ClientId`, `IClientIdGenerator`, awareness client ids and events, `PermanentUserData`. Ids up to 2^53-1 are accepted and round-trip. A wire id of 2^53 or more stays Malformed (a recorded divergence, ADR 0003). Clocks stay `long`. Generated ids stay 32-bit as in Yjs.
- **Tests.** The Q12/ENC-37/AW-34 tests assert that a 2^53-1 id round-trips through the document, state vector, update and awareness encoding instead of only being accepted by `TryApplyUpdate`; the Malformed test for 2^53 and above is kept. New fixtures cover 53-bit client ids in the structs and awareness areas, and the hostile corpus accepts client ids below 2^53.
- **Still excluded** as not-applicable: ENC-26 (`obfuscateUpdate`) and ENC-57/58/59 (`parseUpdateMeta`, `logUpdate`, JS-typed arguments).

### A2. Root `YXmlText`
**Decided: add `Doc.GetXmlText(name)`.** It creates a root `YXmlText`, as JS `ydoc.get(name, Y.XmlText)` does; `YText` and `YXmlText` are different constructors, so asking for the other one on the same name throws `YTypeConflictException`. The SharedTypeTests, TextTests and UndoPortTests ports that put the XmlText in a fragment or an embed now use a root XmlText.

### A3. `YArray.Slice`, `ForEach`, `Map`
**Decided: add `YArray.Slice(start, end)` only.** Negative indexes count from the end, as in JS; `end` defaults to the length. `ForEach` and `Map` are not added: `foreach` and LINQ over the enumerator cover them. The ArrayTests slice port uses `Slice`.

### A4. `Transact` return value
**Decided: add `Transact<TResult>(Func<Transaction, TResult>, origin)` and the state-passing `Transact<TState, TResult>`; rename the timeout overload to `TransactWithTimeout(body, origin, timeout, ct)`.** The rename removes the trap where `Transact(body, null, timeout)` bound to the state-passing generic overload; the tests no longer need named arguments. See ADR 0008.

### A5. `AfterAllTransactions` transaction list
**Decided: expose the list.** `AfterAllTransactions` handlers receive `IReadOnlyList<Transaction>`, the transactions of the cleanup round in order. The list is pooled and valid only during dispatch, like events.

### A6. Item identity is not public
**Decided: keep hidden; no API change.** Items and structs stay internal and there is no `ItemView` beyond the id and deleted flag, because public struct identity would commit the layout (ADR 0018). Tests use the `structs` dump seam (section C). Recorded in ADR 0008.

### A7. `YEvent` lifetime
**Decided: keep events pooled and valid only during dispatch; no `Snapshot()` copy unless users ask.** `YEvent.GetPath()` is relative to `CurrentTarget` (the Q25 fixture already assumes this). Recorded in ADR 0008.

### A8. Other stub gaps
**Decided: add `UndoManagerOptions.Doc` and `Doc.GetSubdocGuids()`; allow a subdoc inside a `YXmlFragment` (`InsertSubdoc(index, doc)`); expose loaded and synced as the existing `Loaded` and `Synced` events.** There are no `whenLoaded`/`whenSynced` tasks, because ADR 0008 puts async APIs out of scope. The SubdocTests undo port now uses a fragment, and the UndoPortTests empty-scope port uses `UndoManagerOptions.Doc`. RP-05 and PUD-05 still follow JS.

## B. Behaviour and error semantics

### B1. Limit semantics
- **Question.** `YLimits` has `MaxUpdateBytes`, `MaxAnyDepth`, `MaxStructsPerUpdate`, `MaxStringBytes` (and `MaxPendingBytes`) but does not say: inclusive or exclusive; wire size or decoded size; whether the check comes before any allocation; whether limits apply to V2 columns and to merge, diff and convert as well as apply.
- **Bites.** Spec 11 hostile tests (HOS-LEN, HOS-DEEP, HOS-AMP) and `ErrorModelTests`.
- **Options.** Define "exceeded when value > limit", checked before allocation, on every decode path, leaving the document unchanged (ADR 0005 decode error).
- **Recommendation.** That option, then one boundary test per limit.

### B2. CC-9: exception type for stale views
- **Question.** ADR 0006 says live views never outlive their transaction. `HandleTests` (~211) accepts either old contents or an exception because the type is undesigned; `LifecycleTests` (~25) notes the `YConcurrencyException` the ADR describes is unreachable.
- **Options.** `InvalidOperationException`; `YConcurrencyException`; `ObjectDisposedException`.
- **Recommendation.** A misuse exception shared with the other lifetime errors, and make "old contents" illegal so the test tightens.

### B3. `TryTransact` under a held read lock
Unspecified whether it returns a failed status, waits, or throws when the calling thread holds a read transaction (an upgrade). Bites the CC tests in `LockModelTests` and `AcquireTests`. **Recommendation.** Return a failed status immediately (an upgrade cannot succeed without deadlock risk); the throwing form throws.

### B4. `Awareness.GetStates`: snapshot or live (AW-13)
JS returns the same live `Map` each call (AW-13, kind `match`); ADR 0008 says awareness states are returned as snapshots. **Recommendation.** Snapshot, and re-kind AW-13 as `diverge` with a `[Diverges]` test.

### B5. AU-5: null handler
A null permission handler is a TypeError at read time in JS (AU-5, `diverge`). Tests assume `ArgumentNullException`. **Recommendation.** Confirm, and throw at the call that receives the null, not later.

### B6. ERR-20: fault-injection seam
- **Question.** ERR-20 (an internal invariant violation throws `YInternalException` and faults the document) cannot be tested without a way to break an invariant. It is `pending`.
- **Options.** An `internal` fault hook visible to the test assemblies through the existing `InternalsVisibleTo`; corrupt the struct store through the `DumpStructs` seam's sibling; or leave it untested.
- **Recommendation.** The internal hook, documented in ADR 0005. ERR-19 also assumes an out-of-memory error from an observer propagates; confirm that.

### B7. Q22: clocks and lengths (ADR 0003)
Q22 (`diverge`, pending): JS clocks and lengths are doubles; y-sharp proposes `long` and a plain binary search, observably identical below 2^53. Open: negative, non-integer or above-2^53 values from the wire. **Recommendation.** Reject as Malformed, say so in ADR 0003, then write the Spec 6 test.

## C. Internal seams the tests require

`internal` members the implementation must provide, reached by `InternalsVisibleTo` or by reflection. Decide that these are accepted, and document them in ADR 0015.

| Seam | Needed by | Contract |
|---|---|---|
| `internal string Doc.DumpStructs()` | Spec 6 (`StructsSupport.cs`) | ADR 0016 `structs` dump as JSON |
| `internal bool Doc.HasPending` | Spec 6 | pending-struct presence |
| `internal int YText.CleanupFormatting()` | Spec 10 | counterpart of `cleanupYTextFormatting` |
| `internal static class YSharp.ScheduleSeam` (`Action<string>? Point`, `Action<string, Func<bool>>? Block`) | Spec 12 (CC-5, scenarios) | the lock reports its transitions; free when unset. The CC-18 lock-order checker is Debug-only |
| `Doc.CollectGarbage/ShouldLoad/AutoLoad/CollectionId/Meta/SignalLoaded/SignalSynced`, `UndoManager.Undoing/Redoing/CurrStackItem` | Spec 8 (by reflection) | members JS exposes that the sketch hides |

**Recommendation.** Accept all. Prefer `InternalsVisibleTo` to reflection once the code exists, and keep `ScheduleSeam` a static with a null check on the hot path (ADR 0009 budgets).

## D. Repository and process

### D1. 18 MB of simulation fixtures in git
`fixtures/sim` is 18 MB of the 20 MB `fixtures/` tree. The repository pack is 2.4 MiB compressed today, so the cost is modest, but it grows with every regeneration.
- **Options.** (a) Keep: no generation step for gating. (b) Git LFS: smaller clones, needs LFS in CI and for contributors. (c) Generate in CI and do not commit: no history growth, but the Windows and macOS gating jobs have no Node (ADR 0015), so they would need the files from an artifact.
- **Recommendation.** Keep (a); revisit if the pack passes about 50 MiB, and regenerate rarely.

### D2. Test method naming rule (resolved)
Decided: rename all. Every test method now follows `Subject_Condition_Outcome` (ADR 0015); upstream names live only in `[PortOf]`. The ported oracle tests are named for the behaviour they check, for example `Undo_Map_UndoesAndRedoesKeyChanges`.

### D3. Open, no decision needed yet
The randomized gating budget (~60 s) and the 2-minute gating budget are unmeasured until an implementation exists; the AFL++ job is untested; Spec 13's live differential properties are unrun and will probably need normalisers; Q25 is active under Spec 10.
