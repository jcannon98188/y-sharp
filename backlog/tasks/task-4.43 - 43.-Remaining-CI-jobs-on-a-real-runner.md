---
id: TASK-4.43
title: 43. Remaining CI jobs on a real runner
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 17:52'
labels:
  - spec
  - 'model:sonnet'
  - hitl
milestone: m-7
dependencies:
  - TASK-4.7
  - TASK-4.42
parent_task_id: TASK-4
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three CI paths have never run anywhere. Running them needs the pushed repository and secrets or runners only the owner controls.

**Owns:** `.github/workflows/` fixes only
**Handoff section-7 items:** Coverage summary step, AFL++/SharpFuzz job, nightly benchmark comparison: never run.
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Coverage summary, SharpFuzz/AFL++ and nightly benchmark comparison jobs run green on a real runner, fixes committed
- [x] #2 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Run each never-run path on GitHub Actions (gating coverage summary, sharpfuzz, nightly) against impl/task-4.
2. Fix workflow or harness defects until every step is green.
3. Treat findings as real bugs: fix in the library (with owner sign-off for divergences) or, for upstream test defects, record a SIM/ENC entry with fixtures.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Coverage summary (gating): verified on gating run 40326099753 (ubuntu, Debug); the summary step reads three cobertura reports (line rates 42.2%, 34.5%, 95.2%). No workflow change needed.

SharpFuzz/AFL++ (sharpfuzz.yml, tools/sharpfuzz):
- Harness: the post-condition's known-good update is now built through the library (client 0x5eed5eed, root "__post"); the old hand-written one used client 1 and root "t", which collided with hos-ref seeds, so AFL refused four seeds. All 625 seeds replay cleanly.
- Workflow: the instrumented YSharp.dll only runs under afl-fuzz (outside it, an AccessViolationException in Doc..ctor), so the build keeps an uninstrumented copy (fuzz-replay) for seed export, replay and minimise. Findings are copied under artifact-safe names (AFL's ':' and ',' are rejected by upload-artifact) with a replay .txt and a minimised .min.bin, and listed in the step summary.
- First green run 40347705802 found 63 crashes, all "post-condition: the encoded state is rejected": an item of length 0 is stored by Yjs but dropped from the state encode, releasing the structs waiting behind it, so the document's own state cannot be applied again. Fixed as ENC-88 (below). Run 40357358937 after the fix: every step green, no findings.

ENC-88 (library, approved by the owner on 2026-10-26): UpdateReader.ReadStruct rejects an item of length 0 ("" string, Deleted 0, empty JSON/Any list) as Malformed. GC and Skip of length 0 are still accepted. Tests: MalformedInputTests.Enc88_ZeroLengthItem_IsRejected (four content kinds and the fuzz finding). ADR 0003 records the divergence. The same root cause failed nightly run 40343483776 (Differential_MutatedValidUpdates: a 101:1 length-0 item waiting on client 3).

Nightly benchmark comparison (nightly.yml): run 40365311083 green on every step, including Benchmarks, Compare (no stored baseline yet: 13 new, 0 regressions) and the bytes-per-item table; the run stored itself as the baseline. Run 40372636845 dispatched to exercise the comparison against it (checked under TASK-4.44).
Nightly run 40357352611 failed on y-text.tests.js:testRepeatGenerateQuillChanges2 seed 1038235662: two formatting cleanups where upstream asserts zero. Yjs on the same draw (lib/sim.mjs runSim) also finds two, so upstream's assertion fails in Yjs itself: SIM-15 (below).

Suite changes (ADR 0022, separate suite: commits):
- ENC-88: steps-foreign.mjs zeroLengthItem oracle flag; MutationCase.OracleZeroLengthItem, ZeroLengthItemNormaliser (zero-length-item) in Normalisers.Registered, DiffRun reads the flag; ComparatorTests.Normaliser_ZeroLengthItem_ClaimsOnlyAnUnchangedRejection, ReplayAndForeignTests.ApplyForeign_AnItemOfLengthZero_IsFlagged; encoding.mjs cases item-zero-length-string/-deleted/-any (diverge ENC-88, Malformed); hostile-corpus.mjs zero-length-string/-deleted now Malformed and new case zero-length-item-releases-a-waiting-struct (discovered 2026-10-26); registry ENC-88 and id-lock.
- SIM-15: SimChecks.CheckCleanup replaces the zero-cleanup assertion (the cleanup must leave the delta unchanged and a second cleanup must remove nothing); fixture sim/quill-cleanup-1038235662 (struct store against the oracle; its generator fails unless Yjs finds exactly two); SimDivergenceTests.QuillCleanup_Seed1038235662_FindsTheTwoMarkersYjsLeaves_UpstreamAssertedNone; the seed in regression-seeds.json; registry SIM-15 and id-lock.
- The failing-seed repro's replay hint filters on the PortOf trait (the upstream name matched no test method).

Owner sign-off:
- ENC-88: approved by the owner on 2026-10-26.
- SIM-15: the port's cleanup check is weaker than upstream's cleanups === 0 (which fails in Yjs on seed 1038235662); redundant markers y-sharp left where Yjs does not would be caught by the sim fixtures' struct stores, not by this check.

Validation: dotnet build YSharp.slnx 0 warnings (Debug, Release); dotnet test --solution YSharp.slnx 4463 total, 0 failed in Debug and Release; YSHARP_TIER=Nightly YSharp.Fuzz.Tests 163 total, 0 failed; Interop Smoke with Node 117 total, 0 failed; traceability gate passes with no pending entry added; npm run check clean; CI: sharpfuzz 40357358937 and nightly 40365311083 green.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All three never-run CI paths now run green on a real runner: coverage summary (gating 40326099753), SharpFuzz/AFL++ (40357358937, after harness and workflow fixes) and the nightly benchmark comparison (40365311083). Their findings were real: zero-length items (ENC-88, owner-approved library change) and an upstream test assertion Yjs itself fails (SIM-15, suite change). Verified with Debug/Release suites (4463, 0 failed), Nightly fuzz tier, Interop Smoke, fixture check and the traceability gate.
<!-- SECTION:FINAL_SUMMARY:END -->
