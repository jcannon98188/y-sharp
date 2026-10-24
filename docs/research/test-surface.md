# Research: test surface by entry point (TASK-3.2)

Status: research findings for the implementation plan. These are findings, not decisions. Nothing here changes the ADRs.

Scope: every test file with `[Fact]`/`[Theory]` in `tests/YSharp.Tests`, `tests/YSharp.Fuzz.Tests`, `tests/YSharp.Interop.Tests`, plus the allocation/memory tier (`tests/YSharp.Tests/Allocation`) and the benchmarks in `bench/YSharp.Benchmarks`. Question: which public entry points and internal seams must work before each file stops skipping, and at which ADR 0018 milestone that could first happen.

Milestone codes (ADR 0018 order, `docs/adr/0018-memory-layout-requirements.md`):

| Code | Milestone | Code | Milestone |
|---|---|---|---|
| P | No store needed: YValue, JSON, StateVector/ItemId, YLimits, errors, pure helpers | M4 | Shared types |
| M0 | Spike gate (not a public-API milestone) | M5 | Transactions and events |
| M1 | Store and integrate | M6 | Awareness and sync |
| M2 | V1 codec | U | Needed by tests but in **no** ADR 0018 milestone |
| M3 | V2 codec | | |

Counts are `[Fact]`/`[Theory]` attributes per file. A `[Theory]` counts once regardless of data rows. Totals: YSharp.Tests 1025 attributes in 83 files, Fuzz.Tests 117 in 12, Interop.Tests 78 in 7. That is 1220 in all, close to the handoff's "about 1,200".

---

## 1. Method

- **Read in full or in the relevant ranges:** `docs/handoff-implementation-planning.md`, `docs/adr/0018-memory-layout-requirements.md`, `docs/adr/0009-allocation-and-memory-budgets.md`, `GLOSSARY.md`, the stub `src/YSharp/2-Doc.cs` (constructors, Doc members, transaction and update surface, YUpdate), grep and range reads of `1-Core.cs`, `3-Types.cs`, `4-DocFeatures.cs` and `5-Protocols.cs`, `tests/YSharp.TestKit/Ops/OpsInterpreter.cs`, `Ops/UndoSeams.cs`, `Stubs/ApiStub.cs`, `Fixtures/FixtureLoader.cs` (loader), `Sim/SimHarness.cs` (ISimInternals), `tests/YSharp.Tests/DocFeatures/DocSupport.cs` (`Net.Sync`), `tests/YSharp.Tests/Protocols/SyncTests.cs` (`SyncSupport`), `docs/inventory/budgets.yaml` (BUD-01..14), `tests/YSharp.Tests/Allocation/MemoryTierTests.cs`, and the heads of `WireFixtureTests.cs`, `TypeFixtureTests.cs` and `DocFeatureFixtureTests.cs`.
- **Scanned mechanically:** every test file in the three projects and `bench/`. A script (`scratchpad/scan.py`, output `scratchpad/scan.json`) counted, per file, `[Fact]`/`[Theory]`, the `Covers`/`PortOf`/`Diverges` IDs, and which stub member names occur. The "entry points" column comes from this scan, so it is an identifier match, not a reading of each test body.
- **Traced through helpers:** the test-side step registries (`OpsInterpreter.cs` StandardSteps, 17 core steps; `Ops/DocFeatureSteps.cs`, 17 doc-feature kinds; `Types/TypeSteps.cs`, `Protocols/ProtocolSteps.cs`, `Simulation/SimSteps.cs`, `Codecs/FixtureReplay.cs`), the per-area support files (`*Support.cs`), and the gating helpers (`DocSupport.Run`, `StructsSupport.Run`, `ConcSupport.Run`, `Stub.Run`).
- **Fixtures:** the fixture-to-test mapping comes from the loader calls (`DocFeatureFixtureTests` areas `doc, subdocs, snapshot, relpos, undo, pud`; `TypeFixtureTests` areas `text, array, map, xml, events`; `ProtocolFixtureTests` areas `awareness, sync, auth`; `WireFixtureTests` area `encoding` plus `hostile/malformed-*`; `StructsFixtureTests` area `structs`; `SimFixtureTests` sim goldens; `HostileAllocationTests` and the Fuzz corpus area `hostile`). About 60 distinct step kinds occur in `fixtures/**/*.json` (counted by a JSON walk). Fixture step kinds that the core interpreter does not register are registered in the test-side files listed above.
- **Not done:** I did not read every test body (about 1,220 attributes). Per-file "earliest milestone" rows for files I did not open are inferred from the entry-point scan plus the helpers, and are marked `(inferred)` where that matters. Uncertain cells are flagged in section 6.

---

## 2. Per-project tables

Columns: test file | tests | registry prefixes (from `Covers`/`PortOf`/`Diverges`) | required public entry points (scan) | required seams | earliest milestone.

### 2a. tests/YSharp.Tests (83 files)

