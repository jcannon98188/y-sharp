# API decisions

**Every section is decided: A, B and C (below), D1 is Keep, D2 and D3 are resolved, and section E was signed off by the owner on 2026-10-27.** Each item is a question the test suite hit while compiling against the API stub (ADR 0008, 0019) and could not settle alone. Sources: the TASK-2 implementation notes (`backlog task view 2 --plain`), `// API gap` comments in `tests/`, and the registry (`docs/inventory/`). Decided items record the decision and what changed; D1 and D3 keep their original options.

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

Section B is decided; the decisions are recorded in ADR 0003, 0005, 0006, 0008 and 0014 and applied to the API stub and the tests.

### B1. Limit semantics
**Decided: exceeded when value > limit; checked before allocation; on every decode path; the document is unchanged on failure.**
- **Decision.** A limit equal to the value is accepted, one more is `LimitExceeded`. The check runs on the length claim before anything is allocated. It applies to apply, merge, diff, convert, state vector and validate, for V1 and every V2 column; `YUpdate.TryMerge`, `TryDiff`, `TryConvert` and `TryStateVectorFromUpdate` take an optional `YLimits`. What each limit measures is written in ADR 0005.
- **Tests.** `LimitsTests` states the semantics instead of assuming them, and has one boundary test per limit (limit accepted, limit+1 rejected): per decode path and format for struct count, string bytes and update bytes, plus depth and pending bytes.

### B2. CC-9: exception type for stale views
**Decided: a new `YLifetimeException : YException`; stale-view access throws it and never returns old contents.**
- **Decision.** No existing type fits: `YConcurrencyException` is for lock-protocol misuse, and `InvalidOperationException` and `ObjectDisposedException` sit outside the `YException` family (nothing is disposed; the objects are pooled). `IsValid` stays callable. Recorded in ADR 0005 and 0006.
- **Tests.** `HandleTests` no longer accepts "old contents or an exception": transactions, events, change collections and enumerators taken inside an explicit transaction throw `YLifetimeException`. The `LifecycleTests` note about the unreachable `YConcurrencyException` now says why (the `ref struct` scope) and points at the reachable case.

### B3. `TryTransact` under a held read lock
**Decided: it returns a failed status immediately; the throwing form throws.** The status is a new `TryTransactStatus.UpgradeRefused` (distinct from `Contended`, which means another thread holds the lock); the body does not run and the read lock is untouched. `Transact` and `BeginTransaction` throw `YConcurrencyException` (CC-3). Recorded in ADR 0006 and the stub doc comments; `LockModelTests` has the test.

### B4. `Awareness.GetStates`: snapshot (AW-13)
**Decided: snapshot.** AW-13 is now kind `diverge` with a `[Diverges]` test (a held map never changes, the next call shows the new state); recorded in ADR 0014 and the Spec 9 matrix.

### B5. AU-5: null handler
**Decided: confirmed.** A null permission handler throws `ArgumentNullException` at the call that receives it, before any byte is read; the AU-5 test also proves it for a message that never reaches the handler. Recorded in ADR 0014.

### B6. ERR-20: fault-injection seam
**Decided: an `internal` hook, `Doc.InjectInvariantViolation()`.** It breaks a struct-store invariant; the next operation that touches the store throws `YInternalException` and faults the document, which stays readable, rejects mutation (`YInternalException`) and apply (`ApplyUpdate` throws, `TryApplyUpdate` returns `Rejected`). ERR-20 is `active` with its test and the pending high-water mark fell. ERR-19 is confirmed: an `OutOfMemoryException` from an observer propagates and is not an observer fault. Recorded in ADR 0005 and 0015.

### B7. Q22: clocks and lengths (ADR 0003)
**Decided: negative, non-integer or above-2^53-1 clocks and lengths on the wire are Malformed.** This includes a start clock plus a length above 2^53-1. Recorded in ADR 0003; the `Q22_*` tests are in `MalformedInputTests` and Q22 is `active` (the hostile clock fixture now covers it). The negative-clock V2 case is hand-built and has not run against an implementation.

## C. Internal seams the tests require

**Decided: accept all seams.** They are declared `internal` in the stub, throwing `NotImplementedException`, and documented in ADR 0015.

