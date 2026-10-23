# Schedule exploration: Coyote versus a hand-rolled scheduler

Research for TASK-1.22 (map: TASK-1). Feeds ADR 0006 (`docs/adr/0006-concurrency-contract.md`, "Systematic exploration" under Test design). Date of research: 2026-10-23. Nothing was installed or run; every Coyote claim below comes from its repository, its docs, NuGet metadata or its issue tracker, read on that date.

## TL;DR

- Recommendation: build a small hand-rolled, seeded, cooperative scheduler. Do not take a Coyote dependency now.
- Coyote's last release is 1.7.11 (NuGet, published March 2024). It targets net8.0/netstandard2.0, its CLI and test host run on .NET 8, and net10.0 support exists only as an open, unmerged PR (#524, updated 2026-10-23).
- Coyote does control plain `Thread`, `Monitor` and `Interlocked`, which matches CC-5. It does not control `ReaderWriterLockSlim` (treated as uncontrolled). The ADR already avoids that type, so this is not the blocker.
- xUnit v3 / Microsoft.Testing.Platform is not documented or tested by Coyote at all. Its xUnit story is the v2 `TestingEngine` pattern, plus a binary rewrite of the test and library DLLs as a post-build step.
- A hand-rolled scheduler for the ADR's scope (two to three threads, a few operations, re-entrancy, ABBA, subscribe-vs-commit) is small, but it forces a seam in the lock and the observer list. That seam is the real cost.

## 1. Coyote: current state