#### Codecs (`tests/YSharp.Tests/Codecs`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Codecs/JsonTests.cs | 15 | ENC, Q08, Q11, Q28, Q30 | YValue factories, TryParseJson, TryWriteJson, AsNumber, AsString | - | P (one case uses Doc: M2) |
| Codecs/MalformedInputTests.cs | 40 | ENC, Q12, Q22 | Doc(DocOptions), ApplyUpdate, TryApplyUpdate, TryConvert, StateVector.Decode, GetStateVector, EncodeStateAsUpdate, Insert | DumpStructs (inferred) | M2 (V1 cases); M3 (V2); M5 (cases that insert) |
| Codecs/StringQuirkTests.cs | 6 | Q16-Q19 | Doc, Insert, Delete, Encode, ApplyUpdate | - | M5 (Insert) |
| Codecs/UpdateFunctionTests.cs | 7 | ENC | YUpdate.TryMerge, TryDiff, TryConvert, Doc, ApplyUpdate, EncodeStateAsUpdate, Insert | - | M3 (TryConvert); M5 (insert cases) |
| Codecs/UpdateLayoutTests.cs | 27 | ENC, Q12-Q19 | Decode, Encode, ApplyUpdate, Insert, Set, GetMap, GetArray, YDecodeException | - | M5 (inserts); M2 (layout-only) |
| Codecs/UpstreamPortTests.cs | 12 | ENC | Doc, ApplyUpdate, Insert, EncodeStateAsUpdate, Gc, PermanentUserData.SetUserMapping | - | U (PUD cases); M5 otherwise |
| Codecs/ValueQuirkTests.cs | 16 | ENC, Q01-Q09, Q31 | YValue (Bytes, Array, BigInt, Object, TryGetObject), Doc.GetText, Encode | - | P (one case uses Doc: M5) |
| Codecs/WireFixtureTests.cs | 13 | ENC, Q01-Q19, Q28, Q30 | FixtureReplay: ApplyUpdate, EncodeStateAsUpdate, plus step kinds of the Codecs replay (mergeUpdates, diffUpdate, convertUpdate, text/map/array steps) | - | M5 (full fixture replay; text/map/array steps); M2 for encoding-only checks |

#### Concurrency (`tests/YSharp.Tests/Concurrency`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Concurrency/AcquireTests.cs | 10 | CC | TryTransact, Transact, ReadTransact, TransactWithTimeout, TryTransactStatus | ScheduleSeam (inferred) | M5 |
| Concurrency/ComponentTests.cs | 12 | CC | Transact, Push, Awareness, UndoManager, StackItem, PermanentUserData, GetStates, SetLocalState | - | M5 for core; U (undo, PUD); M6 (awareness) |
| Concurrency/HandleTests.cs | 12 | CC | Transact, Push, Insert, IsValid, Destroy, EncodeStateAsUpdate, ApplyUpdate, TryGet | - | M5 (lifecycle); M2 (encode) |
| Concurrency/LifecycleTests.cs | 13 | CC | Transact, Destroy, TryTransact, BeginTransaction, BeginReadTransaction, TransactionScope, IsDestroyed | - | M5 |
| Concurrency/LockModelTests.cs | 18 | CC | Transact, ReadTransact, TryTransact, BeginReadTransaction, Insert, Push, EncodeStateAsUpdate | StressHarness (TestKit) | M5 |
| Concurrency/ObserverTests.cs | 11 | CC | Observe, ObserveDeep, Unobserve, AfterAllTransactions, ObserverFaulted, Transact, Push | - | M5 |
| Concurrency/ScheduleScenarioTests.cs | 11 | CC | ScheduleSeam, CooperativeScheduler (TestKit) | ScheduleSeam.Point, ScheduleSeam.Block | M5 (the lock is the only producer of seam calls) |
| Concurrency/SchedulerTests.cs | 8 | CC | ScheduleSeam, CooperativeScheduler | ScheduleSeam.Point, ScheduleSeam.Block | M5 (inferred; may run earlier if the scheduler is tested without a lock) |
| Concurrency/StressTests.cs | 7 | CC | Transact, Observe, Insert | - | M5 |

#### DocFeatures (`tests/YSharp.Tests/DocFeatures`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| DocFeatures/DocFeatureFixtureTests.cs | 3 | DOC, SUB, UM, SNAP, RP, PUD, GC | FixtureLoader over `fixtures/{doc,subdocs,snapshot,relpos,undo,pud}`; V1 and V2 replay; DocFeatureSteps (gc.run, subdoc.*, snapshot.*, relpos.*, undo.*, pud.*) | DocSeams, UndoSeams | U (replays undo, subdocs, snapshot, relpos, pud); M5 for `doc` only |
| DocFeatures/DocTests.cs | 30 | DOC | Doc options (Guid, ClientId, Gc, CollectionId, Meta, AutoLoad, ShouldLoad), Transact, GetText/Array/Map, Share, IsLoaded, IsSynced, Load, Destroy, ApplyUpdate, EncodeStateAsUpdate, Observe, observeDeep | DocSeams (ShouldLoad, AutoLoad, CollectionId, Meta), SignalLoaded, SignalSynced | M5 for the plain doc cases; U for load/sync and subdoc cases |
| DocFeatures/PermanentUserDataTests.cs | 10 | PUD | PermanentUserData, PermanentUserDataOptions, SetUserMapping, GetUserByClientId, GetUserByDeletedId, Defer | - | U (no milestone; needs M4, M5, M2) |
| DocFeatures/RelativePositionTests.cs | 27 | RP | CreateRelativePosition, RelativePosition.Encode, TryDecode, TryFromJson, ToJson, Resolve, AbsolutePosition | - | U (needs M4, M5, M2) |
| DocFeatures/SnapshotTests.cs | 26 | SNAP | YSnapshot.Create, Encode, TryDecode, CreateDoc, ContainsUpdate, Equals, EncodeStateAsUpdate, Gc | DocSeams (CollectGarbage, via DocFeatureSteps) | U (needs M1, M2, M5 plus snapshot code) |
| DocFeatures/SubdocTests.cs | 22 | DOC, SUB, UM | Subdocs, GetSubdocGuids, Doc(options) for subdocs, Destroy, Load, ShouldLoad, AutoLoad, SubdocsChanged, Added/Removed/Loaded | DocSeams (ShouldLoad, AutoLoad, CollectionId, Meta), SignalLoaded | U |
| DocFeatures/UndoManagerTests.cs | 28 | UM | UndoManager (ctor, Undo, Redo, StopCapturing, Clear, AddToScope, UndoStack, RedoStack, StackItemAdded/Popped/Updated), Transact, Origin | UndoSeams (Undoing, Redoing, CurrStackItem) | U (needs M4, M5) |
| DocFeatures/UndoPortTests.cs | 26 | UM | UndoManager, Undo, Redo, SetAttribute, Get, Insert, Delete, Push, Transact | UndoSeams | U |

