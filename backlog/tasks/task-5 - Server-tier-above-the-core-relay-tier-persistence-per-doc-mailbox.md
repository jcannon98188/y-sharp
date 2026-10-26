---
id: TASK-5
title: 'Server tier above the core: relay tier, persistence, per-doc mailbox'
status: To Do
assignee: []
created_date: '2026-10-25 17:19'
labels:
  - server
dependencies:
  - TASK-4.36
references:
  - docs/adr/0006-concurrency-contract.md
  - docs/adr/0013-hostile-input-and-fuzz-strategy.md
  - docs/adr/0022-changing-the-suite-during-implementation.md
ordinal: 53000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
y-sharp is a 1:1 Yjs v13 port, pinned to the oracle (ADR 0001, 0003). The goal after parity is a .NET-native CRDT server with JS Yjs clients, which needs layers the port does not have. These layers must sit above the core and must not change integration order, update encodings or the existing suite (ADR 0022). Three gaps were identified in a design review. (1) Relay tier: most server work is receive, persist, fan out, and can run on raw update bytes (merge, diff, state-vector extraction) without building an Item graph or a Doc; materialize a Doc only when content is needed (indexing, server-side edits, validation). (2) Persistence: an append-only update log with periodic compaction by merging updates without loading the doc, stored in the V2 encoding, written durably before fan-out, with an optional ack or sequence number since y-protocols has none. (3) Per-doc mailbox: ADR 0006 gives each Doc one re-entrant lock and runs observers synchronously on the committing thread; a server wants one serialized mailbox per document (Channel-based or actor) so many documents run in parallel, with each broadcast encoded once and shared across sockets. Hostile-input limits (ADR 0005, 0013) and the protocols from M6 (TASK-4.36 sync and auth) are the foundation. Start only after the core reaches parity. Open design questions for the plan: storage abstraction and first backend, whether the relay tier lives in the YSharp assembly or a new project, and how update-level authorization (read-only connections, subtree write limits) is expressed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A relay API merges updates, diffs against a state vector and extracts a state vector from update bytes without constructing a Doc, and its output is byte-identical to the Doc-based path on the existing fixtures and on differential fuzz runs
- [ ] #2 Relay tier enforces YLimits on pending structs, client-id and clock bounds, update size and decompression for untrusted input, with hostile-input tests in the ADR 0013 style
- [ ] #3 A persistence layer appends updates to a durable log, compacts by merging without loading a Doc, stores the compacted form as V2, and recovers a document to a state equal to the pre-compaction state after a crash between append and compaction
- [ ] #4 A per-document mailbox serializes all work on one Doc while different documents run in parallel, and one encoded broadcast is shared across all connected sockets without per-socket re-encoding
- [ ] #5 The server tier lives above the core: no changes to integration, encodings or existing core tests, and the full existing suite passes unchanged
- [ ] #6 New tests cover the relay tier, compaction and recovery, mailbox ordering and isolation, and the hostile-input limits
- [ ] #7 An ADR records the layering decision, including how the mailbox relates to the ADR 0006 document lock
<!-- AC:END -->
