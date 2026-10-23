---
id: TASK-1.18
title: Thread-safety and concurrency contract
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:57'
updated_date: '2026-10-23 20:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.1
  - TASK-1.3
  - TASK-1.12
  - TASK-1.19
parent_task_id: TASK-1
ordinal: 19000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide the concurrency contract for y-sharp: which types may be used from multiple threads, what is serialized (transactions, apply, encode), whether readers can run concurrently with a writer, on which thread observers and events run, how re-entrancy and deadlock are avoided, and how each guarantee is tested (stress tests, race detection, deterministic schedule exploration). Yjs itself is single-threaded, so this is a new contract with no oracle behaviour to match.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Steer from user (not yet resolved): keep oracle semantics unchanged and get thread safety by wrapping them in a lock, Yrs-style (document-level reader/writer lock taken via transactions; exclusive writes; Yrs behaviour to be verified). Remaining to decide in this ticket: lock granularity, read concurrency, try-vs-blocking acquire, observer threading and re-entrancy under the lock, async/cancellation, and the stress/race test design.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the concurrency contract with the user over three grilling rounds. Recorded as ADR docs/adr/0006-concurrency-contract.md: 20 numbered guarantees (CC-1..CC-20) plus test design. Key decisions: one re-entrant per-thread reader/writer lock per Doc (custom, Monitor-based, controllable by schedule testers); shared reads that never mutate; blocking and Try acquire with timeout/cancellation; nested writes join the current transaction as in Yjs; observers synchronous on the committing thread under the write lock; snapshot-per-trigger handler lists (added-mid-dispatch handler is a Divergence, confirm TX-12 on the oracle); sync-only, thread-affine transactions; per-subdoc locks with parent-before-child order and a debug checker; own lock for Awareness with injected TimeProvider; UndoManager/PermanentUserData under the doc lock. Tests: gating stress tier plus nightly soak with replayable seeds, six asserted invariants (serializability by replay is the headline), separate non-gating schedule exploration. GLOSSARY.md gained Transaction, Implicit transaction, Thread-affine, Observer fault. Verified by re-reading the ADR against each agreed answer; no code or tests apply (planning ticket).
<!-- SECTION:FINAL_SUMMARY:END -->
