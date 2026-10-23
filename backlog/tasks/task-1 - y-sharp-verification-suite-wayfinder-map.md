---
id: TASK-1
title: 'y-sharp verification suite: wayfinder map'
status: To Do
assignee: []
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:57'
labels:
  - 'wayfinder:map'
dependencies: []
ordinal: 1000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Destination

A spec-ready map that implementing agents can be handed to build y-sharp's **verification system**: an xUnit test suite (plus interop, fuzz and benchmark projects) that documents every Yjs v13 edge case and behaviour, and proves interop with the JS implementation. The implementation of y-sharp itself is NOT part of this effort; the suite is what lets it be built with confidence.

## Notes

- Oracle: Yjs v13.6.33 (submodule `yjs/`), lib0 v0.2.99 (`lib0/`), y-protocols v1.0.7 (`y-protocols/`). All pinned. v14 is out of scope.
- Core requirements: byte-exact wire interop with JS; low-GC modern .NET; crash safety is non-negotiable (hostile bytes never crash/hang/over-allocate, failed apply leaves a consistent doc, no escaping observer exceptions, bounded memory and no stack overflow on deep structures).
- Interop strategy, layered: (c) port JS tests to xUnit, (a) golden fixtures from a Node generator (fast CI gate), (b) live Node differential harness (slow, fuzzed).
- Match JS quirks by default; deliberate divergences are recorded decisions with a tagged test showing the JS behaviour. Unsafe behaviour always diverges.
- Thread safety is in scope: the concurrency contract (what may be shared across threads, what is serialized, observer threading) is decided in the thread-safety ticket and tested with stress/race tests.
- Implementation is pure C# (ADR 0004); a native core with a wrapper was considered and rejected. The eventual implementation ticket must treat GC pressure and memory layout as a first-class requirement, and the suite's allocation, memory and Yjs-comparison budgets are how that is proven.
- Targets: net10.0, xUnit v3, FsCheck allowed, other libraries allowed where they bring real engineering value. Projects: YSharp.Tests, YSharp.Interop.Tests, YSharp.Fuzz.Tests, YSharp.Benchmarks, tools/ (Node fixture generator).
- Public `YValue` struct pins the mapping of JS Any values. API contract is a prototype ticket; implementing agents write the compiling stub as their first task.
- Allocation budgets asserted on a few named hot paths in the normal suite; BenchmarkDotNet lives in its own project and does not gate CI.
- Fixtures carry yjs/lib0 versions + seed; CI fails on version/fixture mismatch; regeneration is an explicit reviewed step. Yjs clientIDs are set explicitly.
- Tracker: Backlog.md. Map = this task; tickets = its subtasks labelled `wayfinder:<type>`; blocking = task dependencies; claim = assignee; resolution = Final Summary + Done. Use `GLOSSARY.md` and `docs/adr/` (lazy).
- Plan, don't do. Research tickets write Markdown into `docs/research/` (no branches; the repo has no commits yet).
- Consult skills: grilling + domain-modeling for HITL tickets, research for AFK, prototype for the API ticket.

## Decisions so far

