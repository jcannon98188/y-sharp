---
id: TASK-5.6
title: 6. Per-document mailbox and shared broadcast
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:39'
updated_date: '2026-10-26 21:30'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.1
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 59000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Work on one document must be serialized while different documents run in parallel, and one accepted update should be encoded once and shared across all connected peers rather than re-encoded per socket. Follows the layering in the TASK-5.1 ADR. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A per-document mailbox serializes apply, persist and broadcast for one document, and documents do not block each other
- [x] #2 An accepted update is encoded once and the same buffer is handed to every subscriber
- [x] #3 A slow or disconnected subscriber cannot block the mailbox: backpressure policy is defined and tested
- [x] #4 Tests show ordering per document, isolation between documents, and cancellation and shutdown without losing accepted updates
- [x] #5 A registered validation hook (TASK-5.1 ADR) runs inside the mailbox before an update is persisted or broadcast; a rejected update is dropped, never persisted or broadcast, and the sender is resynced
- [x] #6 Documents with no hook registered use the structural tier only and never materialize a Doc
- [x] #7 Tests cover accept, reject, a hook that throws or times out (treated as reject), and a read-only connection whose updates are dropped without a Doc
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Core (SRV-2): Doc-free YSync writers (WriteStep1 from state vector bytes, WriteStep2 from update bytes), YSync.TryReadFrame, and YUpdate.TryDecode with a public read-only DecodedUpdate view for the hook.
2. YSharp.Server: YServer (one DocumentMailbox per open document, shutdown drains), YConnection (bounded outgoing queue, close reasons), ConnectionContext, ValidationHook/ValidationRequest/ValidationResult, new YServerOptions knobs.
3. Mailbox worker: rehydrate relay (and hot Doc when hooked) from storage, then handle Connect / Message / Disconnect in order; SRV-8 pipeline; one shared broadcast buffer; slow peers closed; resync on reject.
4. Tests: simulated y-websocket peers over YConnection; ordering, isolation, shared buffer, backpressure, cancellation, shutdown, store failure, protocol errors, hook accept/reject/throw/timeout/write, read-only, no-Doc, rehydration, SRV-21.
5. Inventory SRV-2,4,7,8,9,13..19,21; gap ticket for SRV-5/18/20 leftovers.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented the per-document mailbox (ADR 0024 SRV-4..SRV-21 except SRV-5/18 server edits and SRV-20 eviction, filed as TASK-5.8).

Core (SRV-2): YSync.WriteStep1/WriteStep2 over raw bytes, YSync.TryReadFrame, YUpdate.TryDecode with a public DecodedUpdate view (UpdateStruct, UpdateDeleteRange) for validation hooks.
Server: YServer (one DocumentMailbox per open doc, shutdown drains accepted updates then closes connections), YConnection (bounded outgoing queue; a full queue closes the peer with SlowSubscriber), ConnectionContext, ValidationHook/Request/Result, YServerOptions (Validation, ValidationDeadline, MaxRejectsBeforeClose, MailboxCapacity, SubscriberQueueCapacity).
Pipeline per SRV-8: structural V1->V2 under Limits, hook in a read transaction with a deadline (throw/timeout/write attempt = reject), append to the store, relay apply, hot Doc apply, one shared broadcast buffer.

Owner flag (ADR clarified in place, SRV-16): a rejected Update gets permission-denied + Step1 + Step2; a rejected Step2 gets permission-denied + Step2 only. A client answers every Step1 with its full diff, so a Step1 after a rejected Step2 would loop forever. MaxRejectsBeforeClose (default 3) closes a peer that keeps sending rejected edits.

Gap ticket: TASK-5.8 (server-side edits and reads on the hot Doc, idle eviction).

Suite changes:
- tests/YSharp.Tests/Protocols/ServerFacingApiTests.cs (new, SRV-2)
- tests/YSharp.Server.Tests/Mailbox/Peer.cs, GatedStore.cs, MailboxTests.cs, ValidationTests.cs (new)
- docs/inventory/server.yaml + id-lock.txt: SRV-2,4,7,8,9,13..19,21

Full suite with YSHARP_RELEASE_GATE=1: 4630 total, 0 failed, 776 skipped.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Per-document mailbox server (YServer/YConnection/DocumentMailbox) with ordering, isolation, shared broadcast buffers, slow-subscriber backpressure, draining shutdown, and a deadline-bound validation hook over a read-only hot Doc; documents without a hook stay Doc-free. Doc-free sync writers and a public decoded-update view added to core. Rejected Step2 is resynced without a Step1 to avoid a ping-pong loop (ADR 0024 SRV-16 clarified). Follow-up: TASK-5.8.
<!-- SECTION:FINAL_SUMMARY:END -->