| Seam | Needed by | Contract |
|---|---|---|
| `internal string Doc.DumpStructs()` | Spec 6 (`StructsSupport.cs`) | ADR 0016 `structs` dump as JSON |
| `internal bool Doc.HasPending` | Spec 6 | pending-struct presence |
| `internal int YText.CleanupFormatting()` | Spec 10 | counterpart of `cleanupYTextFormatting` |
| `internal static class YSharp.ScheduleSeam` (`Action<string>? Point`, `Action<string, Func<bool>>? Block`) | Spec 12 (CC-5, scenarios) | the lock reports its transitions; a static with a null check on the hot path. The CC-18 lock-order checker is Debug-only |
| `Doc.CollectGarbage/ShouldLoad/AutoLoad/CollectionId/Meta/SignalLoaded/SignalSynced`, `UndoManager.Undoing/Redoing/CurrStackItem` | Spec 8 | members JS exposes that the sketch hides |
| `internal void Doc.InjectInvariantViolation()` | ERR-20 | see B6 |

`YSharp.Tests`, `YSharp.Fuzz.Tests` and `YSharp.Benchmarks` call them directly; the reflection lookups in `StructsProbe`, `TextProbe` and the test-side `DocSeams`/`UndoSeams` callers are gone. The TestKit and the interop project cannot see internals, so the TestKit keeps reflection for the ops interpreter and the schedule scenarios (`DocSeams`, `UndoSeams`, the `ScheduleSeam` adapter).

## D. Repository and process

### D1. 18 MB of simulation fixtures in git
**Decided: Keep (a).** Revisit if the pack passes about 50 MiB, and regenerate rarely.

`fixtures/sim` is 18 MB of the 20 MB `fixtures/` tree. The repository pack is 2.4 MiB compressed today, so the cost is modest, but it grows with every regeneration.
- **Options.** (a) Keep: no generation step for gating. (b) Git LFS: smaller clones, needs LFS in CI and for contributors. (c) Generate in CI and do not commit: no history growth, but the Windows and macOS gating jobs have no Node (ADR 0015), so they would need the files from an artifact.
- **Recommendation.** Keep (a); revisit if the pack passes about 50 MiB, and regenerate rarely.

### D2. Test method naming rule (resolved)
Decided: rename all. Every test method now follows `Subject_Condition_Outcome` (ADR 0015); upstream names live only in `[PortOf]`. The ported oracle tests are named for the behaviour they check, for example `Undo_Map_UndoesAndRedoesKeyChanges`.

### D3. Resolved by measurement
At the time this was open, the randomized gating budget (~60 s) and the 2-minute gating budget were unmeasured, the AFL++ job was untested, Spec 13's live differential properties were unrun, and Q25 was active under Spec 10.

**Resolved.** TASK-4.38 calibrated the randomized gating tier to about 58 s. TASK-4.43 ran coverage, SharpFuzz/AFL++ and the nightly benchmark green on a real runner. Spec 13's live differential runs on every PR (`smoke-differential`), with its normalisers in `tests/YSharp.Interop.Tests/Harness/Normalisers.cs`.

## E. Surface added since the TASK-4 baseline (TASK-10)

`tools/api-surface/check.sh` now checks two baselines: `baseline.txt` for `YSharp`, and `baseline.server.txt` for `YSharp.Server` and `YSharp.Server.Testing`. TASK-10 brought both up to date. Each change below was checked against the conventions sections A and B set: `Try` methods return a status and never throw on bad input, and throwing counterparts throw `YDecodeException`. Outputs go to an `IBufferWriter<byte>`, with a `byte[]` convenience overload. Classes are sealed, and records are used only for plain data. E1 to E3 and E6 were decided by the owner on 2026-10-27; E4 and E5 are for the record.

### E1. `YRelay.TryPrepareUpdate` parameter order
`TryPrepareUpdate(update, format, out prepared)` takes `format` before the `out` and without a default. `YUpdate.TryDecode(update, out decoded, format = V1, limits = null)` and the rest of the core put the `out` right after the input and default the format to V1.
- **Options.** (a) Change it now to `TryPrepareUpdate(update, out prepared, format = V1)`, while nothing is published. (b) Keep it.
- **Recommendation.** (a). The only caller is `DocumentMailbox`.
- **Decision (owner sign-off, 2026-10-27).** (a). The signature is now `TryPrepareUpdate(update, out prepared, format = V1)`.