- [Research: struct and integration behaviour inventory](TASK-1.1): behaviours of structs, Item.integrate, GC, StructStore, Transaction and pending retries, with port-flagged JS-only cases (`docs/research/structs.md`).
- [Research: encoding and update behaviour inventory](TASK-1.2): V1/V2 layouts, Any encoding, merge/diff/convert; ran on Node with hex goldens; JS decoders are lenient and non-atomic on truncation (`docs/research/encoding.md`).
- [Research: shared types and events inventory](TASK-1.3): per-type behaviours and observer/event ordering, including exception and event-lifetime quirks (`docs/research/types.md`).
- [Research: doc-level features inventory](TASK-1.4): Doc, subdocs, UndoManager, snapshots, relative positions, PermanentUserData, GC; needs injectable RNG/clock/scheduler seams (`docs/research/doc-features.md`).
- [Research: catalog of the existing JS tests](TASK-1.5): 209 tests catalogued, the xoroshiro PRNG must be ported bit-exact, the simulation only checks convergence so goldens are needed, and several upstream test defects (`docs/research/js-tests.md`).
- [Research: y-protocols inventory](TASK-1.6): awareness, sync and auth behaviours; only one upstream test; timeouts need an injectable clock (`docs/research/y-protocols.md`).
- [Research: JS quirk register](TASK-1.7): 32 quirks with match/diverge proposals; number tag selection, NaN and -0, key ordering, JSON writer, stable sorts (`docs/research/quirks.md`).
- [Research: Yrs concurrency model](TASK-1.19): Yrs uses one reader/writer lock via transactions with synchronous observers under the lock; no re-entrancy; thin tests (`docs/research/yrs-concurrency.md`).
- [Error model and crash-safety contract](TASK-1.12): non-throwing `Try*` with `ApplyResult`, strict EOF, atomic decode, quarantined integration, `YLimits`, no recursion on depth, contained observer faults, faulted-doc state (`docs/adr/0005-error-model-and-crash-safety.md`).
- [Thread-safety and concurrency contract](TASK-1.18): one re-entrant (per thread) reader/writer lock per doc, synchronous observers under the write lock, snapshot handler lists, thread-affine sync-only transactions, own locks for awareness and subdocs, 20 numbered guarantees and a stress/soak test design (`docs/adr/0006-concurrency-contract.md`).
- [YValue mapping for JS Any values](TASK-1.9): nine cases, Number=double and BigInt=long with no wire tag, NaN canonical, -0 kept, JS key order only on encode, `__proto__` diverges, immutable copy-on-decode, wire vs attribute equality, one tree for Any/V1 JSON/awareness (`docs/adr/0007-yvalue-contract.md`).
- [Public API contract prototype](TASK-1.14): reviewed sketch in `docs/prototype/api-sketch/`; callback and ref-struct transactions with implicit joining, pooled dispatch-scoped events, `YEntry` content, Try* beside throwing forms, three recorded divergences (`docs/adr/0008-public-api-shape.md`).
- [Allocation budgets and named hot paths](TASK-1.15): 11 gated hot paths, plain xUnit helper (Release, TieredCompilation off), no wall-clock gates, ratcheted ID-keyed budgets, hostile-input allocation bound, slow-tier bytes-per-item capped by Yjs, report-only BenchmarkDotNet (`docs/adr/0009-allocation-and-memory-budgets.md`).
- [Behaviour inventory format and traceability tags](TASK-1.8): existing IDs canonical, YAML registry in `docs/inventory/`, `[Covers]`/`[PortOf]`/`[Diverges]` tags, reflection-based gating check with matrix, layer and exclusion rules, 209-test JS ledger, ratcheted pending status (`docs/adr/0010-traceability-registry.md`).
- [Fixture format and Node generator spec](TASK-1.10): declarative versioned ops scripts replayed by Node and C# interpreters, tagged JSON values, per-scenario files with manifest, regenerate-and-diff drift job on pinned Node, recorded op logs for simulations, named observations, stored malformed cases with test-time truncation sweeps (`docs/adr/0011-fixture-format-and-generator.md`).
- [Live differential harness design](TASK-1.11): stateless ops scripts against one long-lived Node child over NDJSON, y-sharp↔Node and shuffled-delivery convergence compared, FsCheck on the C# side with a divergence-targeting generator, virtual clock, timeouts with `oracle-misbehaved`, replayable failures, smoke PR job plus nightly slow tier (`docs/adr/0012-live-differential-harness.md`).
- [Hostile-input and fuzz strategy](TASK-1.13): nine `HOS-*` classes, fixed corpus plus generated seeds and mutations with a non-gating SharpFuzz job, three FsCheck layers (byte mutator, structure-aware builder, ops-script), time/allocation/stack/heap guards with a canary process and a consistency post-condition, per-commit/smoke/nightly tiers, oracle outcome-class comparison, tiny-limits passes (`docs/adr/0013-hostile-input-and-fuzz-strategy.md`).
- [Awareness and presence test scope](TASK-1.17): all three y-protocols modules in scope, providers out; `TimeProvider` seam; match/diverge split for the quirks (atomic bad batch, idempotent destroy); structural-equality change filter; awareness ops steps, oracle-generated goldens, awareness and sync in the live differential (`docs/adr/0014-awareness-and-protocol-test-scope.md`).
- [Test project layout, tooling and CI](TASK-1.16): one slnx, a shared TestKit with a bit-exact PRNG port, xUnit v3 + FsCheck + YamlDotNet, `Tier` traits, serial allocation/stress collections, Linux-first GitHub Actions with drift, smoke and nightly jobs, report-only coverage (`docs/adr/0015-test-project-layout-tooling-and-ci.md`).
- [Ops-script vocabulary and canonical dump formats](TASK-1.20): named replicas with explicit client ids, root/path addressing, `transact` steps with origin, no invented defaults, step kinds per area in a normative schema, typed `doc` tree, clock-ordered `structs` dump, run-length truncation outcomes (`docs/adr/0016-ops-script-vocabulary-and-dump-formats.md`).
- [Research: Coyote versus hand-rolled schedule exploration on net10.0](TASK-1.22): hand-roll a seeded cooperative scheduler with yield/block seams in the lock and observer list; Coyote 1.7.11 targets net8.0, net10 support is an unmerged PR, ReaderWriterLockSlim is uncontrolled, xUnit v3 unverified (`docs/research/schedule-exploration.md`).
- [Research: JS-compatible JSON writer and reader](TASK-1.21): JSON is V1-only (embeds, formats, ContentJSON, awareness); JS number layout and string escaping are not reproducible with System.Text.Json, so a custom writer and reader are needed; parse is strict with listed oddities (`docs/research/json.md`).
- [Port plan for the randomized JS tests](TASK-1.23): fixed gating seed lists by size class (200/40/10), nightly time-boxed random seeds, 12 golden op-log fixtures with a draw-parity test, simulation defects fixed as SIM-01..05 (`docs/adr/0017-randomized-test-port-plan.md`).
- [Memory layout requirements for the implementation](TASK-1.24): outcomes plus hard invariants (chunked storage, no per-item machinery, document-owned memory), storage model left to the implementer behind an M0 spike gate, content as shared chunk slices, pooled bounded transients, milestone gates with ratcheted budgets (`docs/adr/0018-memory-layout-requirements.md`).
- [Package the map into implementing-agent specs](TASK-1.25): 14 specs in four waves (foundation, generator, per-area, cross-cutting) built against the API stub, each owning registry ID prefixes and ledger rows, done when the traceability check is green with no new pending; spec 3 populates the registry and ledger (`docs/adr/0019-implementing-agent-spec-breakdown.md`).