#### Protocols (`tests/YSharp.Tests/Protocols`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Protocols/AuthAndPeerTests.cs | 7 | AU, SY | YAuth.TryReadMessage, WritePermissionDenied, Doc, EncodeStateAsUpdate, StateVector | - | M6 |
| Protocols/AwarenessApplyTests.cs | 11 | AW | Awareness(Doc, TimeProvider), SetLocalState, TryModifyUpdate, Apply, GetStates, Meta | - | M6 (state-only cases may run earlier with a Doc shell; uncertain) |
| Protocols/AwarenessErrorTests.cs | 5 | AW | Awareness, Apply, TryApplyUpdate, Encode, FixtureReplay on `awareness/apply-trailing-and-truncated.json` | - | M6 |
| Protocols/AwarenessStateTests.cs | 21 | AW | Awareness, SetLocalState, SetLocalStateField, RemoveStates, GetStates, LocalState, Meta, Doc.Destroy (destroy case) | - | M6 (destroy case: M5) |
| Protocols/AwarenessTimeTests.cs | 9 | AW | Awareness, TimeProvider, OutdatedTimeout, RemoveStates, Meta | - | M6 (time logic may run earlier; uncertain) |
| Protocols/AwarenessWireTests.cs | 11 | AW | Awareness, TryEncodeUpdate, TryModifyUpdate, Encode, Apply, GetStates | - | M6 |
| Protocols/ProtocolDocsTests.cs | 7 | PR, Q32 | Awareness, YSync, YAuth, TryApplyUpdate, IsSynced, OutdatedTimeout | - | M6 |
| Protocols/ProtocolFixtureTests.cs | 1 | (none) | FixtureReplay over `fixtures/{awareness,sync,auth}`; ProtocolSteps (sync.*, awareness.*, auth.*) plus doc steps | - | M6 (doc steps need M5) |
| Protocols/ProtocolStepKindTests.cs | 8 | AU, AW, SY | ProtocolSteps registry (sync.read, sync.step1/2, awareness.new, awareness.encode, auth.permissionDenied) | - | M6 |
| Protocols/SyncTests.cs | 17 | SY | YSync.WriteStep1, WriteStep2, WriteUpdate, TryReadMessage, EncodeStateAsUpdate (V1), ApplyUpdate, Doc.Insert (via SyncSupport) | - | M6 (needs M2; Insert needs M5) |

#### Simulation (`tests/YSharp.Tests/Simulation`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Simulation/NightlyRandomizedTests.cs | 9 | SIM | Doc, Array, Text, Count; Nightly tier only | ISimInternals | M6 (Nightly tier) |
| Simulation/RepeatedRandomTests.cs | 44 | SIM | SimHarness, Text, Array, FixtureLoader for `fixtures/random/`, Load | ISimInternals | M6 |
| Simulation/SimDivergenceTests.cs | 7 | SIM | Doc, Insert, StateVector, ApplyUpdate, FixtureLoader | ISimInternals (RecordingInternals) | M6 |
| Simulation/SimFixtureTests.cs | 11 | SIM, TX, Q25 | FixtureLoader and FixtureReplay over `fixtures/sim/*.json`; deliver, observeDeep, text/array/map steps | ISimInternals | M6 |
| Simulation/SimLogTests.cs | 2 | SIM | StateVector, ApplyUpdate, deliver (SimLog) | - | M6 |
| Simulation/SimPlanTests.cs | 1 | SIM | plan goldens (`plan-goldens.json`), Count, Length, ToArray | - | P (inferred: plan generation without a Doc; uncertain) |

#### Structs (`tests/YSharp.Tests/Structs`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Structs/ClientIdTests.cs | 2 | STR, Q20, Q21 | DocOptions.ClientId, IClientIdGenerator, GetStateVector, Insert | - | M1 (GetStateVector); M5 (Insert case) |
| Structs/ContentTests.cs | 8 | GC, STR | Insert, Delete, Push, Set, EncodeStateAsUpdate, Observe (Added/Removed) | - | M5 |
| Structs/ErrorPathTests.cs | 6 | STR | ApplyUpdate, TryApplyUpdate, IsFaulted, Observe, ObserverFaulted, Insert, InsertEmbed | - | M2 (decode errors); M5 (observer/insert cases) |
| Structs/GarbageCollectionTests.cs | 11 | GC, STR | Gc, Insert, Delete, Set, Push, EncodeStateAsUpdate, YSnapshot.Create, CreateDoc | DocSeams.CollectGarbage (inferred) | M5 (GC core); U (snapshot cases) |
| Structs/IntegrationTests.cs | 12 | STR | ApplyUpdate, GetStateVector, EncodeStateAsUpdate, Insert, Unshift, Set, Push, Delete | - | M5 (inserts); M2 (apply/encode) |
| Structs/ItemHelperTests.cs | 7 | GC, STR | UndoManager, StopCapturing, Undo, Redo, RelativePosition.Resolve, Insert, Delete | UndoSeams (inferred) | U |
| Structs/StructModelTests.cs | 9 | STR | YXmlElement.NodeName, ApplyUpdate, EncodeStateAsUpdate, Insert, Set, Push, GetStateVector | - | M4 + M5 (XML and inserts); M2 |
| Structs/StructStoreTests.cs | 5 | GC, STR | ApplyUpdate, TryApplyUpdate, EncodeStateAsUpdate, GetStateVector, Insert, Set, Delete | DumpStructs (inferred) | M2 (apply); M5 (insert cases) |
| Structs/StructsFixtureTests.cs | 2 | GC, STR, Q20, Q21 | FixtureLoader over `fixtures/structs/*.json` (29 files); mergeUpdates, map.insertType, array.insertType, Doc.DumpStructs | DumpStructs, HasPending | M5 (inserts and nested types); U (snapshot and subdoc fixtures) |
| Structs/TransactionTests.cs | 8 | STR | Transact, AfterAllTransactions, AfterTransactionCleanup, Observe, BeforeState, Insert, Set, Clear, Origin, IsLocal | - | M5 |
| Structs/UpdateApplicationTests.cs | 16 | GC, STR | ApplyUpdate, TryApplyUpdate, HasPending, EncodeStateAsUpdate, GetStateVector, Set, Insert, Delete | DumpStructs, HasPending | M2 (apply and pending); M5 (insert cases) |

