# Handoff: the state of y-sharp

Updated 2026-10-27. This note is for whoever picks the project up next, whether a person or an agent. It says what exists, how to work on it, and what is still open. It used to be the handoff from the verification suite to implementation planning. That stage is long finished, and the history is in git and in backlog TASK-1 to TASK-4.

For what the library does and how to use it, read [guide.md](guide.md). For hosting the server, read [server.md](server.md).

## 1. What exists

y-sharp is a pure C# port of Yjs v13 (ADR 0004). The oracle is pinned as git submodules (`yjs/`, `lib0/`, `y-protocols/`): Yjs v13.6.33, lib0 v0.2.99 and y-protocols v1.0.7. Yjs v14 is out of scope.

**The core library is implemented.** All 44 implementation specs (TASK-4, milestones M0 to M7) have landed. Every feature gate is open, and no test is skipped for a missing feature. The **server tier** (TASK-5) sits above the core without changing it (ADR 0024).

| Piece | Where |
|---|---|
| Core library: documents, transactions, V1 and V2 codecs, shared types, events, snapshots, relative positions, undo, subdocs, permanent user data, the awareness, sync and auth protocols, `YRelay`, the update functions | `src/YSharp` (module layout in ADR 0021) |
| Server tier: mailbox, connections, storage interface, in-memory store, validation hooks | `src/YSharp.Server` |
| Storage conformance suite for `IUpdateStore` backends | `src/YSharp.Server.Testing` |
| Unit, port, fixture, allocation and simulation tests | `tests/YSharp.Tests` |
| Server-tier tests: mailbox, validation, lifecycle, storage conformance, crash recovery, end-to-end simulation | `tests/YSharp.Server.Tests` |
| Hostile input, fuzz and the deep-recursion canary | `tests/YSharp.Fuzz.Tests`, `tests/YSharp.Fuzz.Canary`, `tools/sharpfuzz` |
| Live Node-versus-y-sharp differential harness | `tests/YSharp.Interop.Tests` |
| Test helpers: PRNG with lib0 goldens, ops-script interpreter, fixture loader, sim harness, scheduler, traceability | `tests/YSharp.TestKit` |
| Storage-model spike harness (ADR 0023) | `tests/YSharp.StoreHarness`, `spike/` |
| Benchmarks and the Yjs memory baseline | `bench/YSharp.Benchmarks`, `bench/yjs-memory` |
| Fixture generator and about 140 committed fixtures | `tools/fixture-gen`, `fixtures/` |
| Public API baselines, for the core and for the server | `tools/api-surface/` (`check.sh`, `baseline.txt`, `baseline.server.txt`) |
| NuGet packaging and the install smoke test | `src/Directory.Build.props`, `tools/package-smoke`, [releasing.md](releasing.md) |
| Behaviour registry, JS-test ledger, ID lock, pending high-water mark, per-spec matrices | `docs/inventory/` |
| CI workflows | `.github/workflows/` (section 4) |

The API sketch in `docs/prototype/api-sketch` and the research notes in `docs/research/` are historical. They record what was known before the implementation existed.

## 2. Read these first

1. `CLAUDE.md`: the Backlog.md workflow is mandatory. Then `GLOSSARY.md`, for the project's vocabulary: Oracle, Fixture, Divergence, Registry entry, Ops script.
2. [guide.md](guide.md) and [server.md](server.md), for how the library and the server work.
3. The ADRs in `docs/adr/`. The most important are:
   - 0003: match JS by default, and record every divergence;
   - 0005: the error model, limits and faulted documents;
   - 0006: the concurrency contract;
   - 0008: the public API shape;
   - 0009 and 0018: allocation budgets and memory layout;
   - 0010: traceability;
   - 0020: feature gates;
   - 0022: the rules for changing the suite;
   - 0023: the storage model;
   - 0024: the server-tier layering.
4. [api-decisions.md](api-decisions.md): every public API decision, with the owner's sign-offs.
5. The backlog: `backlog task list --plain`, then `backlog task view <id> --plain`. The implementation notes on TASK-4.x, TASK-5.x and TASK-6 to TASK-15 record why things are the way they are.

## 3. Process rules

- **Backlog.** Use the `backlog` CLI for every task change. Never edit the backlog markdown by hand. Read `backlog instructions task-creation`, `task-execution` or `task-finalization` before the matching action.
- **Owner sign-off.** Public API changes, library behaviour changes the suite did not ask for, and suite changes outside ADR 0022's rules all need the owner's approval. Record it in the task notes as "Owner sign-off".
- **Suite changes** follow ADR 0022. They go in their own `suite:` commits, and each task's notes carry a "Suite changes" list.
- **Interop is the requirement.** Bytes must stay identical to Yjs. A deliberate difference is a recorded divergence (`[Diverges]`) with a test that shows the JS behaviour.
- **API surface.** `bash tools/api-surface/check.sh` fails on any unreviewed change to the public API. `--update` rewrites the baselines; review the diff first.
- **Source files** are ASCII with LF line endings and no BOM. Git on this machine warns that LF will become CRLF; that is the local `autocrlf` setting, and the repository stores LF.

## 4. Building, testing and CI