## Not yet specified

- Quirk/divergence register content (after the JS quirk research lands).
- Thread-safety test scenarios per guarantee CC-1..CC-20, seeds and tiers (tool choice: hand-rolled scheduler, see the Coyote research ticket).
- UndoManager, snapshot, relative-position, subdoc and PermanentUserData test spec detail (after doc-level research).
- Benchmark suite spec: workload definitions shared with the fixtures, and the report format for the nightly comparison (hot-path list and budgets are decided in ADR 0009).
- Observer/event ordering and re-entrancy test spec.
- Awareness/sync/auth test spec detail: per-ID golden vector list, the two-peer convergence harness, and the structural-equality case table (ADR 0014).
- Differential harness spec detail: protocol message catalogue, per-area step and observation registration, per-divergence normalisers, `applyForeign` and delivery-schedule step shapes, fixture-candidate `repro` header.
- Hostile-input spec detail: concrete fixed-corpus file list per `HOS-*` class, the structure-aware update builder's model and API, calibrated k and timeout values, and the SharpFuzz harness setup.

## Out of scope

- Implementing y-sharp itself.
- Async/awaitable transactions: the concurrency contract is sync-only ([Thread-safety and concurrency contract](TASK-1.18)).
- Yjs v14 and any other version.
- Provider concerns of y-protocols: outer message envelope, SyncDone, read-only filtering, transport.
<!-- SECTION:DESCRIPTION:END -->
