---
id: TASK-4.18
title: 18. V2 codec
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 17:37'
labels:
  - spec
  - 'model:sonnet'
milestone: m-3
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 18000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The V2 encoder and decoder adapters behind the codec seam, the `UpdateV2` event, and limits on every V2 column (ADR 0005).

**Features:** `V2`
**Owns:** `src/YSharp/Codecs/V2/`
**Registry:** V2 `ENC` entries, V2 decode `ERR` and `HOS` entries
**Test files:** V2 cases of `Codecs/MalformedInputTests.cs` and the structure files, `YSharp.Fuzz.Tests` V2 corpus
**Handoff section-7 items:** Fix (with evidence, ADR 0022) the hand-built V2 negative-clock bytes in `Q22_ANegativeClockFromAV2DiffColumn_IsMalformed`; the expected Malformed outcome stays.
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Port the lib0 V2 column codecs (Rle, UintOptRle, IntDiffOptRle, String) byte-exact, with strict readers.
2. UpdateDecoderV2 / UpdateEncoderV2 behind the IUpdateDecoder / IUpdateEncoder seam; wire UpdateCodecV2 Decode, EncodeState, EncodeDeleteSet, EncodeFunctionOutput.
3. Land Feature.V2; gate tests that fail only on later-spec APIs (standing approval); verify Q22 bytes.
4. Debug + Release suite, 0-warning build, fixture drift, API surface.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (5252623):
- src/YSharp/Codecs/V2/: Columns.cs (column readers/writers), UpdateDecoderV2.cs, UpdateEncoderV2.cs; UpdateCodecV2.cs rewritten from stubs.
- Seam change outside V2/ (additive, internal): IUpdateDecoder.Remaining became CanHold(count, CountClaim), and UpdateReader's five count checks call it. V1 keeps its old bounds (clients <= remaining/3, Any/JSON <= remaining, others <= remaining/2); V2 bounds counts by the rest buffer, JSON elements by the string column, and struct counts by MaxStructsPerUpdate only, because a V2 struct can cost zero bytes when every column repeats.
- Strict V2 decode: a column longer than the input (ENC-56), an empty Rle/UintOptRle/IntDiffOptRle column (ENC-64..66), a string length past the joined string (ENC-68), a negative key clock, or a clock outside 0..2^53-1 (Q22) is Malformed. Each string is capped at MaxStringBytes (LimitExceeded).
- The writers match lib0: Rle never writes the final count; a UintOptRle run is written as -value (including -0); the IntDiffOptRle flush writes diff*2+hasCount. A negative DS clock diff (an unsorted delete set passed through) writes one byte, diff & 0x7f, as JS writeVarUint does.
- The Update/UpdateV2 events are declared but still not raised: the transaction-update encoder is spec 27's handoff, and no test that requires only landed features exercises them.

Q22 handoff item: no change needed. In Q22_ANegativeClockFromAV2DiffColumn_IsMalformed, left-clock byte 0x42 is lib0 varint -2: diff = floor(-2/2) = -1 with no count, so clock = -1. The paired case with 0x00 is accepted, so the rejection comes from the clock alone. The test passes unchanged in Debug and Release.

ENC-67 (OPEN, owner decision): the registry says diverge ('an exhausted Rle column is a decode error'). That would reject valid Yjs output. lib0 RleEncoder never writes the last run's count, so the decoder must repeat the final run. Evidence: a 3-entry map doc encoded as V2 by our byte-exact encoder has the info column 01 28 (one value, no count). With a temporarily strict reader, TryApplyUpdate of that V2 returns Malformed; with the repeating reader it round-trips. A strict reader also passes the whole current suite, because every V2 oracle fixture with repeated final runs is gated on Text/Map/etc. Enc67_ARleColumnRunningOut_DoesNotRepeatItsLastValue passes only because struct 2 then fails on the empty string column, not on Rle exhaustion. Implemented: repeat, as JS does. Proposed: change ENC-67 to kind match and rewrite the Enc67 test to assert that the final run repeats (suite change, ADR 0022).

Suite changes (ADR 0022):
- 4c82c55 suite: [Requires(Feature.Text)] added to UpdateLayoutTests.Enc10_WorkedExample_TextHi_IsTheDocumentedV1AndV2Bytes, Enc13_DiffInsideAStruct_RewritesOriginAndKeepsTheTail, Enc16_DeleteSet_IsSortedRangesPerClient_DeltaCodedInV2, and HotPathBudgetTests.TryApplyUpdate_HotPath_StaysWithinItsBudget (all build docs with Doc.GetText); [Requires(Feature.V2Functions)] added to UpdateFunctionTests.Enc25_Convert_GivesTheNativeBytesOfTheOtherFormat (calls YUpdate.TryConvert, spec 19). Standing gate approval: each failed only with NotImplementedException from those later-spec APIs.

Verification: build 0 warnings; Debug 8748 total / 0 failed / 1156 passed; Release 0 failed / 1168 passed; npm run check clean (141 fixtures, plan goldens); api-surface unchanged (733 lines).

ENC-67 RESOLVED (owner approved 2026-10-25): registry kind changed to match; the last Rle run repeats, as in JS.
- b8262d2 suite: docs/inventory/encoding.yaml ENC-67 kind diverge -> match (adr 0005 reference dropped, note rewritten). MalformedInputTests.Enc67_ARleColumnRunningOut_DoesNotRepeatItsLastValue replaced by Enc67_ARleColumnsLastRun_RepeatsForEveryLaterRead. It decodes two root strings from one info value and one parentInfo value, and compares the result with the V1 form of the same structs. Checked against a temporarily strict Rle reader: the test fails (Malformed), so it now exercises the repeat. The old test passed only because of an empty string column.
- docs/inventory/matrices/spec-5-traceability.md is a hand-off snapshot and is left unchanged.

Merged origin/impl/task-4 (CI coverage fixes 1cd31e3, 4de543e) before final verification: build 0 warnings; Debug 8748 / 0 failed / 1156 passed; Release 0 failed / 1168 passed; npm run check clean; api-surface unchanged.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed the V2 codec: lib0 column codecs (Rle, UintOptRle, IntDiffOptRle, String) ported byte-exact, plus UpdateDecoderV2 and UpdateEncoderV2 behind the codec seam, with V2 encoding of documents, delete sets and update-function output. Decoding is strict (ADR 0005), except that the last Rle run repeats as in JS (ENC-67, now kind match by owner decision). The decoder seam's Remaining became CanHold(count, claim). Feature.V2 is landed. Five tests gated on later features under the standing approval. The Q22 bytes were correct as written. Verified: 0-warning build; Debug and Release suites green; fixture drift and API surface clean.
<!-- SECTION:FINAL_SUMMARY:END -->
