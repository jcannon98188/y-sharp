---
id: TASK-5.1
title: '1. ADR: server-tier layering and mailbox versus the document lock'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:38'
updated_date: '2026-10-26 20:45'
labels:
  - server
  - hitl
  - 'model:opus'
dependencies:
  - TASK-4.36
references:
  - docs/adr/0006-concurrency-contract.md
  - docs/adr/0021-source-modules-and-ownership.md
parent_task_id: TASK-5
ordinal: 54000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The server tier (TASK-5) must sit above the core without changing it. ADR 0006 gives each Doc one re-entrant lock and runs observers synchronously on the committing thread; a server wants a serialized mailbox per document so many documents run in parallel. Several layout choices block the other TASK-5 subtasks and are the owner's call. **Suggested model:** opus (override freely). Needs owner decisions: assembly placement, storage abstraction shape, and the first backend.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An ADR records whether the server tier lives in the YSharp assembly or a new project (for example YSharp.Server), and the package and versioning consequence
- [x] #2 The ADR states how the per-document mailbox relates to the ADR 0006 lock: who may hold the lock, how observers and broadcast interact with it, and what the mailbox guarantees that the lock does not
- [x] #3 The ADR defines the storage abstraction (append, read since, compact, delete) and names the first backend, with the owner's decision recorded
- [x] #4 The ADR defines two validation tiers: structural validation on every write using the relay tier alone (YLimits, well-formedness), and opt-in semantic validation (authorization, schema) that needs a hot Doc held in the document mailbox
- [x] #5 The ADR defines the validation hook contract: inputs (current Doc, incoming update, connection context), result (accept or reject with reason), that it runs before persist and broadcast because a merged CRDT edit cannot be rolled back, and what happens to a rejected client (drop the update, resync from server state)
- [x] #6 The ADR records that read-only connections are enforced at the protocol layer by dropping update messages with no Doc, and that fine-grained subtree rules require the hot-Doc tier
- [x] #7 The ADR records the hot-Doc lifecycle: when a Doc is materialized, idle eviction, and rehydration from the persisted log
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Read ADR 0006, 0021, the update functions, YSync/YAuth, YLimits and the TASK-5 subtasks.
2. Put the four layout decisions to the owner (assembly, storage API, backend, hook shape).
3. Write ADR 0024 with the owner's answers, as guarantee IDs SRV-1..SRV-21.
4. Check each AC against its ADR section; commit with the TASK-5 backlog files.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner decisions (2026-10-26, via question):
- Assembly: split. Relay type in YSharp/Codecs; mailbox, storage, validation, hot-Doc lifecycle in a new src/YSharp.Server (own package and version).
- Storage API: async per-doc log (AppendAsync/ReadSinceAsync/CompactAsync/DeleteAsync, ValueTask, V2 records, sequence numbers).
- Backend: none built in. Owner: 'Create an interface/callback/whatever that allows the end user to define their storage backend'. ADR SRV-12 records this; y-sharp ships the interface, the in-memory store and a conformance suite.
- Hook: synchronous delegate on the mailbox worker in a read transaction, with a cooperative deadline token; throw or overrun = reject.

AC #3 asks the ADR to name the first backend. The owner's decision is that there is none; SRV-12 records it, which meets the AC's intent (decision recorded).

Consequence for TASK-5.5: its scope ('first concrete backend') no longer matches. It should become the reusable storage conformance suite (crash at each step, partial writes, corrupted tails, concurrent documents) plus the torn-record contract. Not changed yet: awaiting owner OK.

SRV-16 records a limit the AC wording hides: a resync cannot remove an edit a client already integrated, so a rejected client keeps it and resends it; converging needs the client to discard its Doc or the server to close the connection.

Branch impl/task-5.1 was moved onto origin/main (93ac71e) after the owner merged PR #1. The TASK-5 backlog files are committed with this task now that TASK-5 has started.

Owner revised the backend decision (2026-10-26): ship an in-memory store as the default backend, plus the interface for user-provided backends. ADR 0024 SRV-12 updated. TASK-5.5 rescoped to 'default in-memory backend and conformance suite' at the owner's direction.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added ADR 0024 (docs/adr/0024-server-tier-layering.md): relay in YSharp, server tier in a new YSharp.Server package; mailbox versus the ADR 0006 lock (SRV-4..9); async per-doc storage log with no built-in backend and a conformance suite (SRV-10..12); structural and semantic validation tiers, the synchronous hook contract, reject and resync, read-only connections (SRV-13..17); hot-Doc lifecycle (SRV-18..21). Owner decisions recorded 2026-10-26. Verified by checking each AC against its ADR section; no code changed.
<!-- SECTION:FINAL_SUMMARY:END -->