| Fact | Value | Source |
|---|---|---|
| Latest NuGet version | `Microsoft.Coyote` / `Microsoft.Coyote.Test` 1.7.11 (list ends at 1.7.11; published 2024-03-18 for 1.7.10, 1.7.11 later in 2024) | NuGet flat container index and registration for `microsoft.coyote.test` |
| Version in repo | `VersionPrefix` 1.7.11 | `Common/version.props` on `main` |
| Package target frameworks | `.NETCoreApp3.1`, `net6.0`, `net8.0`, `.NETStandard2.0` dependency groups | `microsoft.coyote.nuspec` 1.7.11 |
| No net9.0 / net10.0 target | Build targets `net8.0` plus `netstandard2.0`, `net6.0` conditional | `Common/build.props` on `main` |
| Last substantive upstream commit | 2024-12-11 (Akash Lal), then a gap to 2026-08/10 housekeeping (CI actions, Dependabot, one constructor fix #529 merged 2026-10-23) | `gh api repos/microsoft/coyote/commits` |
| Not archived | `archived: false`; pushed 2026-10-23 | GitHub repo API |
| Release request | "Release 1.7.12/1.8.0": 1.7.11 references vulnerable dependency versions. Open since 2025-04-25 | issue #518 |
| .NET 9 / 10 request | Open since 2025-08-18. A user comments "The project looks like abandoned since 2024." One commenter forked it as a separate library (`InterleaveX`; not evaluated here, third-party) | issue #521 |

### 1.1 net10.0 and the .NET 10 runtime

The strongest primary source is the assessment file attached to the open PR (`NET10-COMPATIBILITY-ASSESSMENT.md` on PR #524, dated 2026-08-20, probe on SDK 10.0.303 / runtime 10.0.11):

- A `net10.0` project can reference the existing net8.0 Coyote binaries.
- The rewriter (Mono.Cecil 0.11.4) parsed and rewrote a C# 14 `net10.0` assembly with async state machines and `lock`.
- The normal `coyote test` host is a net8.0 process and fails to load a `net10.0` test assembly ("Could not load file or assembly 'System.Runtime, Version=10.0.0.0'").
- A manual override works: `dotnet exec --fx-version 10.0.11 --roll-forward LatestMajor coyote.dll test ...`. The assessment calls this a constrained workaround, not declared support.
- Real gaps on .NET 10: C# 14 picks `Task.WhenAll(ReadOnlySpan<Task>)`, which Coyote does not wrap (reported as an uncontrolled invocation); `System.Threading.Lock` is not replaced and is not even reported as uncontrolled. The assessment's own decision is "use against net10.0 only as a constrained workaround, not as declared/sound .NET 10 support."

PR #524 "Add .NET 10 compatibility support" (author frankshearar, opened 2026-08-24, still OPEN on 2026-10-23, 82 files) adds a native `Lock` wrapper, atomics and `WaitAsync`/`WhenAll` fixes. A Coyote maintainer (akashlal) is reviewing it and asked for further API coverage (small-integer and generic `Interlocked`, `Volatile` barriers, named wait handles); the author reports those addressed and a later merge-conflict fix. Merging and a NuGet release are both still ahead, with no date.

SDKs on this machine (`dotnet --list-sdks`): 10.0.303, 10.0.400, and 11.0.100-rc.2.26504.105 (all in `C:\Program Files\dotnet\sdk`). Net10.0 builds are available; the Coyote CLI would need the roll-forward workaround above, or the unreleased PR.

### 1.2 Plain threads, Monitor, ReaderWriterLockSlim

From the rewriting source on `main`:

- Controlled (have rewriting wrappers under `Source/Test/Rewriting/Types/Threading/`): `Monitor` (Enter, TryEnter, Exit, Wait, Pulse, PulseAll, IsEntered), `Thread` (Create, Start, Sleep, Yield, Join), `Interlocked`, `Volatile`, `SpinWait`, `SemaphoreSlim`, `AutoResetEvent`, `ManualResetEvent`, `EventWaitHandle`, `WaitHandle`. The release notes confirm `Thread` control in v1.7.5 and memory-access race checking in v1.7.8 (`History.md`).
- Not controlled: `UncontrolledInvocationRewritingPass.cs` lists `ReaderWriterLock`, `ReaderWriterLockSlim`, `Mutex`, `Semaphore`, `SpinLock`, `ManualResetEventSlim`, `Timer`, `SynchronizationContext`, `ExecutionContext` as uncontrolled: calls pass through and may block a thread Coyote believes is running.
- The docs say Coyote "is focused on asynchronous task-based concurrency" and that lower-level threading is not fully supported (`docs/concepts/binary-rewriting.md`).
- So it is not Task-only: ADR CC-5 (lock built from `Monitor`, `Interlocked` and explicit state) lands inside the controlled set. A lock using `ReaderWriterLockSlim` or `System.Threading.Lock` would not.
- Default scheduling points are at synchronization calls, not at ordinary memory accesses. Fine-grained race checking is opt-in (`WithMemoryAccessRaceCheckingEnabled`, since v1.7.8; maintainer explanation in issue #484) and enlarges the schedule space.
- Open issues of note for a library of this kind: #488 (deadlock if an interleaving point sits inside a static constructor), #506 (rewriter NRE on a large real library), #433 (`InvalidProgramException` after rewriting a library), #491 (type load failures).

### 1.3 xUnit v3 and Microsoft.Testing.Platform

- Coyote's documented xUnit integration (`docs/how-to/unit-testing.md`) is: reference `xunit` 2.4.x, call `coyote rewrite` as a post-build step, then run `TestingEngine.Create(config, testMethod).Run()` from a `[Fact]`. `Microsoft.Coyote.Test` depends on `xunit.abstractions` 2.0.3 (nuspec), i.e. the v2 world.
- There is no mention of xUnit v3 or Microsoft.Testing.Platform anywhere in the docs, and GitHub searches of the Coyote issues for "xunit v3" and "Testing.Platform" return nothing. xUnit v3 test projects are executables on Microsoft.Testing.Platform (`xunit.v3` 4.0.2 depends on `xunit.v3.core.mtp-v2`; Microsoft.Testing.Platform 2.5.1 is current on NuGet).
- Mechanically, the `TestingEngine` pattern is just a library call from inside a test, so it plausibly works under any runner, and the rewriter handles `.dll`/`.exe`. But this is an inference, not documented support, and rewriting an xUnit v3 test host assembly together with the library has not been shown by any source I found. Treat as unverified; it would need a spike.
- The rewrite is an extra build step that edits the output assemblies in place (or to an output folder), so the tests must run against rewritten copies. That complicates the repo's single `dotnet test` flow and CI.

### 1.4 Binary rewriting constraints for a pure C# library

- The library DLL and the test DLL must both be rewritten; third-party DLLs are left as pass-through ("partially-controlled exploration", `docs/concepts/binary-rewriting.md`).
- Rewriting is exact-signature matching of emitted types and calls, so new BCL overloads and new language lowering can silently escape control (shown by the .NET 10 assessment above).
- Mono.Cecil 0.11.4 is the IL reader; novel metadata shapes are a risk, and several open issues report rewriter crashes on real libraries (#433, #506).
- Strengths: no source changes in the library, exploration of `Monitor`/`Thread` code is automatic, systematic strategies (random, PCT, fair PCT) and a replayable schedule trace come for free, and a lock-and-thread design like CC-5 fits its model.

## 2. What a hand-rolled scheduler costs and covers

Design (the approach the task named): cooperative interleaving via injected yield points, seeded.

- Test threads are real `Thread`s, but a scheduler hands out a baton so exactly one runs at a time. Each controlled thread parks on its own gate when it is not chosen.
- The library gets an internal seam with two operations: `Point(id)` (a yield point; the scheduler may switch) and `Block(condition)` (the thread cannot proceed until a predicate over lock state holds; scheduler marks it blocked and picks another). In production both compile to nothing (a `static readonly` hook that is null, or a `[Conditional]` attribute), keeping CC-7's zero-allocation and hot-path goals.
- The lock is written against that seam: every transition (read-acquire, write-acquire, owner check, queued-writer check, release, upgrade rejection) calls `Point`, and every wait is `Block` rather than a raw `Monitor.Wait`. Observer-list snapshotting (CC-12/13) and the lock-order checker (CC-18) get points too.
- A seeded `Random` (or PCT: random priorities with a few priority change points) picks the next runnable thread at each point. The log of choices is the schedule; replaying it from the seed reproduces a failure, matching the ADR's "every failure prints the seed and operation log" rule.
- Deadlock detection falls out: if every thread is blocked, report the stack of waits (this directly serves invariant 4 and the ABBA case in CC-18).
- Small scenarios can also be enumerated exhaustively (depth-first over choices) when there are two threads and a handful of points, which random testing cannot guarantee.

Cost estimate (mine, not from a source): a scheduler of roughly 300 to 600 lines of test-support code, plus a seam in the production lock and observer code (a handful of call sites, one per state transition), plus a one-time effort to make the lock's waiting go through `Block`. Ongoing cost is that each new lock path needs a point added by hand.

What it covers: exactly the interleavings of the instrumented points, deterministically, with a replayable seed on any machine, under plain `dotnet test`, with xUnit v3 / MTP unaffected (it is just test code), on net10.0 and the 11.0 RC SDK without waiting on anyone.

What it does not cover: uninstrumented code (a forgotten point is an unexplored interleaving), weak-memory effects and reordering (neither does Coyote by default), and code that blocks outside the seam (e.g. a callback calling `Thread.Sleep` or a BCL lock). It also cannot explore the final real-thread lock implementation itself unless the shipped lock is the same code with the hook compiled in; the stress tier in the ADR still covers real hardware ordering.

## 3. Comparison

| Concern | Coyote 1.7.11 | Hand-rolled |
|---|---|---|
| net10.0 / .NET 10 runtime | Not supported by a release; CLI host is net8.0; workaround via `dotnet exec --roll-forward`; native support is an unmerged PR | No dependency |
| xUnit v3 / MTP | Undocumented, unverified; needs post-build rewrite of test assemblies | Plain test code |
| Plain threads, `Monitor`, `Interlocked` | Controlled | Controlled by construction (via seam) |
| `ReaderWriterLockSlim`, `System.Threading.Lock` | Uncontrolled / not wrapped on 1.7.11 | N/A (lock is ours) |
| Effort to adopt | Rewrite step in build and CI, tool pinning, version risk | Seam plus scheduler |
| Coverage of un-annotated code | Automatic at sync calls | Only at injected points |
| Search strategies, trace replay, coverage reports | Built in | Seeded random plus replay; PCT and DFS optional extras |
| Maintenance risk | Low upstream activity, vulnerable dependency note (#518), single-maintainer fork exists | Ours |
| Fits non-gating job in ADR | Yes in principle | Yes |

## 4. Recommendation and caveats

Recommendation: hand-roll. The ADR's scenarios are small (two threads, few operations) and CC-5 already forces the lock to be built from primitives we control, so the marginal cost of a seam is low and the dependency risk of Coyote on net10.0 and xUnit v3 is high and currently unfalsifiable without a spike. Record the choice in ADR 0006's "Systematic exploration" bullet.

Design implications to carry into the lock design (TASK-1.14/1.15):

- Put the seam in from the start; retrofitting yield points later is how interleavings get missed. Keep it zero-cost in release.
- Never wait with raw `Monitor.Wait`/`Thread.Sleep` inside the lock or observer dispatch; route through `Block`.
- Make `TimeProvider` (CC-19) and the lock-order checker (CC-18) usable under the scheduler, so awareness-timer and ABBA scenarios run deterministically.
- Keep the scheduler deterministic from `(seed, scenario)` and print both on failure.

Caveats:

1. Hand-rolled coverage is bounded by the points we place; a missed point is silent. Mitigation: a test that fails the build if a lock state field is written without a preceding point (reviewable convention), plus the real-thread stress tier.
2. This conclusion rests on Coyote's published state on 2026-10-23. If PR #524 merges and a release ships with a net10.0 host, re-evaluate; the ADR scenarios would then be a cheap Coyote experiment (the lock already uses only `Monitor` and `Interlocked`).
3. Unverified: whether Coyote's rewriter works on an xUnit v3 test host assembly under Microsoft.Testing.Platform. I did not spike it; no source states either way.
4. Cost figures for the hand-rolled path are my estimates, not measured.
5. The `InterleaveX` fork mentioned in issue #521 was not evaluated (third-party, not a primary source for Coyote itself).

## Sources

- Coyote repository: https://github.com/microsoft/coyote (`Common/version.props`, `Common/build.props`, `History.md`, `Source/Test/Rewriting/Types/Threading/*.cs`, `Source/Test/Rewriting/Passes/Rewriting/UncontrolledInvocationRewritingPass.cs`, `docs/concepts/binary-rewriting.md`, `docs/how-to/unit-testing.md`)
- Issues #433, #484, #488, #491, #506, #517, #518, #521; PR #524 (including `NET10-COMPATIBILITY-ASSESSMENT.md` on head commit `bdfe9c83`), PR #529
- NuGet: https://www.nuget.org/packages/Microsoft.Coyote, `microsoft.coyote.test` and `xunit.v3` flat-container metadata, `xunit.v3.core` 4.0.2 nuspec
- Local: `dotnet --list-sdks`; ADR 0006; `docs/research/yrs-concurrency.md`
