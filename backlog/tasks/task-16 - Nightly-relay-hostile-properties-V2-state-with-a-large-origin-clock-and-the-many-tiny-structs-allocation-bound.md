---
id: TASK-16
title: >-
  Nightly relay hostile properties: V2 state with a large origin clock, and the
  many-tiny-structs allocation bound
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 05:09'
updated_date: '2026-10-27 06:00'
labels:
  - bug
  - relay
  - codec
dependencies: []
priority: high
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The nightly tier on main has failed since 2026-10-25 on four RelayHostileProperties cases. Three (Builder_ClockShapes tiny/not, Builder_CountClaims tiny) are one bug: a V1 update whose origin clock is near 2^53 is accepted, but every V2 encoder (YRelay, Doc, YUpdate.TryConvert) writes that clock into an IntDiffOptRle column as diff*2+flag, up to 2^54-1, and y-sharp's own V2 reader refuses any varint above 2^53-1, so the V2 state is Malformed. Repro: 01010105888080808008ffffffffffffff0f017d5600. The fourth (Builder_ManyTinyStructsClientsAndRanges_AreContained, tiny: False) allocates 1,329,792 bytes against a 1,219,264 bound for a 2,667-byte input.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An update y-sharp accepts encodes to V2 that y-sharp decodes again, including origin clocks up to 2^53-1; decoded clocks above 2^53-1 or below 0 stay Malformed
- [x] #2 Builder_ClockShapes_AreContained and Builder_CountClaims_AreContained pass in the nightly tier
- [x] #3 Builder_ManyTinyStructsClientsAndRanges_AreContained passes in the nightly tier, by a relay fix or a bound justified with evidence
- [x] #4 Gating suite and API baselines unchanged apart from recorded suite changes
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Widen the V2 IntDiffOptRle reader to the writer's range (|diff*2+flag| <= 2^54-1); the decoded clock keeps its 0..2^53-1 check. Unit test: an origin clock of 2^53-1 round-trips V1->V2->V1 (suite commit). 2. Reproduce the Amp over-allocation, find what the fold allocates per held struct or per input byte, fix in the relay or justify the bound. 3. Nightly relay tests, gating suite, API check.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Clock overflow: not relay-specific. Every V2 writer (YRelay, Doc, YUpdate.TryConvert) writes an origin clock into an IntDiffOptRle column as diff*2+flag, up to 2^54-1, but IntDiffOptRleColumnReader read it through TryReadVarInt capped at 2^53-1, so y-sharp could not read its own V2 for an origin clock of 2^52 or more. Fix: the column reader allows encoded magnitudes up to 2^54-1 (Lib0Reader.TryReadVarInt takes a maxMagnitude); the decoded clock keeps its 0..2^53-1 check (Q22). Evidence: pinned Yjs v13.6.33 convertUpdateFormatV1ToV2 writes the same V2 bytes for all four nightly inputs (e.g. 000002010208beffffffffffff3f0001880100000001010101007d3600) and convertUpdateFormatV2ToV1 returns the original V1.

Refold post-condition: a new nightly seed found 0202ffffffff07010a0424010174016b0568656c6c6f02ffffffff07020a0424010174016b0568656c6c6f00, whose relay state refolds to different bytes (info a4 then 84). Yjs mergeUpdates does exactly the same (m1 ...a4..., m2 ...84..., m3 = m2): a sliced item keeps its parentSub flag until it is read back with an origin. y-sharp matches the oracle, so the post-condition was too strong; it now requires the same state vector after one refold and a fixed point by the second (ADR 0022, with evidence).

Owner sign-off (2026-10-27): the many-tiny-structs over-allocation is handled in the relay guard. A fold costs about 620 B per held struct (StructRef is ~170 B, copied through decode, merge and the per-client lists), so two-byte GC/Skip structs cannot fit 64 B per input byte. RelayHostile now allows BytesPerMergedStruct (768) per struct the fold adds to the held state (new internal YRelay.HeldStructs), as it already did per held struct merged again. A compact held form for GC/Skip runs was the alternative, not taken.

Found, out of scope: earlier nightlies (e.g. run 40372636845) also failed on a live differential Mismatch in mutated-updates, unrelated to the relay guard.

Suite changes: MalformedInputTests.Q22_AnOriginClockOf2Pow53Minus1_RoundTripsThroughV2 (new); RelayHostile.ApplyTo allowance per added struct; RelayHostile.AssertPostCondition refold fixed point. Validation: RelayHostileProperties at nightly size (YSHARP_TIER=Nightly) 15/15 passed in 19m 46s; gating suite with YSHARP_RELEASE_GATE=1: 5056 total, 0 failed; api-surface check unchanged. Out of scope: the seed-dependent nightly differential mismatch in mutated-updates.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The V2 IntDiffOptRle column reader now accepts the writer's full range (up to 2^54-1), so origin clocks up to 2^53-1 round-trip through V2 byte-identically with Yjs; decoded clocks outside 0..2^53-1 stay Malformed. The relay guard allows 768 B per struct a fold adds (owner sign-off), and the refold post-condition matches Yjs mergeUpdates (one change, then a fixed point). Verified by the nightly-size relay properties (15/15), the gating suite (5056, 0 failed) and the API baselines.
<!-- SECTION:FINAL_SUMMARY:END -->
