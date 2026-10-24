# The suite is built as 14 specs in four waves, each owning registry IDs

Extends ADR 0010 and 0015. This says how the decided suite is cut into pieces an implementing agent can pick up without the wayfinding conversation. The y-sharp implementation itself is not part of these specs (see the map).

## Rules

- **Tests against a stub.** Spec 1 lands the compiling API stub from `docs/prototype/api-sketch/` (ADR 0008). Tests compile against it and fail or skip until the implementation exists. Every registry entry starts `pending` with an owning spec (ADR 0010 high-water mark) and ratchets to `active` as its spec lands.
- **Ownership.** Each spec owns a set of registry ID prefixes, a set of upstream-test ledger rows and its own directories. Two specs never edit the same files; shared files (the TestKit, the solution) are owned by one early spec and changed through it.
- **Size.** A spec is one agent session of work. If a spec turns out larger during execution, it is split by ID prefix and the split recorded in the breakdown below, not silently widened.
- **Definition of done (every spec).** The traceability check (ADR 0010) passes and, for every entry the spec owns:
  - the required layers have tests carrying `[Covers]`, `[PortOf]` or `[Diverges]` (unit or port for every `match` entry, plus a fixture when `wire: true`);
  - the entry is `active`, or still `pending` with a written reason and an owner, and the spec adds no new pending entries;
  - its ledger rows have an outcome other than `pending`;
  - fixtures regenerate with no drift, the gating tier meets its time budget (ADR 0017), and the traceability matrix is attached to the task.
  Specs that own no registry IDs (1, 2, 4) are done when their stated deliverables pass their own gating tests instead.
- **Inventory population.** Spec 3 creates `docs/inventory/` before any area spec starts: one YAML per area, every ID from the research notes and ADRs registered as `pending` with its `source`, `kind`, `layers`, and owner. It assigns the `STR-nn`, `ENC-nn` and `ERR-nn` numbers, then freezes them (IDs are never renumbered).
- **JS-test ledger.** Spec 3 also creates the ledger with one row per catalogued upstream test, each `pending` with an owner. Ownership follows the upstream file, except that every randomized test (the 48 `testRepeat*` tests, `testInsertAndDeleteAtRandomPositions`) belongs to spec 10 whatever file it sits in. Spec 3 must reconcile the total: the per-file counts in `docs/research/js-tests.md` sum to 210 including the one y-protocols test, while ADR 0010 says 209.

## Breakdown

| # | Spec | Wave | Owns (IDs, ledger, files) | Depends on |
|---|------|------|---------------------------|------------|
| 1 | Scaffold: `YSharp.slnx`, build props, API stub, empty projects, CI skeleton | 0 | `src/`, solution, workflows | none |
| 2 | TestKit: bit-exact PRNG with goldens, ops-script interpreter and tagged values, fixture loader and header check, hex and structural diffs, allocation helper, virtual `TimeProvider`, Node client | 0 | `tests/YSharp.TestKit`, `SIM-10+` harness entries | 1 |
| 3 | Registry, `[Covers]`/`[PortOf]`/`[Diverges]` attributes, traceability checker and matrix, ledger | 0 | `docs/inventory/`, ledger, checker test | 1 |
| 4 | Node fixture generator, drift job, manifest | 1 | `tools/fixture-gen`, `fixtures/` layout | 2 |
| 5 | Values and codecs: `YValue`, JS-compatible JSON writer and reader, V1/V2 encode and decode, merge, diff, convert | 2 | `ENC-*`, `Q*` for number and string quirks; `encoding`, `compatibility`, `updates` ledger rows | 2, 3, 4 |
| 6 | Structs and integration: Item integrate, GC, skip, pending retries, StructStore | 2 | `STR-*`, `GC-*` | 2, 3, 4 |
| 7 | Shared types and events: Text, Array, Map, Xml, observer and event ordering | 2 | `TX-*` and the other types prefixes; `y-map`, `y-array`, `y-text`, `y-xml` deterministic rows | 2, 3, 4 |
| 8 | Doc features: Doc, subdocs, UndoManager, snapshots, relative positions, PermanentUserData | 2 | `DOC-*`, `UM-*`, `SNAP-*`, `RP-*`, `SUB-*`, `PUD-*`; `doc`, `undo-redo`, `snapshot`, `relativePositions` rows | 2, 3, 4 |
| 9 | Awareness, sync and auth | 2 | `AW-*`, `SY-*`; the `awareness` row | 2, 3, 4 |
| 10 | Randomized ports and simulation: seed lists, regression seeds, the 12 golden op-log fixtures, draw-parity test, `SIM-01..05` | 2 | `SIM-*`; all randomized ledger rows | 2, 3, 4 |
| 11 | Hostile input and fuzz: corpus, byte mutator, structure-aware builder, canary, guards, SharpFuzz harness | 3 | `HOS-*`, `ERR-*`; `YSharp.Fuzz.Tests` | 5, 6 |
| 12 | Concurrency: scenarios per `CC-1..CC-20`, hand-rolled scheduler, stress and soak | 3 | `CC-*` | 7, 8, 9 |
| 13 | Live differential harness and `YSharp.Interop.Tests` | 3 | the differential layer declared in the registry | 4, 5, 6, 7 |
| 14 | Allocation, memory and benchmarks: `AllocationBudgets`, slow memory tier, BenchmarkDotNet, nightly comparison | 3 | `BUD-*`; `YSharp.Benchmarks` | 2, 5, 7 |

