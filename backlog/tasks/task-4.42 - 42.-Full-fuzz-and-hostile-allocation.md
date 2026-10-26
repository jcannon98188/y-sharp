---
id: TASK-4.42
title: 42. Full fuzz and hostile allocation
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 14:44'
labels:
  - spec
  - 'model:sonnet'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 42000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every decoder exists, so the full hostile corpus, canary and SharpFuzz job can run and BUD-12 can be fixed.

**Features:** `Fuzz`
**Owns:** `tests/YSharp.Fuzz.Tests`, `tests/YSharp.Fuzz.Canary`, `tools/sharpfuzz`
**Registry:** `HOS`, remaining `ERR` entries
**Test files:** `YSharp.Fuzz.Tests/*`, `Allocation/HostileAllocationTests.cs`
**Budgets:** BUD-12 (hostile input k*N plus constant): constants recorded, active
**Handoff section-7 items:** Record the BUD-12 constants (the tests currently print the value and skip).
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
- [x] #4 No input in the corpus or generators faults a document; the post-condition holds everywhere
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-4.24 (Text landing ungated these; gated on Fuzz until this spec). Release measurements against the AmplificationTests bounds:
- 5000 clients with one struct each: 4.7 MB, bound 4.24 MB.
- 5000 structs waiting on missing origins: 11.5 MB, bound 3.29 MB. Decode is about 2.45 MB; the pending dry-run path dominates.
- ThousandsOfTinyPendingUpdates, when the dependency arrives: 5.2 MB, bound 4.1 MB.
A StructRef is 152 bytes. The 3000-update merge was cut from 5.17 MB to 1.56 MB in TASK-4.24 (lazy UpdateRefs clients, exact read-order sizing). Corpus_References is also gated here: the hos-ref corpus expects ok or Rejected for parent-is-self and forward-reference-chain-200, where spec 14 decided Malformed (ENC-49); reconcile the corpus.

Landing (TASK-4.42): Feature.Fuzz in Features.Landed; BUD-12 active at k = 64 bytes per input byte plus 1 MiB fixed (AllocationBudgets BUD-12:k / BUD-12:fixed, budgets.yaml).

Library changes to meet BUD-12 (largest measured 56.5 bytes per input byte above the fixed MiB):
- UpdateReader: client and struct lists are pre-sized from the claimed counts only up to 16384 clients / 4096 structs, then grow by doubling capped at the claim (only the last client can overstate its count).
- PendingStructs: per-client lists are exactly sized, Skip-free and shared on Clone (never changed in place); ToUpdate hands them over without copying.
- Doc.PendingLimit.V1Bytes writes the pending structs straight to the encoder instead of building a ref list.
- IntegrateStructs sizes its state map once; ChunkedList.FirstCapacity 16 -> 4 (one list per client).
- Integration rejects an item that would end at clock 2^53 (Resolution.Rejected, with its client's later structs). It still decodes and may wait at 2^53-1 (ENC-36/ENC-71 unchanged); JS integrates it, but its state/delete range would not decode again under Q22. Test: MalformedInputTests.ItemAt2Pow53Minus1_ItsPredecessorKnown_IsRejectedAtIntegration_SoTheStateStillEncodes.

- ENC-87 (found by the Nightly HOS-MUT run, which hit an internal exception in PredictPendingBytes): V2 reads format and embed values as Any, so a BigInt at any depth, a top-level undefined or nesting past MaxAnyDepth cannot be V1 JSON. UpdateReader rejects such a value as Malformed (JsJsonWriter.CanWrite); YText.Format/Insert reject such attribute values with ArgumentException. JS stores the value and then throws on every V1 encode. Tests: MalformedInputTests.Enc87_*, TextTests.FormatValues_ThatV1JsonCannotHold_*. ADR 0003 records both new divergences (2^53 end clock, ENC-87).

Suite changes (ADR 0022, separate suite: commit):
- HostileProperties.OpsScript: the expected doc also applies KnownGood (the post-condition applied it to the receiver); maps compare as key sets, since key order follows delivery order (insertion-ordered like JS Map since 4.40).
- HostileProperties.Builder_CountClaims (HOS-LEN): a rejection is expected only when every over-claim exceeds the input length; a small over-claim on a non-final client reads the next client's bytes as structs, so neither it nor a later claim need run off the end.
- HostileModels case 5 (integer Any content): only a count above 1 is a claim (CountOverride false otherwise).
- Corpus: encoding.mjs delete-set-len-0 (ENC-51) and delete-set-pending-out-of-order (ENC-86) expect ok as recorded divergences; hostile-corpus.mjs parent-is-self and forward-reference-chain-200 expect Malformed (ENC-49, spec 14); new hos-clock case clock-plus-deleted-length-reaches-2-pow-53 (found by HOS-LEN) expects Rejected.
- Registry: new ENC-87 (diverge, ADR 0005, layers unit+fixture) and id-lock; encoding.mjs cases v2-format-value-bigint and v2-format-value-undefined (diverge ENC-87, expect Malformed); hos-mut case v2-quill-overwrite-format-value-v1-cannot-hold (the nightly finding, discovered 2026-10-26, expect Malformed).

Owner sign-off:
- BUD-12 headroom is 16% (56.5 measured against k = 64), under the usual 25%.
- Rejecting an item that would end at clock 2^53 at integration is a divergence from JS (Q22 / ADR 0003 extension).
- ChunkedList.FirstCapacity lowered from 16 to 4 for BUD-12.
- ENC-49 corpus reconciliation (parent-is-self, forward-reference-chain-200 now Malformed; JS throws a TypeError on both).
- ENC-87: rejecting V2 format/embed values that V1 JSON cannot hold, at decode (Malformed) and in the YText API (ArgumentException), where JS accepts them and later fails to encode V1.

Validation: dotnet build YSharp.slnx 0 warnings (Debug, Release); dotnet test --solution YSharp.slnx 4449 total, 0 failed in Debug and Release; YSHARP_TIER=Nightly YSharp.Fuzz.Tests 163 total, 0 failed (includes the HOS-DEEP canary cases); traceability gate passes with no pending entry added; npm run check clean; SharpFuzz harness builds and --export-seeds writes 625 seeds.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Feature.Fuzz landed and BUD-12 is active (64 B per input byte + 1 MiB) after cutting decode and pending-store allocation. The nightly fuzz found two divergences, both now recorded in ADR 0003: an item ending at clock 2^53 is rejected at integration, and V2 format/embed values that V1 JSON cannot hold are Malformed (ENC-87, also rejected by the YText API). Verified: Debug and Release suites green (4449), nightly fuzz tier green (163), gate and fixture drift clean, 0 warnings. Owner sign-offs listed in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