#### TestKit self-tests (`tests/YSharp.Tests/TestKit`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| TestKit/AllocationAssertTests.cs | 4 | (none) | AllocationAssert (TestKit) | - | P |
| TestKit/ApiStubTests.cs | 8 | (none) | ApiStub.Detect, YValue.Null (probe) | - | P (see finding 7) |
| TestKit/DiffAssertionTests.cs | 11 | (none) | Diffs (TestKit) | - | P |
| TestKit/FixtureLoaderTests.cs | 7 | (none) | FixtureLoader, PinnedVersions (temp fixtures) | - | P |
| TestKit/GeneratedFixtureTests.cs | 7 | (none) | FixtureLoader.Discover over `fixtures/`; drift checks | - | P (inferred) |
| TestKit/NodeClientTests.cs | 7 | (none) | NodeClient, echo-server.mjs (needs Node) | - | P (needs Node) |
| TestKit/OpsScriptTests.cs | 15 | (none) | OpsScript parser; OpsInterpreter replay (Transact, GetText) | - | P (parser); M5 (replay cases) |
| TestKit/PrngGoldenTests.cs | 4 | (none) | Prng goldens | - | P |
| TestKit/SimHarnessTests.cs | 8 | SIM | SimHarness, SyncMessageType, Doc, ApplyUpdate, Insert, Delete, Update event | ISimInternals | M6 |
| TestKit/TaggedValueTests.cs | 9 | (none) | TaggedValue, YValue (Number, BigInt, Array) | - | P |
| TestKit/TierEnforcementTests.cs | 5 | (none) | Tiers | - | P |
| TestKit/TierTests.cs | 5 | (none) | Tiers, YValue | - | P |
| TestKit/VirtualTimeProviderTests.cs | 7 | (none) | VirtualTimeProvider | - | P |

#### Allocation and memory tier (`tests/YSharp.Tests/Allocation`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Allocation/BenchmarkComparisonTests.cs | 10 | (none) | Reads `bench/YSharp.Benchmarks/Benchmarks.cs` as text (source scan) | - | P (source scan) |
| Allocation/BudgetTableTests.cs | 11 | BUD | AllocationBudgets table consistency | - | P |
| Allocation/HostileAllocationTests.cs | 2 | BUD (BUD-12) | Doc, TryApplyUpdate (V1, V2) over `fixtures/hostile` | - | M2 (V1); M3 (V2) |
| Allocation/HotPathBudgetTests.cs | 15 | BUD-01..11 | Transact, ReadTransact, TryTransact, YValue scalar reads, AttributeEquals, event dispatch, Update subscriber, EncodeStateAsUpdate, YText.Insert, TryApplyUpdate, Push/Set, YSync Step1/2, Awareness encode/apply | - | per path, see section 5 |
| Allocation/MemoryTierTests.cs | 2 | BUD-13, BUD-14 | Doc, YText.Insert, YArray.Push, YMap.Set, Transact; Nightly tier, Release only | - | M5 |

#### Fuzz-adjacent helpers and traceability (in YSharp.Tests)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| ScaffoldSmokeTests.cs | 3 | (none) | Doc, YArray, YText, YMap, YLimits, YValue (constructed) | - | M1 (Doc object exists) |
| Traceability/CheckerRuleTests.cs | 26 | (none) | TraceabilityChecker (TestKit) | - | P |
| Traceability/ScannerAndAttributeTests.cs | 9 | ZZ (test fixtures) | TestScanner, attributes | - | P |
| Traceability/TraceabilityGateTests.cs | 2 | (none) | Registry and ID-lock gate; goes green as IDs get tests | - | P (not an API dependency) |

#### Types (`tests/YSharp.Tests/Types`)

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Types/ArrayTests.cs | 32 | A | YArray: Insert, Push, Delete, Slice, ToArray, GetEnumerator, Transact, From, Set | - | M4 + M5 (see finding 2) |
| Types/EventTests.cs | 34 | E, M, Q23, Q24, TX, V, X, Y | Observe, ObserveDeep, YMapEvent, YArrayEvent, YTextEvent, KeysChanged, Target, Delta, Transact | - | M5 (events); M4; M2 (multi-replica sync) |
| Types/MapTests.cs | 18 | M, Q26, TX | YMap: Set, Get, TryGet, Delete, Clear, Has, Keys, Count | - | M4 + M5 |
| Types/SharedTypeTests.cs | 17 | M, Q10, Q29, X | YType: Parent, Clone, Kind, SetAttribute; Doc.Get; YXmlElement; Set, Insert, Push | - | M4 + M5 |
| Types/StepKindTests.cs | 9 | A, E, F, M, T, X, Y | Type step registry (TypeSteps: text.applyDelta, text.insertEmbed(Type), array.insertType, map.insertType, xml.insert, xml.setAttribute, observeDeep); GetXmlFragment, GetText | - | M4 + M5 |
| Types/TextTests.cs | 41 | A, D, E, F, Q27, T, TX, X, Y | YText: Insert, Delete, Format, ApplyDelta, InsertEmbed, ToDelta, Observe, AttributeEquals, Embed | - | M5 (Insert and Observe need transactions); M4 for pure text reads |
| Types/TransactionTests.cs | 15 | Q23, Q25, TX | Transact, Observe, ObserveDeep, IsValid, ApplyUpdate, EncodeStateAsUpdate, Insert, Push | - | M5 |
| Types/TypeFixtureTests.cs | 1 | (none) | FixtureLoader over `fixtures/{text,array,map,xml,events}`; V1 and V2 replays; events | - | M5 + M3 (replays both formats) |
| Types/XmlTests.cs | 14 | X, Y | YXmlElement: SetAttribute, RemoveAttribute, HasAttribute, TryGetAttribute, Attributes, NodeName, InsertAfter, NextSibling, FirstChild, QuerySelector; YXmlText; GetXmlFragment, GetXmlElement | - | M4 + M5 |

