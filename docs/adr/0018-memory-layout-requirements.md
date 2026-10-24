# Memory layout is bounded by invariants and a spike gate, not by a prescribed design

ADR 0004 makes GC pressure the main risk of a pure C# implementation and ADR 0009 measures it. This ADR states what the implementation must satisfy so the budgets can be met, without choosing the data structures. A requirement here is something a test, a counter or a review can check.

## Storage model

The implementation chooses how items are represented (an object per item, or structs addressed by index in arenas). It must satisfy these invariants either way:

- **Client ids are 53-bit.** Internal tables hold client ids up to 2^53-1 (the public type is `ulong`, ADR 0008), not 32-bit ones; how they are keyed stays internal.
- **Chunked storage.** No growing structure the document owns, such as a client's struct list, a content pool or the delete set, is one contiguous allocation larger than about 64 KB. It grows by adding chunks, so steady-state edits never grow the large object heap.
- **No per-item machinery.** An item carries no finalizer, lock, event object, delegate or closure. Neighbour and parent links, origin ids and the parent sub-key do not require a separate heap object per link.
- **Document-owned memory.** All memory held for a document is reachable only from that document. There are no finalizers, no static or global caches keyed by document, and no timer or handler that roots a document after it is destroyed. Destroying a document releases every chunk and unsubscribes everything it subscribed (the JS leak in PUD-05 is not reproduced).
- **Declared exceptions.** One caller-supplied payload larger than the chunk size (a single big binary or string) may be one allocation. The LOH allowance in ADR 0009 covers it and nothing else.

## Content

- Text and binary content are slices of shared chunked buffers, so splitting an item when an update integrates in the middle of it allocates nothing and copies nothing.
- Decoded `YValue` and Any payloads are copied once into document-owned storage and never alias caller buffers (ADR 0007).
- Deleted content shrinks to the minimal form the wire needs, and its payload storage is released. A garbage-collected struct keeps only what encoding a state vector and a delete set requires.

## Transient memory

- Decode and encode use rented buffers and writers. Transactions, event objects and dispatch lists are reused per document or per thread (ADR 0008).
- Pending-struct queues and every other transient structure are bounded by `YLimits` (ADR 0005). Transient growth is proportional only to input size.
- Every rented buffer is returned on every exit path, including decode errors, rejected structs and faulted documents. The hostile-input post-condition (ADR 0013) checks that nothing stays rented after a failed apply.

## Not required

Compaction of fragmented chunks and a fixed per-item byte count are not requirements. The bytes-per-item ceiling in ADR 0009 is the only footprint gate, and a heavy-delete fragmentation workload can be added later if the slow tier shows a problem.

## How the budgets gate implementation

1. **Spike gate (M0).** Before decode and integrate are built on a store, a spike of the chosen storage model runs the three slow-tier workloads (1M text inserts, 1M array pushes, 100k map keys) and the steady-state check (zero gen2 collections, no LOH growth over 100k edits). Bytes per item must be at or below the Yjs figure. The choice between object-per-item and indexed arenas is recorded in a follow-up ADR with those measurements. Nothing builds on a store that fails. The spike's design is under "Amendment: M0 spike design" below.
2. **Milestone gate.** The milestones are listed under "Amendment: milestone boundaries and order" below. They replace the original list (store and integrate, V1 codec, V2 codec, shared types, transactions and events, awareness and sync). A milestone is done only when its hot paths from ADR 0009 are in `AllocationBudgets` with measured values (plus 25%), the zero-byte paths read 0, and its memory gate is green. Budgets only move down after that.
3. **Pending hot paths.** A hot path whose milestone is still open may be pending. Pending paths are counted and the count never grows, like pending registry entries (ADR 0010). No milestone closes with a pending path of its own.
4. **Layout changes.** Changing the storage model after M0 reruns the spike workloads and is a reviewed change with a reason.

## Amendment: milestone boundaries and order

Decided with the owner (TASK-3.4), from `docs/research/test-surface.md`. The gate rules above are unchanged; the milestone boundaries and order change. The original order did not work. Almost every structure test reaches integrate through V1 `ApplyUpdate`. Every mutator runs in a transaction that takes the document lock (ADR 0006 CC-8), but shared types came before transactions. Values and doc features had no milestone.

| Milestone | Depends on | Owns (registry prefixes) | Test files that turn green | Budgets it closes | Seams it provides |
|---|---|---|---|---|---|
| **M0** Storage spike | none | none | none (private harness) | none; records the spike figures | none |
| **M1** Values and errors | none (runs alongside M0) | `V`, `Q` value, string and varint quirks, the value-level `ERR` entries | `Codecs/JsonTests`, `ValueQuirkTests`, `StringQuirkTests`, TestKit self-tests that touch `YValue` | BUD-03 | none |
| **M2** Core: store, integrate, transactions, V1 | M0, M1 | `STR`, `GC`, `ENC` V1 entries, `Q` client-id quirks, decode `ERR` and `HOS` for V1 | `Structs/*` (except V2 cases), `Codecs/UpdateLayoutTests`, `UpdateFunctionTests` and `MalformedInputTests` (V1), `UpstreamPortTests` (V1) | BUD-06; records the V1 value of BUD-08 | `DumpStructs`, `HasPending`, `InjectInvariantViolation`, `ScheduleSeam` (the lock reports through it) |
| **M3** V2 codec | M2 | `ENC` V2 entries, `TryConvert` | V2 cases of the codec and structure files, `Codecs/WireFixtureTests` once M4 has landed | BUD-08 | none |
| **M4** Shared types and events | M2 | `T`, `A`, `M`, `X`, `Y`, `E`, `F`, `D`, `TX`, observer-fault `ERR` | `Types/*`, `Concurrency/ObserverTests`; `TypeFixtureTests` once M3 has landed | BUD-01, 02, 04, 05, 07, 09, 13, 14 | `YText.CleanupFormatting` |
| **M5** Doc features | M3, M4 | `DOC`, `UM`, `SNAP`, `RP`, `SUB`, `PUD` | `DocFeatures/*` | none | `DocSeams` members (`ShouldLoad`, `AutoLoad`, `CollectionId`, `Meta`, `CollectGarbage`, `SignalLoaded`, `SignalSynced`), `UndoSeams` members |
| **M6** Protocols | M1, M2 | `AW`, `SY`, `AU`, `PR` | `Protocols/*` (`ProtocolFixtureTests` once M4 has landed) | BUD-10, BUD-11 | none |
| **M7** Integration and release | all | `SIM`, `DIFF`, `CC`, `HOS` (full), BUD-12 | `Simulation/*`, `Concurrency/*` scenarios and stress, `YSharp.Fuzz.Tests`, `YSharp.Interop.Tests`, `Allocation/HostileAllocationTests` | BUD-12; the release gate (zero pending, zero skips) | `ISimInternals` |

