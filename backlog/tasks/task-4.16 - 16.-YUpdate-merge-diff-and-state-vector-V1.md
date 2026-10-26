---
id: TASK-4.16
title: '16. YUpdate merge, diff and state vector (V1)'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 16:55'
labels:
  - spec
  - 'model:sonnet'
milestone: m-2
dependencies:
  - TASK-4.14
parent_task_id: TASK-4
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Doc-free update functions on V1 bytes, with optional `YLimits` (api-decisions).

**Features:** `UpdateFunctions`
**Owns:** `src/YSharp/Codecs/UpdateFunctions/` (V1)
**Registry:** V1 update-function `ENC` entries
**Test files:** `Codecs/UpdateFunctionTests.cs` (V1), `YSharp.Fuzz.Tests/Tests/AmplificationTests.cs` (V1)
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
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
Implementation: Codecs/UpdateFunctions/YUpdate.cs (TryMerge, TryDiff, TryStateVectorFromUpdate), TryValidate in YUpdate.Convert.cs (TryConvert stays for spec 19). Function decoding: exact-Skip rule (ENC-85), structs in read order including repeated clients; merge = UpdateMerge.Merge + merged delete sets; diff = UpdateMerge.Diff + delete set grouped per client in read order; single-input merge returns the input unchanged (Yjs). Apply decoding now joins adjacent GC/Skip structs into one run (HOS-AMP allocation bound). V2 hooks stubbed in Codecs/V2/UpdateCodecV2.cs.

Verification: 0 warnings; Debug 8747 total, 0 failed, 1141 passed; Release 0 failed, 1152 passed; drift clean; api surface unchanged. UpdateFunctionFixtureTests: all 14 V1 merge/diff fixture steps match the oracle byte for byte.

Suite changes:
- Free (standing approval): AmplificationTests.ManyOneClockStructs_100k -> [Requires(Text)] (allocation bound now holds; the post-condition calls GetText).
- Free: new UpdateFunctionFixtureTests (fixture steps and pinned-Yjs state vectors).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Document-free V1 update functions landed (merge, diff, state vector from update, validate) with strict decoding and limits. Verified: 0 warnings, Debug and Release green, drift clean, every V1 merge/diff fixture step reproduces the oracle bytes.
<!-- SECTION:FINAL_SUMMARY:END -->