### 2b. tests/YSharp.Fuzz.Tests (12 files)

Gating for these runs through `Stub.Run` (`tests/YSharp.Fuzz.Tests/Support/Stub.cs`) or a nightly gate (`NightlyGate`).

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Concurrency/SoakTests.cs | 6 | CC | Transact, Insert | - | M5 |
| Tests/AmplificationTests.cs | 8 | ERR, HOS | TryApplyUpdate, TryMerge, TryDiff, TryConvert, StateVector, Gc, GetText | allocation counters | M2 (V1); M3 (V2) |
| Tests/BuilderSelfTests.cs | 8 | (none) | UpdateBuilder (fuzz-side builder, no library use in scan) | - | P (inferred) |
| Tests/CanaryTests.cs | 8 | ERR, HOS | Canary host (`Apply` into a child process; `tests/YSharp.Fuzz.Canary`) | - | M2 (decode path under test) |
| Tests/CorpusReplayTests.cs | 13 | ERR, HOS | Hostile corpus replay (`fixtures/hostile`, `Corpus`); TryApplyUpdate, post-condition checks | - | M2 (V1 corpus); M3 (V2); M6 (sync and awareness corpus) |
| Tests/ErrorModelTests.cs | 17 | ERR | Doc, TryApplyUpdate, ApplyUpdate, IsFaulted, Insert, GetText, Observe, GetStateVector, EncodeStateAsUpdate, YDecodeException, YInternalException, YTypeConflictException | Doc.InjectInvariantViolation (fault hook) | M5 (faulted doc needs writes; fault seam needs M1 store) |
| Tests/GuardAndGeneratorSelfTests.cs | 20 | (none) | Guards, generators; Doc.Apply, Insert, Delete in some cases | - | P for guards; M5 for generator cases that use Doc (inferred) |
| Tests/HostileProperties.cs | 12 | HOS | TryApplyUpdate (V1, V2), TryReadMessage, Awareness, YSync, DeleteSet, YLimits, GetText, GetMap, GetArray | - | M6 (sync and awareness); M2 and M3 (decode) |
| Tests/LenientDecodingDivergenceTests.cs | 3 | Q15 | Doc, Load, TryApplyUpdate, Text | - | M5 (text checks) |
| Tests/LimitsTests.cs | 16 | ERR, HOS | YLimits (Default, MaxUpdateBytes, MaxStringBytes, MaxAnyDepth, MaxStructsPerUpdate, MaxPendingBytes), TryApplyUpdate, Encode, Gc, GetText | - | M3 (both V1 and V2 limits) |
| Tests/TierEnforcementTests.cs | 1 | (none) | Tiers | - | P |
| Tests/TruncationTests.cs | 5 | HOS | TryApplyUpdate (V1, V2), TryDiff, TryReadMessage, Awareness, YSync, StateVector | - | M6 (sync and awareness truncation); M2, M3 |

Note: `tests/YSharp.Fuzz.Canary/Program.cs` is a separate process. It catches `NotImplementedException` only while `IsStub()` is true (line 56 and line 69). It is not a test file but its behaviour depends on the same gate (finding 7).

### 2c. tests/YSharp.Interop.Tests (7 files)

All live-differential tests need the pinned Node oracle (`OracleHost`, `NodeClient`). They are never Gating (`Needs=Node`). Files marked "Needs Node" are listed in the `Needs=Node` set by the grep of `OracleHost|NodeClient`.

| Test file | Tests | Prefixes | Entry points | Seams | Earliest |
|---|---|---|---|---|---|
| Tests/ComparatorTests.cs | 24 | DIFF | Doc, Insert, Set, Delete, Text, Load, TryGetValue (through DiffRun); comparator logic on JSON | - | P for comparator-only cases (inferred; uncertain); M5 for cases that replay ops |
| Tests/DifferentialTests.cs | 7 | A, DIFF, ENC, M, Q16, SY | UpdateFormat, Apply, Insert, Kind; live replay (Needs Node) | - | M6 (covers sync; needs whole stack) |
| Tests/GenerationTests.cs | 9 | DIFF | ScriptPlan generation (`deliver`), Insert, Apply, TryGetValue | - | P or M5 (generation runs without the library in part; inferred) |
| Tests/HarnessSelfTests.cs | 12 | DIFF | Harness self-checks; Doc, Insert, Delete, ApplyUpdate (Needs Node) | - | M5 |
| Tests/OracleProtocolTests.cs | 19 | DIFF | Oracle protocol; awareness.new, doc steps (Needs Node) | - | M6 |
| Tests/ReplayAndForeignTests.cs | 6 | DIFF | Replay with applyForeign (V1 and V2 outcomes), Load, TryGet, Set, Text (Needs Node) | - | M3 (foreign V2); M5 (replay) |
| Tests/TierEnforcementTests.cs | 1 | (none) | Tiers | - | P |

