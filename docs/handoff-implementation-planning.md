# Handoff: from the verification suite to the y-sharp implementation plan

Written when the verification suite (backlog TASK-2, specs 1 to 14) was complete and merged into `main`. The next job is to **plan** the y-sharp implementation (not build it yet). This note says what exists, what is unproven, and what the planning agent must know.

## 1. What y-sharp is, and what is already built

y-sharp is a pure C# port of Yjs v13 (ADR 0004). The oracle is pinned: Yjs v13.6.33, lib0 v0.2.99, y-protocols v1.0.7, as git submodules (`yjs/`, `lib0/`, `y-protocols/`). Yjs v14 is out of scope.

The **implementation does not exist**. What exists is the verification suite that lets it be built with confidence:

| Piece | Where |
|---|---|
| API stub (every member throws `NotImplementedException`) | `src/YSharp/` (five files from the old API sketch) |
| Solution, build props, SDK pin | `YSharp.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (SDK 10.0.x, Microsoft.Testing.Platform runner, net10.0) |
| Unit and port tests | `tests/YSharp.Tests` (about 1,200 test methods, mostly skipped against the stub) |
| Test helpers: PRNG with lib0 goldens, ops-script interpreter, fixture loader, diffs, allocation helper, virtual `TimeProvider`, Node client, sim harness port, concurrency scheduler, traceability attributes and checker | `tests/YSharp.TestKit` |
| Hostile input, fuzz, canary | `tests/YSharp.Fuzz.Tests`, `tests/YSharp.Fuzz.Canary`, `tools/sharpfuzz` |
| Live Node-versus-y-sharp differential harness | `tests/YSharp.Interop.Tests` |
| Benchmarks and Yjs memory baseline | `bench/YSharp.Benchmarks`, `bench/yjs-memory` |
| Fixture generator (replays ops scripts on the pinned Yjs) and about 141 committed fixtures (about 20 MB, `fixtures/sim` is 18 MB) | `tools/fixture-gen`, `fixtures/` |
| Behaviour registry (one YAML per area), JS-test ledger (210 rows), ID lock, pending high-water mark, per-spec traceability matrices | `docs/inventory/` |
| CI workflows (gating, fixture drift, smoke, nightly, sharpfuzz) | `.github/workflows/` |

## 2. Read these first

1. `CLAUDE.md` (Backlog.md workflow is mandatory) and `GLOSSARY.md` (ubiquitous language: Oracle, Fixture, Divergence, Registry entry, Pending entry, Ops script, and so on).
2. ADRs in `docs/adr/`: 0003 (match JS by default; divergences are recorded), 0004 (pure C#), 0005 (error model, limits, faulted documents), 0006 (concurrency contract), 0007 and 0008 (value and public API shape), 0009 (allocation budgets), 0010 (traceability registry), 0011 and 0016 (fixtures and ops scripts), 0015 (layout, tooling, CI, naming, tiers), 0018 (memory layout and the M0 spike gate), 0019 (how the suite was cut into 14 specs, plus "Deviations during execution").
3. `docs/api-decisions.md` (every API decision, all marked Decided).
4. `docs/research/*.md` (structs, encoding, types, quirks, JSON, y-protocols, schedule exploration, Yrs concurrency).
5. Backlog: `backlog task view 1 --plain` (the wayfinder map) and `backlog task view 2 --plain` (the specs, with long implementation notes).

## 3. The implementation order is already decided (ADR 0018)

- **M0, spike gate.** Before decode and integrate are built on a store, a spike of the chosen storage model must run the three slow-tier workloads (1M text inserts, 1M array pushes, 100k map keys) plus a steady-state check (zero gen2 collections, no LOH growth over 100k edits). Bytes per item must be at or below the Yjs figure. The Yjs numbers are in `bench/yjs-memory/yjs-bytes-per-item.json` (text 194.77, array 8.31, map 312.26 bytes per item). The choice between object-per-item and indexed arenas is recorded in a **follow-up ADR with the measurements**. Nothing builds on a store that fails.
- **Milestones:** store and integrate, V1 codec, V2 codec, shared types, transactions and events, awareness and sync. A milestone is done only when its hot paths from ADR 0009 are in `AllocationBudgets` with measured values (plus 25%), zero-byte paths read 0, and the slow memory tier is green. Budgets only move down afterwards.
- Layout invariants: chunked storage, no per-item machinery, document-owned memory, pooled bounded transients (ADR 0018).

## 4. Decisions made (do not reopen without a reason)

All in `docs/api-decisions.md` and the ADRs. Headlines:

- Client ids are `ulong`; wire ids up to 2^53-1 are accepted and round-trip. At or above 2^53 they are Malformed (a recorded divergence: JS rounds silently). ENC-37 and Q12 stay `diverge`; ENC-26 and ENC-57..59 stay excluded (no `obfuscateUpdate`, `parseUpdateMeta` or `logUpdate` equivalents).
- Added to the stub: `Doc.GetXmlText`, `YArray.Slice`, `Transact<TResult>`, `TransactWithTimeout` (renamed to avoid the null-origin overload trap), the transaction list on `AfterAllTransactions`, `UndoManagerOptions.Doc`, `Doc.GetSubdocGuids`, `YXmlFragment.InsertSubdoc`.
- Kept hidden: items and structs. Kept pooled: events (valid only during dispatch; `GetPath()` is relative to `CurrentTarget`).
- Limits (ADR 0005): exceeded when value > limit; checked before allocation; on every decode path including merge, diff, convert and V2 columns; document unchanged on failure. `TryMerge/TryDiff/TryConvert/TryStateVectorFromUpdate` take an optional `YLimits`.
- Stale views throw the new `YLifetimeException : YException`. `TryTransact` under a held read lock returns `UpgradeRefused`; the throwing forms throw `YConcurrencyException`.
- `Awareness.GetStates` returns a snapshot (AW-13 is a divergence). A null auth handler throws `ArgumentNullException`.
- Clocks and lengths that are negative, non-integer or above 2^53-1 are Malformed (Q22, ADR 0003).
- A faulted document (ERR-20): reads work, mutators and `ApplyUpdate` throw `YInternalException`, `TryApplyUpdate` returns `Rejected`. **A subagent chose this; the ADR had left it open.** Confirm it.
- An `OutOfMemoryException` thrown by an observer propagates and is not an observer fault (ERR-19).

## 5. Internal seams the implementation must provide

Declared in the stub as `internal` members (they throw `NotImplementedException`); tests in `YSharp.Tests`, `Fuzz.Tests` and `Benchmarks` reach them through `InternalsVisibleTo`. TestKit and Interop cannot see internals, so they still use reflection (`DocSeams`, `UndoSeams`, a `ScheduleSeam` adapter). Documented in ADR 0015.

- `Doc.DumpStructs()` (ADR 0016 `structs` dump as JSON), `Doc.HasPending`, `Doc.InjectInvariantViolation()` (fault hook for ERR-20).
- `YText.CleanupFormatting()`.
- `ScheduleSeam`: internal static class with `Action<string>? Point` and `Action<string, Func<bool>>? Block`, plain null fields, null-checked on the hot path. The lock reports its transitions through it.
- `Doc.ShouldLoad`, `AutoLoad`, `CollectionId`, `Meta`, `CollectGarbage`, `SignalLoaded`, `SignalSynced`; `UndoManager.Undoing`, `Redoing`, `CurrStackItem`.
- The CC-18 lock-order checker is a Debug-only feature.

## 6. How the suite behaves today, and how to run it

```bash
dotnet build YSharp.slnx                       # expect 0 warnings
dotnet test --solution YSharp.slnx             # Debug
dotnet test --solution YSharp.slnx -c Release  # Release (allocation tests run only here)
cd tools/fixture-gen && npm ci && npm run check   # fixtures and plan goldens must show no drift
```

- Last result: about 8,140 tests, 0 failed, about 360 passing. The rest **skip because the library is still the stub**. Skips go through the shared probe `ApiStub.IsStub` (TestKit). **Once the implementation exists, a stray `NotImplementedException` fails the test instead of skipping**, so every skip is a to-do.
- The progress measure for the implementation is the skip count falling to zero, together with the traceability gate and the budgets.
- Tiers: trait `Tier` = `Gating` (default), `Smoke`, `Nightly`, set by `YSHARP_TIER`. `Needs=Node` tests are never Gating (a test enforces this). Allocation tests run only in Release in a serial collection; TieredCompilation is off for the test projects.
- The traceability gate (`TraceabilityGateTests`) enforces: every active registry entry has tests with `[Covers]`/`[PortOf]`/`[Diverges]` on its declared layers; pending entries have a reason and an owner; the ID lock and `pending-high-water.json` exist and are not exceeded; ledger rows have evidence. It writes a matrix to `TestResults/traceability/`.
- Test names follow `Subject_Condition_Outcome` (ADR 0015). Upstream JS test names live only in `[PortOf]`.

## 7. Pending and unproven

**Registry:** 14 pending entries, all `BUD-01..14` (allocation budgets, owner spec 14). They activate at the milestone gate that records a measured value. Pending is only allowed to shrink (high-water mark: registry 14, ledger 0). Release gate: `YSHARP_RELEASE_GATE=1` requires zero pending.

**Never run against a real implementation (all of it):**
- The ~7,700 skipped tests; their expectations come from the oracle and the research notes.
- Spec 2's last criterion: the end-to-end sim harness (`Init`, `Compare`, `ApplyRandomTests`), which needs `ISimInternals`. TASK-2.2 is still **In Progress** for this reason; the parent TASK-2 is still To Do.
- The gating time budgets (2 minutes) and the randomized budget (about 60 s). The calibration knob is `SimCase.GatingSeeds` in `tests/YSharp.Tests/Simulation/SimCases.cs`.
- The bounded allocation limits and the BUD-12 constants (the tests measure, print the value to record, then skip).
- The first live Node-versus-y-sharp runs. Expect unrecorded divergences that need normalisers (`LenientTruncationNormaliser` is the only one). The differential generators cover text, array and map edits only; XML, nested types, subdocs, `sync.*` and awareness generators are not built (only the `SharpSide.Extensions` hook). The y-sharp side compares `updates`, `state`, `sv`, `doc` and `results`, not yet `events`, `structs` or `ds`.
- The CI workflows have never run on a real runner (actionlint passes). The coverage summary step, the AFL++/SharpFuzz job and the nightly benchmark comparison are untested.
- Two inputs likely to need a fix once there is code: the hand-built V2 negative-clock bytes in `Q22_ANegativeClockFromAV2DiffColumn_IsMalformed`, and the pending-bytes boundary in `LimitsTests`.
- ENC-26/57-59 are excluded and the JS-only `testObfuscateUpdates` ledger row is `skipped` with a reason.

## 8. Practical notes for whoever picks this up

- **Process rules (CLAUDE.md):** use the `backlog` CLI for every task, doc and decision change. Never edit Backlog markdown by hand. Read `backlog instructions task-creation`, `task-execution` and `task-finalization` before the matching action.
- **Git:** `main` is the only branch, all work is local, **nothing is pushed**. Commit trailers end with the `Co-Authored-By` line in use. Keep `.claude/worktrees/` out of commits (it is in `.git/info/exclude` locally); never `git add -A` blindly.
- **Fixtures are generated.** Change the generator, then run `npm run gen` in `tools/fixture-gen` and commit the output; the drift job fails on any diff. The generator needs the submodules checked out.
- **Environment:** Windows, SDK pinned by `global.json` (the machine also has an 11 RC SDK; the pin avoids it). Node major is pinned in `.nvmrc` (24). Git warns that LF becomes CRLF on commit; that is the local autocrlf setting.
- **Useful gotchas learned:** the allocation-tick listener is process-wide, so the allocation tests run in a non-parallel collection; the Yjs `Date.now` capture in lib0 is shimmed in the generator so the virtual clock reaches `UndoManager`; the fixture client reads one JSON line per request, so scripts are compacted before sending.
- Per-spec traceability matrices are in `docs/inventory/matrices/` and show which IDs each spec owns.

## 9. Suggested brief for the planning agent

> Produce an implementation plan for y-sharp as Backlog tasks under a new parent, using the `backlog` CLI. Inputs: ADR 0004, 0005, 0006, 0008, 0009, 0018, 0019; `docs/api-decisions.md`; `docs/inventory/` (registry, ledger); the stub in `src/YSharp`; the research notes. Order by ADR 0018: M0 spike first (with the follow-up storage ADR as its output), then the six milestones. For each milestone list the registry IDs and test files that turn from skipped to passing, the budgets that must be recorded to close it, and the seams it must provide. Schedule the unproven items in section 7 of this handoff at the milestone where they first become measurable. Do not write implementation code. Ask the owner about the faulted-document behaviour and anything the ADRs leave open.
