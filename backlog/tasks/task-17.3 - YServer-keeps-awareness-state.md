---
id: TASK-17.3
title: YServer keeps awareness state
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 12:17'
updated_date: '2026-10-27 12:56'
labels:
  - bug
  - server
dependencies: []
parent_task_id: TASK-17
ordinal: 76000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
YServer relays awareness messages but keeps no state, so a connection that joins after a peer set its presence never gets it (y-websocket sends every known state on connect), and when a connection closes without clearing its state (a crashed tab) the others are never told. So presence shows up late (up to the client's renewal interval) and lingers until the outdated timeout after a crash. Owner sign-off 2026-10-27: behave like y-websocket bin/utils.js, re-encoding: the server applies each awareness update to its own Awareness, tracks the client ids each connection controls, and broadcasts its encoding of what changed to every connection, the sender included.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A connection that joins after a peer set its state receives every known awareness state after the sync step 1
- [x] #2 When a connection closes, the states it controlled are removed and the removal is broadcast to the remaining connections
- [x] #3 An awareness update is applied to the server state and the encoding of the changed clients is broadcast to every connection, as y-websocket does
- [x] #4 Read-only connections may still share presence (SRV-17); a malformed awareness message still closes the sender
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DocumentMailbox holds an Awareness of its own (local state null, server TimeProvider) and applies every awareness message to it with the connection as origin; the update handler tracks each connection's clients (added and updated, minus removed: a client that removed its state and set it again stays its connection's, where y-websocket would lose it) and sends the server's encoding of the changed clients to every connection, the sender included. On connect the known states follow Step1. A connection's clients are removed after the message in hand when it closes for any reason, so the removal is never sent from inside another send; outdated-timeout removals come from the awareness timer and are posted to the worker. A null state for a never-seen client changes nothing and is not sent (as y-protocols). Recorded as SRV-22 (ADR 0024, server.yaml, id-lock). Tests: Mailbox/AwarenessTests (12), replacing MailboxTests' relay-as-is test.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YServer keeps each document's presence as y-websocket bin/utils.js does (SRV-22): awareness updates apply to the mailbox's own Awareness and its encoding of the changed clients goes to every connection, a late joiner gets every state after Step1, and a closed connection's states (any close reason) and stale states (30 s outdated timeout) are removed and the removal sent. Tests: Mailbox/AwarenessTests (12). Verified: YSHARP_RELEASE_GATE=1 full suite 5074 total, 0 failed; YSHARP_TIER=Smoke interop 119/120 (replay env-gated); fixture-gen npm run check and the API surface check clean.
<!-- SECTION:FINAL_SUMMARY:END -->