The interop project links helper sources from `YSharp.Tests` (`YSharp.Interop.Tests.csproj` lines 23-27: `CodecSupport.cs`, `FixtureReplay.cs`, `TypeDump.cs`, `TypeSteps.cs`, `SimSteps.cs`). Those helpers call the stub, so the interop project inherits the same milestone gates even where its own test body is simple.

### 2d. bench/YSharp.Benchmarks (not gating)

| File | Methods | Entry points | Earliest |
|---|---|---|---|
| bench/YSharp.Benchmarks/Benchmarks.cs | 13 (`[Benchmark]`) | new Doc (10), DocOptions, Transact (4), TryTransact, GetText/GetMap/GetArray, Insert, Set, Push, EncodeStateAsUpdate, ApplyUpdate, a local `ApplyUpdatesV1` helper | M5 (transact and inserts); M2 (encode and apply) |
| bench/YSharp.Benchmarks/Comparison/BenchmarkComparison.cs | 0 (no tests) | comparison table | no gate |

---

## 3. Tests or groups that cannot turn green under the ADR 0018 order as written

These are the findings most likely to hurt the plan. Each names the reason.

1. **Structs and anything that syncs replicas cannot close at M1.** `DocSupport.Sync` (`tests/YSharp.Tests/DocFeatures/DocSupport.cs:40`) is `to.ApplyUpdate(from.EncodeStateAsUpdate(sv))`. `SyncSupport` (`Protocols/SyncTests.cs`) and `Net.Sync` are the same. So every multi-replica test needs V1 encode and apply (M2) even though M1 "store and integrate" comes first. Affected: `Structs/UpdateApplicationTests.cs`, `Structs/StructStoreTests.cs`, `Structs/IntegrationTests.cs`, `Structs/ErrorPathTests.cs`, `Structs/ContentTests.cs`, `Structs/GarbageCollectionTests.cs`, `DocFeatures/UndoManagerTests.cs`, `DocFeatures/SnapshotTests.cs`. An M1-only close is therefore not possible for these.

2. **Mutators need a transaction, but M4 (shared types) comes before M5 (transactions).** The stub's mutators (for example `YText.Insert`, `YArray.Push`, `YMap.Set`, `src/YSharp/3-Types.cs` lines 102-147) take no transaction argument, and GLOSSARY defines an implicit transaction for "a single call made without an explicit one". The stub's transaction members (`2-Doc.cs` lines 84-94) are the CC (concurrency) surface, which ADR 0018 places in M5 (transactions and events). So M4 cannot close with its own tests: `Types/ArrayTests.cs` (32), `Types/MapTests.cs` (18), `Types/TextTests.cs` (41), `Types/XmlTests.cs` (14), `Types/SharedTypeTests.cs` (17), `Types/StepKindTests.cs` (9) need M5 too. This is an ordering inversion, not a design decision: either M4 includes a minimal transaction core, or M5 is split (see section 4).

3. **Fixture replays need the whole stack.** `Types/TypeFixtureTests.cs` replays both V1 and V2 update bytes (`fixtures/{text,array,map,xml,events}`), which needs M3 and M5. `Codecs/WireFixtureTests.cs` replays text, map and array steps, which need M5. `Protocols/ProtocolFixtureTests.cs` needs M6 and M5. `Structs/StructsFixtureTests.cs` needs DumpStructs and HasPending (M1 seams), plus M4 and M5 for `map.insertType`, `array.insertType` and `mergeUpdates`, plus U for the snapshot and subdoc fixtures.

4. **Doc features are in no ADR 0018 milestone.** UndoManager (`UM`: 28 attributes in `UndoManagerTests.cs`, 26 in `UndoPortTests.cs`, plus UM cases in `SubdocTests.cs` and `ComponentTests.cs`), YSnapshot (`SNAP`, 26), RelativePosition (`RP`, 27), subdocs (`SUB`, 22), PermanentUserData (`PUD`, 10), CollectGarbage (`gc.run`), and document load and sync events (`DOC-13/14`) all have no milestone. ADR 0018 lists only store, codecs, shared types, transactions and events, and awareness and sync. The DocFeatures folder alone is about 142 attributes, and the GC and ItemHelper files add more. None of these can close at a listed milestone.

5. **Hot path BUD-08 spans two milestones.** `TryApplyUpdate` is measured for V1 (M2) and V2 (M3). ADR 0018 says a milestone closes only when its own hot paths are measured (`0018` section "How the budgets gate implementation", point 2). BUD-08 cannot close M2 or M3 cleanly as written. Recorded as two entries or split.

6. **The M0 spike cannot run the three workloads through the public API.** `tests/YSharp.Tests/Allocation/MemoryWorkloads.cs` builds text inserts, array pushes and map keys with `Transact`, `Insert`, `Push` and `Set` (M4 and M5 surface). ADR 0018 requires the spike before M1 is built on a store. So the spike must use a private harness, which should be stated in the follow-up storage ADR.

7. **The skip gate is global, so milestone-by-milestone progress cannot skip cleanly.** `ApiStub.IsStub` probes only `YValue.Null` (`tests/YSharp.TestKit/Stubs/ApiStub.cs` line 20, and `src/YSharp/1-Core.cs` line 59). The probe (`ApiStub.cs` line 13) is computed once. As soon as YValue is implemented, `IsStub` becomes false for every test in the process. From then on `ApiStub.Run` and `Evaluate` do not catch `NotImplementedException`, so an unimplemented member in shared types, undo or sync fails the test instead of skipping. `DocSupport.RequireImplementation` and `ConcSupport.Run` use the same global probe. The handoff's progress measure (skip count falling to zero) therefore breaks as soon as YValue lands. Per-area gates are needed. Also, `Doc`'s constructor body is `{ }` (`src/YSharp/2-Doc.cs` line 38) and `ClientId` is an auto-property, so a bare `new Doc()` does not throw in the stub. A few tests may pass or fail for the wrong reason before implementation.