### E2. `YRelay` has no throwing `ApplyUpdate`
`Doc` pairs `ApplyUpdate` with `TryApplyUpdate`, and `YRelay` pairs `EncodeStateAsUpdate` with `TryEncodeStateAsUpdate`. But `YRelay` has only `TryApplyUpdate`, and preparing has only `TryPrepareUpdate`.
- **Options.** (a) Add `ApplyUpdate`, which throws `YDecodeException`, or `YLimitException` for the pending bound. (b) Leave the relay `Try`-only, as server code should not throw on a peer's bytes.
- **Recommendation.** (b), with a sentence in the class documentation saying why.
- **Decision (owner sign-off, 2026-10-27).** (b). The `YRelay` class documentation says why.

### E3. Server convenience for compaction and deletion
A host compacts with `new UpdateLog(store).CompactAsync(docId)` and deletes through `IUpdateStore.DeleteAsync`. `YServer` exposes neither. Deleting a document that is open leaves its mailbox serving the old relay until it is evicted.
- **Options.** (a) Add `YServer.CompactAsync(docId)` and `YServer.DeleteAsync(docId)`, which close the document's connections first. (b) Keep it as is and document the open-document caveat in `docs/server.md`.
- **Recommendation.** (a) for deletion, which has the caveat. Compaction is already safe while a document is open.
- **Decision (owner sign-off, 2026-10-27).** (a) for deletion only. `YServer.DeleteAsync(docId)` lets the mailbox finish what it accepted, closes its connections with the new `ConnectionCloseReason.DocumentDeleted`, then deletes from the store; opens of that document wait meanwhile. `docs/server.md` documents it and the direct-store caveat. No `YServer.CompactAsync`.

### E4. Added in TASK-5 (no decision needed)
- **Core.** `YRelay` and `YRelay.PreparedUpdate` (TASK-5.2, 5.3). `YUpdate.TryDecode` with `DecodedUpdate`, `UpdateStruct`, `UpdateStructKind`, `UpdateContentKind` and `UpdateDeleteRange`: a read-only view of an update for validation hooks. `UpdateContentKind` is an API enum, not the wire's content reference numbers. Also `YSync.TryReadFrame`, and span overloads of `YSync.WriteStep1` and `WriteStep2`.
- **Server.** `YServer`, `YServerOptions`, `YConnection`, `ConnectionContext`, `ConnectionCloseReason`; storage (`IUpdateStore`, `InMemoryUpdateStore`, `UpdateRecord`, `UpdateLog`, `AppendResult`, `LoadedDocument`); validation (`ValidationHook`, `ValidationRequest`, `ValidationResult`); and `YSharp.Server.Testing` (`UpdateStoreConformance`, `InMemoryUpdateStoreConformance`, `ConformanceResult`, `ConformanceOutcome`, `ConformanceException`).
- **Type choices.** `YConnection.Outgoing` is a `ChannelReader<ReadOnlyMemory<byte>>`, so hosts depend on `System.Threading.Channels`, which ships with the framework. `YServer.EditAsync` returns false when the edit changed nothing.

### E5. Baseline drift from TASK-4 (no decision needed)
The TASK-4 baseline still had two lines the implementation had already changed:
- `StackItem` and `YSnapshot` lost their public parameterless constructors (TASK-4.32, TASK-4.30). Only the undo manager and `YSnapshot.Create` or `Decode` make them.
- `YSnapshot` gained `Equals` and `GetHashCode`, matching Yjs `equalSnapshots` (TASK-4.30).

### E6. Decoded-update overloads (TASK-14)
So that `DocumentMailbox` decodes a peer's update once, three members take a `DecodedUpdate` from `YUpdate.TryDecode`: `YRelay.TryPrepareUpdate(DecodedUpdate, out prepared)`, `Doc.TryApplyUpdate(DecodedUpdate, origin)`, and `DecodedUpdate.Encode(output, format = V1)`, which writes the bytes `YUpdate.TryConvert` would. Each gives what its bytes overload gives. A decoded update carries the limits it was decoded under, and a relay or document refuses it with `LimitExceeded` when they were looser than its own string or nesting bound, since those are checked only while decoding.
- **Options.** (a) Keep the three overloads. (b) Remove them and let the server decode three times; ADR 0024 keeps the server on the public API, so there is no internal route.
- **Recommendation.** (a). There is no throwing `Doc.ApplyUpdate(DecodedUpdate)`; add one only if E2 goes the other way.
- **Decision (owner sign-off, 2026-10-27).** (a). E2 stayed `Try`-only, so no throwing `Doc.ApplyUpdate(DecodedUpdate)` is added.
