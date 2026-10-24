# Items are one heap object each (the M0 spike result)

Closes the M0 spike gate of ADR 0018. Both candidates of the spike amendment were built (TASK-4.4, TASK-4.5) and measured with the spike harness (TASK-4.6). Object per item (candidate A) is the storage model that `Store/` builds on in M2. The arenas candidate (B) is deleted.

## Measurements

The harness runs each workload in 3 fresh processes (`YSharp.StoreSpike measure <candidate> <workload>`) and takes the median. Every run gave the same bytes per operation. Gates are the Yjs figures from ADR 0009. Headroom is (gate − measured) / gate.

- **Runtime:** .NET 10.0.11 (SDK 10.0.400), x64, Windows 11, Intel i9-9900KF.
- **GC configuration:** Release, `TieredCompilation` off, workstation GC (`ServerGc: false` in every record).
- **Commit:** `impl/task-4` at `b8dee6f`, which includes candidate A's push and map fix (`dd9ff4e`).

| Workload (gate, B/op) | Candidate | Bytes/op | Headroom | Structs/op | Gen2 | LOH growth | Ops/s |
|---|---|---|---|---|---|---|---|
| text-insert, 1M (194.77) | A | 98.17 | 49.6% | 1 | 2 | 0 | 1.42M |
| | B | 66.28 | 66.0% | 1 | 2 | 0 | 0.78M |
| array-push, 1M (8.31) | A | 8.079 | 2.78% | 1 | 0 | 0 | 13.2M |
| | B | 8.202 | 1.30% | 1 | 0 | 0 | 15.8M |
| map-set, 100k (312.26) | A | 165.52 | 47.0% | 1 | 0 | 0 | 1.49M |
| | B | 112.92 | 63.8% | 1 | 0 | 0 | 2.06M |
| steady state, 100k edits (no bytes gate) | A | 47.93 | n/a | 0.5 | 0 | 0 | 45.9k |
| | B | 26.88 | n/a | 0.5 | 0 | 0 | 36.2k |

The gen2 collections on the text build are not a gate failure. The owner confirmed (TASK-4.6) that the zero-gen2 gate covers only the steady-state check, as ADR 0009 words it.

## Decision

Rule 2 of the spike amendment decides: **"if both pass, the one with more headroom on its tightest gate wins."** Both candidates' tightest gate is array-push, where A has 2.78% headroom and B has 1.30%.

- **Rule 1.** Both candidates pass every gate: bytes per operation on all three workloads, zero gen2 collections in the steady state, and no LOH growth.
- **Rule 3** does not apply. Neither candidate can have 20% headroom on the array gate. The shared 8-byte value column alone costs about 8.07 B per operation against a gate of 8.31. The owner ruled the rule not applicable (TASK-4.6).
- **Rule 4** does not escalate. A is not more than 2x slower than B on any workload. It is slower on array-push (1.19x) and map-set (1.38x), and faster on text (1.8x) and the steady state (1.27x). An earlier measurement had A 19x slower on array-push. The cause was a push that walked every item pushed since the last merge. It was fixed in candidate A (`dd9ff4e`) before this decision, and the owner approved that fix.
- **Rule 5** does not apply.

Rule 2 won by 0.12 B per operation. A wins on the gate the rules weigh, but B used less memory on every other workload: about 32 B less per character, 53 B less per map key and 21 B less per steady-state edit. If a later change pushes A over a gate, the arenas design is the documented alternative. ADR 0018 point 4 governs that change: it reruns the spike workloads and is a reviewed change with a reason.

## How it fits behind `ItemRef` (ADR 0021)

- `ItemRef` is a `readonly struct` holding one `Item?` reference. Passing it costs the same as passing the object. Each accessor (`Clock`, `Length`, `IsDeleted`, `IsCountable`, `Kind`, `Left`, `Right`) is one inlined field read.
- Equality is reference identity, and `IsNull` stands for Yjs's `null` neighbour.
- Accessors that need document tables live on the store, not on the handle. These are `ClientOf`, `OriginOf` and `RightOriginOf`, which map a client slot to its 53-bit id.
- `Item` stays internal to `Store/`. Integration, codecs and types see only `ItemRef`.
- Index reuse does not arise. A handle is a reference, so a merged-away item lives as long as a handle to it does. A stale handle reads a merged-away item instead of aliasing a reused slot. Code that holds handles across a commit has to re-find by id (Yjs `getItemCleanStart`), as Yjs code does today.