8. **Seams without any milestone.** `DocSeams` (`ShouldLoad`, `AutoLoad`, `CollectionId`, `Meta`, `CollectGarbage`, `SignalLoaded`, `SignalSynced`), `UndoSeams` (`Undoing`, `Redoing`, `CurrStackItem`), and `Doc.CollectGarbage` are needed by the doc-feature and undo tests and by `gc.run`. None of these is assigned to a milestone. `ISimInternals` (`tests/YSharp.TestKit/Sim/SimHarness.cs`, lines 16-27) needs `HasPending` and `DumpStructs` (M1 seams) and the full stack for the sim (M6).

9. **Sim, interop and fuzz need M6 or the full stack.** `Simulation/*` (74 attributes) need awareness and sync (M6) and the sim internals. `tests/YSharp.Interop.Tests` needs Node and the whole stack. The hostile fuzz (`HostileProperties`, `TruncationTests`, `CorpusReplayTests`) needs sync and awareness decoding. No milestone lists fuzz or hostile input work, so they are unplanned until M6.

---

## 4. Dependency summary

### 4a. Minimal components per milestone (from the entry-point and seam scan)

| Milestone | Minimal components required by the tests | Notes |
|---|---|---|
| P (none listed in ADR 0018) | YValue (factories, scalars, JSON `TryParseJson`/`TryWriteJson`, `AttributeEquals`), `StateVector`, `ItemId`, `YLimits`, `YError`/`YStatus`/exceptions, `TimeProvider` seam | About 100 tests in TestKit, Codecs/JsonTests, ValueQuirk, Traceability, Allocation/BudgetTable run with only YValue. ADR 0018 does not schedule them. |
| M1 Store and integrate | `Doc` object (`ClientId`, `Guid`, `Gc`, `Destroy` placeholder), `GetStateVector`, struct store integration, pending structs (`HasPending`), `DumpStructs`, `InjectInvariantViolation` (fault hook), `Limits` | Closes only the non-syncing structure tests. Most STR tests need M2 as well (finding 1). |
| M2 V1 codec | `EncodeStateAsUpdate` (V1, both overloads), `EncodeStateVector`, `ApplyUpdate`, `TryApplyUpdate` (V1), `YUpdate.TryMerge/TryDiff/TryStateVectorFromUpdate` (V1), `YDecodeException`, `StateVector.Decode/Encode` | Needed by `DocSupport.Sync`, `SyncSupport`, and every fuzz decode test. |
| M3 V2 codec | V2 variants of the above, `YUpdate.TryConvert`, `UpdateV2` event, `applyForeign` with V2 | Needed by `TypeFixtureTests`, `LimitsTests`, `AmplificationTests`, `ReplayAndForeignTests`. |
| M4 Shared types | `GetText/GetArray/GetMap/GetXmlFragment/GetXmlText/GetXmlElement`, `YText` and `YArray` and `YMap` and `YXml*` mutators and reads, `YEntry`, `ToJson`, `Doc.Get` untyped placeholder, `YText.CleanupFormatting` (internal seam, `2-Doc.cs`/`3-Types.cs` line 153), `TextDeltaOp` | Needs a transaction core to run any mutator (finding 2). |
| M5 Transactions and events | `Transact` family, `TryTransact`, `ReadTransact`, `BeginTransaction`, `BeginReadTransaction`, `TransactionScope`, `Transaction`, `Destroy`, `Update` and `UpdateV2` events, `BeforeTransaction`, `AfterTransaction`, `AfterAllTransactions`, `Observe`, `ObserveDeep`, `Unobserve`, `ObserverFaulted`, `YEvent` types, `ScheduleSeam` (internal, `2-Doc.cs` lines 120-125) | `ScheduleSeam` is set by the lock only. |
| M6 Awareness and sync | `Awareness` (ctor takes a `Doc`, `SetLocalState`, `GetStates`, `Meta`, `TryEncodeUpdate`, `TryModifyUpdate`, `EncodeUpdate`, `TryApplyUpdate`, `RemoveStates`, `Dispose`), `YSync` (`WriteStep1/2`, `WriteUpdate`, `TryReadMessage`), `YAuth` (`WritePermissionDenied`, `TryReadMessage`), `AwarenessChange`, `AwarenessMeta` | `Awareness` needs a `Doc` (`src/YSharp/5-Protocols.cs` line 24), so it depends on M1 at least. |
| U (unscheduled) | `YSnapshot` (Create, Encode, TryDecode, CreateDoc, ContainsUpdate), `RelativePosition` and `AbsolutePosition`, `UndoManager` (and seams), `Subdocs` and `SubdocsChanged` and `GetSubdocGuids`, `PermanentUserData`, `CollectGarbage`, `Loaded`/`Synced`/`Destroying`/`Destroyed` events, `ShouldLoad`/`AutoLoad`/`CollectionId`/`Meta` | Needs M2, M4, M5 at least. |

### 4b. Orders that look like they would work better (findings, not decisions)