## Ordering

- Wave 0 runs 1, then 2 and 3 in parallel. Wave 1 is 4. Wave 2 (5 to 10) runs in parallel once wave 0 and 4 are done, because the stub lets every area compile. Wave 3 specs start when the wave 2 specs they list are done, so their tests have real behaviour to assert against and not only the stub.
- Spec 14's budgets stay `pending` until the implementation exists (ADR 0018 sets the milestone gates); its done condition is the helper, the table of IDs and the slow tier running, with the counts reported.

## Not decided here

Writing the 14 specs as Backlog tasks (acceptance criteria and links per spec) is the first step of the handoff and needs the user's go-ahead. The spike store choice and its follow-up ADR belong to the implementation effort (ADR 0018).

## Deviations during execution

Spec 1 owns `src/`, and the plan was that later specs would only compile against the stub. In practice Specs 7 and 9 had to edit the Spec 1 stub in `src/` because the sketch could not express what the ADR 0016 steps and the awareness and sync tests need. None of these changed behaviour (every member still throws `NotImplementedException`); each is a signature the implementer inherits and may revise under ADR 0008:

- **`TextDeltaOp` constructor** (Spec 7, TASK-2.7, `src/YSharp/3-Types.cs`). The struct had only getters, so `ApplyDelta` (T-6) tests could not build ops. It gained `TextDeltaOp(DeltaKind kind, int length, string? text = null, YEntry embed = default, YValue attributes = default)` and get-only properties.
- **`YText.GetAttributes`** (Spec 7, TASK-2.7, `3-Types.cs`). Added `IReadOnlyDictionary<string, YValue> GetAttributes()` for the live attributes (X-2), the same shape `YXmlElement` already had.
- **`Awareness.TryEncodeUpdate`** (Spec 9, TASK-2.9, `src/YSharp/5-Protocols.cs`). Added `YStatus TryEncodeUpdate(IBufferWriter<byte> writer, ReadOnlySpan<ulong> clients, IReadOnlyDictionary<ulong, YValue>? states = null)` for AW-31..33 (order kept, duplicates written twice, an id with no meta fails). `Awareness.EncodeUpdate` lost its `clients` parameter and now encodes every client with a live state.
- **`YSync.TryReadMessage` `errorHandler`** (Spec 9, TASK-2.9, `5-Protocols.cs`). Added a trailing `Action<YError>? errorHandler = null` parameter.

Open questions these raise are collected in `docs/api-decisions.md`.
