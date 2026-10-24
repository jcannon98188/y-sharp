# Open API decisions

**For the project owner. Nothing here has been decided or changed.** Each item is a question the test suite hit while compiling against the API stub (ADR 0008, 0019) and could not settle alone. Sources: the TASK-2 implementation notes (`backlog task view 2 --plain`), `// API gap` comments in `tests/`, and the registry (`docs/inventory/`). Where a test works around a gap, the workaround is named so it can be undone when you decide. Recommendations are the suite author's view, not decisions.

IDs: `Q`, `ENC`, `AW`, `CC`, `ERR`, `AU` are registry entries (ADR 0010).

## A. Public API shape (ADR 0008)

### A1. Client ids: `uint` or 53-bit
- **Question.** `StateVector`, `ItemId` and `DocOptions.ClientId` use `uint`. The wire (varuint) and Yjs accept client ids up to 2^53-1 (Q12, ENC-37, AW-34); Yjs itself generates 32-bit ids only.
- **Bites.** `MalformedInputTests` ENC-37/Q12 (acceptance is asserted through `TryApplyUpdate` only), `AwarenessWireTests` AW-34, fixtures with client ids above 2^32. Registry entries excluded as not-applicable because the stub has no such members: ENC-26 (`obfuscateUpdate`), ENC-57/58/59 (`parseUpdateMeta`, `logUpdate`, JS-typed arguments).
- **Options.** (a) Keep `uint` and reject larger ids as Malformed (a recorded divergence, ADR 0003). (b) Change the public type to `ulong` everywhere, matching the wire. (c) Keep `uint` for generated ids, decode into `ulong` internally, and expose `ulong` only on read APIs.
- **Recommendation.** (b), unless interop with Yjs peers using large ids is out of scope, in which case (a) with an explicit `[Diverges]`. (c) leaks two types into the API. Decide before implementation: the type is in every signature. Keep ENC-26 and ENC-57..59 excluded (no `parseUpdateMeta`/`logUpdate`/`obfuscateUpdate` equivalents).

### A2. Root `YXmlText`
- **Question.** `Doc.Get` cannot create a root `YXmlText`; JS has `ydoc.get('', Y.XmlText)`.
- **Bites.** `SharedTypeTests` (line ~77) and `TextTests` (~261) put the XmlText inside a fragment instead. Spec 8 hit the same gap.
- **Options.** Add `Doc.GetXmlText(name)`; or leave it out as rare.
- **Recommendation.** Add it (cheap, removes the workaround).

### A3. `YArray.Slice`, `ForEach`, `Map`
- **Question.** JS `slice(start, end)`, `forEach` and `map` have no counterpart; the sketch has the enumerator, `ToArray` and `CopyTo(start, destination)`.
- **Bites.** `ArrayTests` lines ~114 and ~167 port them with `ToArray` and enumeration.
- **Options.** Add `Slice` only; add all three; add none (LINQ over the enumerator).
- **Recommendation.** `Slice` only. `ForEach`/`Map` add nothing over `foreach` and LINQ.

### A4. `Transact` return value
- **Question.** JS `transact` returns the callback's value; C# `Transact` returns nothing.
- **Bites.** `TransactionTests` (~86) reads state from inside the body.
- **Options.** Add `Transact<TResult>(Func<Transaction, TResult>)` (and a state-passing form); or keep void.
- **Recommendation.** Add the generic overload. Related trap (Spec 12): `Transact(body, origin, timeout)` with a `null` origin binds to the generic overload, so tests use named arguments; consider renaming one overload.

### A5. `AfterAllTransactions` has no transaction list
- **Question.** JS `afterAllTransactions` passes the list of transactions of the cleanup round; the stub exposes none.
- **Bites.** Spec 7 event and observer ordering tests.
- **Options.** Expose a pooled read-only list valid during dispatch (ADR 0008); or only a count.
- **Recommendation.** Expose the list, consistent with the "valid only during dispatch" rule.

### A6. Item identity is not public
- **Question.** `ItemId` is public, items and structs are not, so `compareIDs` and `getTypeChildren` are approximated in tests. Spec 8: `ItemView` exposes only the id.
- **Options.** Keep hidden and test through the `structs` dump seam (section C); or expose a read-only `ItemView` (origin, deleted flag, content kind).
- **Recommendation.** Keep hidden. Public struct identity commits the layout (ADR 0018) for little user value.

### A7. `YEvent` lifetime
- **Question.** Events are pooled and valid only during dispatch (ADR 0008); JS shares one event object across observers and it stays usable afterwards.
- **Bites.** Spec 7 observer tests cannot reproduce shared-event semantics. The Q25 fixture assumes `YEvent.GetPath()` is relative to `CurrentTarget`.
- **Options.** Keep pooled and document; or add an explicit owned copy (`YEvent.Snapshot()`).
- **Recommendation.** Keep pooled; add the copy only if users ask. State the `GetPath()` base in the API docs.

### A8. Other stub gaps from Spec 8
`UndoManagerOptions.Doc` is missing, `YXmlFragment` cannot hold a subdoc, there is no `whenLoaded`/`whenSynced` and no `GetSubdocGuids`. `SubdocTests` (~315) and `UndoPortTests` (~73) work around the first two. **Recommendation.** Add `UndoManagerOptions.Doc` and `GetSubdocGuids`; allow subdocs in fragments (Yjs does); expose loaded/synced as events, since ADR 0008 puts async APIs out of scope. RP-05 and PUD-05 follow JS for now.

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
Decided: rename all. Every test method now follows `Subject_Condition_Outcome` (ADR 0015); upstream names live only in `[PortOf]`. The ported oracle tests are named `<Scenario>_OracleScenario_BehavesLikeTheOracle`.

### D3. Open, no decision needed yet
The randomized gating budget (~60 s) and the 2-minute gating budget are unmeasured until an implementation exists; the AFL++ job is untested; Spec 13's live differential properties are unrun and will probably need normalisers; Q25 is active under Spec 10.