1. **Split M5 into a transaction core (M5a) and events and observers (M5b), and place M5a before M4.** The mutators are transactional (finding 2), so the transaction core is a prerequisite for any shared-type test. Events and observers can follow M4 without blocking it.
2. **Move awareness state ahead of M6.** The awareness constructor needs only a `Doc` shell (`ClientId`, `Destroy` and the time provider). The `AW-*` files that do not sync (`AwarenessStateTests`, `AwarenessTimeTests`, `AwarenessWireTests`) could start after M1 or M2. Sync (`YSync`) stays at M6. This is an inference from the signatures; not verified against the awareness wire format.
3. **Redefine M1's gate.** M1 should be gated by `DumpStructs`, `HasPending` and state-vector tests, and the STR tests that need decode should move to M2. Otherwise M1 is never "green" on its listed tests.
4. **Add an explicit document-features milestone (U).** It needs M2, M4 and M5. It would give an owner to about 140 DocFeatures attributes plus the GC and ItemHelper cases (section 3, finding 4).
5. **Schedule the values layer (P) explicitly.** YValue, JSON and StateVector are needed by almost every file and have no milestone. A "M-0 values and errors" step, or a statement that they land with the first commit, would make the plan complete.
6. **Make the skip gate per area before the first implementation lands.** See section 3, finding 7. A simple option is one probe per milestone instead of one global probe. This is a finding for the planning agent to decide.
7. **Split BUD-08 into BUD-08a (V1, M2) and BUD-08b (V2, M3).** It is the only budget spanning two milestones.
8. **Let the M0 spike use a private harness.** The spike cannot run the public workloads before M4 and M5, so the follow-up storage ADR should say it runs an internal harness.

---

## 5. ADR 0009 hot paths and BUD-* entries by milestone

Source for the 11 named paths and their budget types: `docs/adr/0009-allocation-and-memory-budgets.md`. Registry: `docs/inventory/budgets.yaml` (BUD-01..14, all `status: pending`, `owner: 14`). Tests: `tests/YSharp.Tests/Allocation/HotPathBudgetTests.cs`, `MemoryTierTests.cs`, `HostileAllocationTests.cs`.

| BUD | Hot path (ADR 0009) | Kind | Entry points exercised | Milestone |
|---|---|---|---|---|
| BUD-01 | Uncontended `Transact`/`ReadTransact`, static lambda | zero | Doc.Transact, ReadTransact | M5 |
| BUD-02 | `TryTransact`, also contended | zero | Doc.TryTransact | M5 |
| BUD-03 | Scalar `YValue` reads and `AttributeEquals` | zero | YValue.TryGet*, AsNumber, AttributeEquals | P (YValue; no milestone) |
| BUD-04 | Event dispatch with no handlers | zero | Doc events, YEvent dispatch | M5 |
| BUD-05 | Commit with no `Update` subscriber | zero | Transact commit path; Update event | M5 (also needs the V1 encode path to be skippable) |
| BUD-06 | `EncodeStateAsUpdate` into a pre-sized writer | zero | EncodeStateAsUpdate (writer overload) | M2 |
| BUD-07 | One-character `YText.Insert` at the end | zero | YText.Insert (via implicit transaction) | M5 (also M4) |
| BUD-08 | `TryApplyUpdate` V1 and V2 | bounded | Doc.TryApplyUpdate (V1, V2) | M2 and M3 (split; finding 5) |
| BUD-09 | `Push` and `Set` of scalars | bounded | YArray.Push, YMap.Set | M5 (also M4) |
| BUD-10 | Sync Step1/Step2 round trip | bounded | YSync.WriteStep1/2, TryReadMessage | M6 (also M2) |
| BUD-11 | Awareness encode and apply | bounded | Awareness.EncodeUpdate, TryApplyUpdate | M6 (may start after M1; finding 2 in 4b) |
| BUD-12 | Hostile input of N bytes allocates at most k*N plus a constant | bounded | TryApplyUpdate, TryConvert, TryDiff, TryReadMessage over the hostile corpus | M2 (V1), M3 (V2), M6 (sync and awareness) |
| BUD-13 | Managed heap per item after forced collection, three workloads | slow tier | Doc.Transact, YText.Insert, YArray.Push, YMap.Set | M5 (the M0 spike must run before M1; finding 6) |
| BUD-14 | Zero gen2 collections, no LOH growth over 100k steady-state edits | slow tier | same as BUD-13 | M5 |

Hot paths in the ADR text that have no BUD entry of their own: none. The registry note confirms "BUD-01..11 are the eleven named hot paths; BUD-12..14 are the other ADR 0009 budgets."

---

## 6. Uncertainty and open items

- **Inferred per-file rows.** The "entry points" column is an identifier scan (`scratchpad/scan.json`), and "Earliest" for files marked `(inferred)` is derived from helpers and naming, not from reading test bodies. The files I read in part are the helpers, the interpreter, the sim interface, `DocSupport`, `SyncSupport`, `TypeFixtureTests`, `WireFixtureTests` and `DocFeatureFixtureTests`. Re-check the rows marked uncertain before using them for scheduling.
- **Implicit transaction.** The claim that every mutator needs a transaction (finding 2) comes from GLOSSARY "Implicit transaction" and the stub comments. It was not checked against `docs/api-decisions.md`. Confirm it with the API decisions before reordering M4 and M5.
- **Awareness earlier than M6.** Finding 2 in section 4b rests on the constructor signature only. The wire and time rules were not traced.
- **`ComparatorTests`, `GenerationTests`, `GuardAndGeneratorSelfTests`, `SimPlanTests`.** I did not confirm whether these touch the library at all. The rows say so.
- **Benchmarks.** `Benchmarks.cs` has 13 `[Benchmark]` methods, not gating. Their milestone is the milestone of the API they call, so they are informational.
- **Counts.** Counts are attributes, not runtime test cases. Theory rows are not expanded.
- **Global skip probe.** Finding 7 is verified against the source (`ApiStub.cs`, `1-Core.cs` line 59) but not run. The behaviour after the first implemented member is inferred from the code.
- **Fixture step vocabulary.** The 60-kind figure comes from a JSON walk of `fixtures/**/*.json`. The mapping of kinds to registries (section 1) is from grep of the registration calls and is complete for the kinds listed there.
