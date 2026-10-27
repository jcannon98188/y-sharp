---
id: TASK-17.2
title: A zero-length delete range is Malformed
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 12:17'
updated_date: '2026-10-27 12:56'
labels:
  - bug
  - codec
  - security
dependencies: []
parent_task_id: TASK-17
ordinal: 75000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A V1 update `000100010000` (no structs, one delete range for client 0, clock 0, length 0) passes YUpdate.TryValidate; YServer acknowledges it and relays the same bytes, and Yjs 13.6.33 Y.applyUpdate throws Unexpected case on it (UpdateEncoderV2.writeDsLen), so anyone with edit rights can break every browser on a document. ENC-51 (TASK-4.14) made y-sharp ignore such ranges. Owner sign-off 2026-10-27: reverse it, the decoder refuses a zero-length delete range everywhere, as ENC-88 does for zero-length items; Yjs never writes one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every decode path (Doc, YUpdate.TryValidate/TryDecode/TryConvert/merge, YRelay, YServer) reports Malformed for a delete range of length 0, in V1 and V2, and changes nothing
- [x] #2 YServer closes the sender with a protocol error and relays nothing for 000100010000
- [x] #3 ENC-51 is re-recorded (ADR 0003, registry, fixture classification, differential normaliser) as y-sharp being stricter than Yjs
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
UpdateReader.ReadDeleteSet returns Malformed for a range of length 0 (7a038d3), the only update delete-set reader, so Doc, YUpdate (validate, convert, merge, the other functions), YRelay and YServer all reject it atomically. V2 cannot carry such a range (its length column holds length - 1). Snapshots and PermanentUserData entries still read a range of length 0 as Yjs does: they are not updates. ENC-51 re-recorded in ADR 0003 and encoding.yaml (adr 0005), the fixture cases delete-set-len-0* expect Malformed, steps-foreign now flags an accepted update holding a len-0 range and ZeroLengthDeleteRangeNormaliser claims y-sharp's unchanged rejection of it (unknown client: both reject). Tests: Enc51_* (incl. the bytes 000100010000), RelayTests, MailboxTests SRV-8 case, ReplayAndForeignTests, ComparatorTests; interop smoke 119/120 green with the oracle.

Release gate follow-up: a zero-length GC is still accepted (as Yjs accepts it) and is deleted, so the store delete set wrote a run of length 0 and the document's own state was then Malformed to y-sharp (fuzz corpus hos-clock zero-length-gc; HOS-PROTO find 020100000000010000000000 as a sync update). UpdateWriter.WriteStoreDeleteSet now leaves runs of total length 0 out (a zero-length struct inside a longer run still merges as before). Yjs writes the 0-length range there; noted on ENC-51. Regression test MalformedInputTests.Enc51_ADocumentHoldingAZeroLengthGc_EncodesAStateThatApplies fails without the fix. Gate after: YSHARP_RELEASE_GATE=1 5074 total, 0 failed; YSHARP_TIER=Smoke interop 119/120 (replay test env-gated); fixture-gen check and API check clean.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
A delete range of length 0 is Malformed in every update decode path (UpdateReader.ReadDeleteSet), so a y-sharp peer, relay or server no longer accepts and relays an update Yjs cannot re-encode; snapshots and PermanentUserData still accept it. ENC-51 re-recorded across ADR 0003/0012, the registry, fixtures (two new cases), the differential oracle flag and normaliser. A document holding a zero-length GC now leaves the 0-length run out of its own delete set, so its state stays readable. Verified: YSHARP_RELEASE_GATE=1 full suite 5074 total, 0 failed; YSHARP_TIER=Smoke interop 119/120 (replay env-gated); fixture-gen npm run check and the API surface check clean.
<!-- SECTION:FINAL_SUMMARY:END -->