```bash
git clone --recurse-submodules https://github.com/jcannon98188/y-sharp.git
dotnet build YSharp.slnx                                     # 0 warnings expected
dotnet test --solution YSharp.slnx                           # gating tier, Debug
YSHARP_RELEASE_GATE=1 dotnet test --solution YSharp.slnx     # also requires zero pending registry entries
dotnet test --solution YSharp.slnx -c Release                # Release: the allocation tests run only here
cd tools/fixture-gen && npm ci && npm run check              # fixtures and plan goldens show no drift
```

- **Suite size.** The gating suite is about 5,050 tests and all pass. The skipped tests are the nightly tier, or the allocation tests that run only in Release.
- **Tiers** are set with `YSHARP_TIER`: `Gating` (the default), `Smoke` or `Nightly`. Tests that need Node are never in the gating tier. `YSHARP_REPLAY=<path>` re-runs one saved differential failure.
- **Filters.** Run one class or method with `--filter-class` or `--filter-method`.
- **Traceability.** The traceability gate (ADR 0010) runs inside the gating tests and writes a matrix to `TestResults/traceability/`. The registry has 0 pending entries, and pending may only shrink.
- **Allocation tests** run in a serial collection, because the allocation listener is process-wide.

CI on GitHub Actions:

| Workflow | When | What |
|---|---|---|
| `gating` | push to `main`, PRs, and called by `release` | Build and gating tests: Linux Debug (with coverage) and Release, Windows Release, macOS Release |
| `fixture-drift` | PRs | Regenerates every fixture on the pinned Yjs and fails on any byte of drift |
| `smoke-differential` | PRs | Live Node-versus-y-sharp differential runs at Smoke size |
| `nightly` | 03:00 UTC daily, or manual | Slow differential, fuzz, soak, slow memory and benchmark comparison |
| `sharpfuzz` | 03:30 UTC daily, or manual | AFL++ through SharpFuzz |
| `release` | every push, and `v*` tags | Packs the three NuGet packages and smoke-installs them; on a tag, runs `gating` and then publishes ([releasing.md](releasing.md)) |

## 5. Where the work stands

| Tasks | State |
|---|---|
| TASK-1 to TASK-3 | Done: research, the verification suite, implementation planning. The parent tasks' statuses were not closed in the backlog. |
| TASK-4 (M0 to M7, 44 specs) | Done; all subtasks are Done. The parent is still To Do in the backlog. |
| TASK-5 (server tier) | All eight subtasks are Done and merged. The parent's acceptance criteria are left for the owner to check. |
| TASK-6 to TASK-14 | Done: registry rows, an undo ordering detail, zero-allocation budgets, the API baselines, folds and loading that scale with the update rather than the held state, the server's API decisions, and decoding a peer's update once. |
| TASK-16 | Done: the nightly relay hostile-input failures (a V2 clock range and the guard allowance). |
| TASK-15 (NuGet) | Done. Packaging, symbols, Source Link, the release workflow and the docs. Publishing uses nuget.org trusted publishing, so no API key is stored. 0.1.0-preview.1 is published. |
| TASK-17 (host integration defects, for preview.2) | Done: an empty prelim text encodes no item (17.1); a zero-length delete range is Malformed everywhere, as Yjs rejects it (17.2, ENC-51); the server keeps presence as y-websocket does (17.3, SRV-22); `YServerOptions.DocumentUnloading` runs before an idle document is unloaded (17.4, SRV-23). 0.1.0-preview.2 is not yet tagged. |

## 6. Known issues and open items

- **Nightly differential mismatches.** Some nightly runs fail on a live differential mismatch in the mutated-updates property; the failure depends on the seed. The four relay hostile-input failures of 2026-10-25 are fixed (TASK-16).
- **XML documentation.** About 630 public members have no doc comment. The packages ship the comments that exist, and CS1591 is suppressed under `src/`.
- **Parent tasks** TASK-1 to TASK-5 still show To Do in the backlog, although their work is done. Closing them is the owner's call.
- **Divergences and normalisers.** The live differential compares updates, state, state vectors, documents and results. Where y-sharp is deliberately stricter than JS, normalisers in `tests/YSharp.Interop.Tests/Harness/Normalisers.cs` account for it.

## 7. Practical notes

- **Git.** `main` is protected by review: work goes on a branch (`impl/...`) and lands through a pull request to `main`. Commit trailers end with the `Co-Authored-By` line in use. Keep `.claude/worktrees/` out of commits; it is in `.git/info/exclude` locally. Never `git add -A` blindly.
- **Fixtures are generated.** Change the generator, run `npm run gen` in `tools/fixture-gen`, and commit the output. The drift job fails on any diff. The generator needs the submodules checked out.
- **Environment.** The SDK is pinned by `global.json` (10.0.x, Microsoft.Testing.Platform runner). The Node major version is pinned in `.nvmrc` (24).
- **Stray test processes.** A test host left running in the background locks the build output, and the next build fails with MSB3021. End the process and rebuild.
- **Learned gotchas:**
  - the Yjs `Date.now` capture in lib0 is shimmed in the generator, so the virtual clock reaches `UndoManager`;
  - the fixture client reads one JSON line per request, so scripts are compacted before sending;
  - `YValue` object equality is order-sensitive;
  - applying a remote format change makes a Yjs peer delete redundant format items as its own local update. A test that compares peers must let them settle.
