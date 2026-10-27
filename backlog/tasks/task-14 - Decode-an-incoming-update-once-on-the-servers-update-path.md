---
id: TASK-14
title: Decode an incoming update once on the server's update path
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-26 23:57'
updated_date: '2026-10-27 01:05'
labels:
  - server
  - performance
  - api
dependencies: []
ordinal: 70000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the performance audit after PR #2: DocumentMailbox.AcceptAsync decodes each peer update up to four times: YUpdate.TryConvert (to V2 for the store), the validation hook's DecodedUpdate, YRelay.TryPrepareUpdate (decodes the V2 again) and the hot Doc's TryApplyUpdate. EditAsync does the same for server edits. Decode once, then write the V2 record, prepare the relay and apply to the Doc from the decoded form. YSharp.Server reaches the core only through public API (ADR 0024), so this needs public overloads that take an already-decoded update (for example on DecodedUpdate from YUpdate.TryDecode). That is an API decision for the owner (docs/api-decisions.md). Nothing a peer sees changes: the bytes stored and broadcast stay the same.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An accepted peer update is decoded once on the server's path, a server edit at most once after it is captured
- [x] #2 Stored records and broadcast bytes are unchanged on the existing server and simulation tests
- [ ] #3 Any new public members are recorded in the API baseline and approved by the owner
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented in 03f05da (src), dce3c20 (suite).

Decisions:
- DecodedUpdate keeps the functions decode (structs in read order, client blocks, deletes, decode limits, length); Structs/Deletes views are built lazily. Doc.TryApplyUpdate(DecodedUpdate) rebuilds the Doc decode's per-client lists (a repeated client's later block replaces the earlier, adjacent GC/Skip joined), so it gives what the bytes give.
- AcceptAsync decodes V1 once under PeerLimits (server limits with the relay's string/nesting bounds where tighter); EditAsync once under the relay's limits.
- Found by the HOS-MUT mailbox fuzz property: delete ranges that go back within a client do not survive V2 (V2 writes a clock as a step from the previous range's end; a negative step is written as JS writes it). Before, the relay read the stored V2 bytes and agreed with the store while the hot Doc read the V1. Now, when DocumentMailbox.SurvivesV2 says no, the relay and the hot Doc take a decode of the stored V2 bytes: a second decode, for that hostile shape only. Peers that received the V1 broadcast still hold the V1 reading, as with any V2 store in Yjs.
- ServerSimulationTests: flaked once at seed 12 (hooked) under a loaded full run, not in 20 isolated runs; most likely the 1 s hook deadline. The simulation now uses an infinite deadline and asserts no peer was denied.

Suite changes (ADR 0022): new DecodedUpdateTests (encode = TryConvert, apply = bytes apply incl. pending/out of order, prepare = bytes prepare, over every V1 fixture merge/diff input plus hand-made shapes and edits; looser decode limits refused); MailboxTests.AnUpdateWhoseDeletesDoNotSurviveV2_IsServedAsStored_BeforeAndAfterARestart (fails without the fallback); ServerSimulationTests deadline and no-denial assertion.

Validation: full suite 5046 total, 0 failed (twice); Smoke interop 118 passed, 1 skipped; Release allocation 710, 0 failed; API baseline updated (three new core members).

Owner sign-off: AC3 waits on docs/api-decisions.md E6 (keep the three overloads, recommended). Left In Progress until then.
<!-- SECTION:NOTES:END -->