## Releasing deleted payloads

The rule follows Yjs `Item.gc` with a live parent and runs at commit, in the `tryGcDeleteSet` step.

- **Deleted items.** A deleted item that is not marked `Keep` changes its content kind to `Deleted`, becomes non-countable and clears its two content indexes. It keeps only its id, length, links and origins: what the state vector and the delete set need to encode.
- **Text.** The store keeps a live-character count per text-buffer chunk. Garbage-collecting a text item subtracts its length from its chunk's count. When a chunk other than the one being appended to reaches zero, the store calls `TextBuffer.Release`, so the GC reclaims the chunk.
  - The spike counts releasable chunks (`ReleasableTextChunks`) but does not call `Release`.
  - M2's store makes that call, and the M2 tests check that a fully deleted chunk is released.
- **Array values.** The 8-byte value column is not reclaimed piecewise. ADR 0018 does not require compaction.
- **Map keys.** A map key keeps its payload slot for the key's lifetime. Overwriting a key hands the slot to the new item, so a set over an existing key adds no payload.

## Remaining risks

- **Thin array headroom.** The array gate has 0.23 B of headroom per operation, and nearly all of the cost is the shared value column. Any per-element cost added in M2 fails the gate, for example a tag byte, a boxed value, or a type column without run encoding. Mixed-type arrays need a separate kind column that is run-encoded.
- **Wider clocks.** The spike holds clocks as `uint` and clients as `int` slots. The public `ItemId` clock is `long` (ADR 0008).
  - Three clocks per item widened to 64 bits add about 16 B per item. That puts text at about 114 B per character and map-set at about 182 B per key, both still under their gates.
  - Array runs merge, so the array figure does not move.
- **Gen2 as documents grow.** Every live item is a traced object. The steady state shows zero gen2, but the 1M-item builds do trigger gen2 collections. Pause time is report-only (ADR 0009). Watch it in the M7 soak.
- **Remote integration is unbuilt.** Candidate A's `Integrate` throws where the Yjs conflict-resolution scan would run. M2 adds the scan.
- **Rule 2 was decided by 1.5 points of headroom.** A small layout change in M2 could reverse the ranking without failing a gate. The M2 and M3 memory gates rerun the harness against the real store and hold it to the figures above.

## How to rerun

1. `dotnet build spike/YSharp.StoreSpike -c Release`
2. For each workload in `text-insert`, `array-push`, `map-set` and `steady-state`, run `dotnet spike/YSharp.StoreSpike/bin/Release/net10.0/YSharp.StoreSpike.dll measure object-per-item <workload>` three times and take the median. Each run prints one JSON record with the table's fields.
3. Alternatively, run the Nightly theory with `YSHARP_TIER=Nightly`: `dotnet test --solution YSharp.slnx -c Release` with `SpikeObjectPerItemTests.Process_ObjectPerItem_MeetsTheSpikeGates`. It runs the 3 processes and asserts the gates.
4. In M2, the real store joins `SpikeCandidates` as a third adapter and runs the same workloads (TASK-4.11). The spike project is deleted once that store passes at or below these figures.

## Consequences

- Candidate B (`spike/YSharp.StoreSpike/Arenas/`) and its tests (`ArenaStoreTests`) are deleted.
- Candidate A stays where it is: `spike/YSharp.StoreSpike/ObjectPerItem/`, registered as `object-per-item` in `SpikeCandidates`. M2 may port from it but does not have to.
- The shared content layer (`spike/YSharp.StoreSpike/Content/`) and the harness (`tests/YSharp.Tests/Allocation/Spike/`) are unchanged. `TextBuffer.Release` stays, because the payload release rule above uses it.