- **Order.** M0 and M1 run in parallel. M2 is sequential. After M2 the work fans out: M3, M4 and M6 run in parallel, and M5 follows M3 and M4. M7 closes. A test that needs two milestones turns green when the later one lands.
- **The lock lands in M2.** Every entry point acquires it, and adding it later would touch all of them. M2 builds the simple form. The CC scenario, stress and soak tests close in M7.
- **Events belong to shared types (M4).** The type tests observe events, so types cannot close without them. `UpdateV2` event tests close when M3 lands too.
- **BUD-08 stays one entry.** M3 owns it. M2 records the V1 figure as soon as it is measurable, and M3 closes the entry with both figures. It is not split, because a new pending entry would breach the high-water mark.
- **The memory gate depends on the milestone.** M1 has no store and no memory gate. M2 and M3 rerun the M0 spike's private-harness workloads against the real store, so the spike harness is kept. From M4 onwards, when the public workloads in `Allocation/MemoryWorkloads.cs` can run, the slow memory tier is the gate.
- **Hostile input closes as it goes.** Each codec or protocol milestone makes its own decode-side hostile tests pass. M7 owns the full fuzz, canary and BUD-12 runs over every decoder.
- The per-ID split inside a prefix (for example V1 versus V2 `ENC` entries) is made when the plan is packaged (TASK-3.8), from the registry `layers` and the test files.

## Amendment: M0 spike design

Decided with the owner (TASK-3.5), from `docs/research/storage-layout.md`.

- **Candidates.** Both storage models are built: object-per-item (A) and structs in chunked arenas addressed by index (B). Each runs local operations only. Each implements:
  - a client table, and per-client sequences that support split
  - neighbour, origin and right-origin links, and the parent sub-key
  - local insert and delete
  - the Yjs merge rules at commit (`Item.mergeWith`)
  - search markers, or an index with the same per-operation cost
  - a chunked delete set
  - a map key table
  - release of deleted payloads
  - chunks of 64 KB or less

  Not built: remote conflict resolution, codecs, events and the lock. The design must still leave room to add remote integration.
- **Shared content layer.** Text-buffer slices, homogeneous 8-byte value columns and map payloads are one layer that both candidates use. The comparison therefore isolates the item model. The array figure of 8.31 B per operation needs the column whichever model wins.
- **Driver and harness.** The workloads run through a small driver: insert a character at an index, delete at an index, push a value, set a key, commit. The two candidates are two adapters. In M2 the real `Store/` module becomes a third adapter, and the M2 and M3 memory gates rerun the same harness. The harness and driver live in `tests/YSharp.Tests/Allocation` (Nightly tier, Release only, internals visible). The candidates live in a throwaway `spike/YSharp.StoreSpike` project. The losing candidate is deleted when the storage ADR lands. M2 may port from the winner but does not have to. The spike project is deleted once M2's store passes the harness.
- **Workloads.** These are the three slow-tier workloads exactly as `MemoryWorkloads` defines them, and the steady-state mix of `MemoryWorkloads.SteadyStateEdits`.
- **Measurement.** The method is the same as `measure.mjs`: force two collections, read the heap, build, force two collections, read again, and divide by operations. Runs use Release, TieredCompilation off and workstation GC (the test projects' configuration). Each workload runs in its own process, and the median of 3 runs is taken. The steady-state check follows `MemoryTierTests`. Throughput (operations per second) is recorded but not gated.
- **Selection.**
  1. A candidate that fails any gate is out. The gates are text 194.77, array 8.31 and map 312.26 bytes per operation, zero gen2 collections, and no LOH growth beyond the allowance.
  2. If both pass, the one with more headroom on its tightest gate wins.
  3. If both have at least 20% headroom on every gate, object-per-item wins because it is simpler.
  4. A winner more than 2x slower than the other needs the owner's sign-off.
  5. If both fail, work stops and goes to the owner. A hybrid spike is the next step.
- **The follow-up storage ADR records:**
  - a measurement table per candidate: bytes and structs per operation, gen2 count, LOH growth, throughput, plus runtime, GC configuration and commit
  - the choice, and which selection rule decided it
  - how the winner fits behind the `ItemRef` handle (ADR 0021)
  - for arenas, the index-reuse policy (a generation check, or no reuse)
  - the rule for releasing deleted payloads
  - the remaining risks, and how to rerun the spike
