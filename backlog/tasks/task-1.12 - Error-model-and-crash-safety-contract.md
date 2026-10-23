---
id: TASK-1.12
title: Error model and crash-safety contract
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:29'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.1
  - TASK-1.2
parent_task_id: TASK-1
ordinal: 13000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide the typed error model for malformed input (exceptions vs TryApply/Result), atomicity of failed applies (what 'no partial state' means given pending structs), observer exception handling, limits (size, depth, allocation) and how each is tested.
<!-- SECTION:DESCRIPTION:END -->

## Comments

<!-- COMMENTS:BEGIN -->
author: @claude
created: 2026-10-23 20:29
---
Resolved in grilling session; see ADR 0005 and Final Summary.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the error model and crash-safety contract; recorded in docs/adr/0005-error-model-and-crash-safety.md. Try* returns an ApplyResult struct (status, error code, rejected and observer-fault counts) with a throwing wrapper; sync/awareness readers never throw on bytes. Decoding is strict on EOF but accepts trailing bytes, duplicate clients and unsorted input. Decode of the whole update is atomic (divergence from JS partial apply on truncated delete sets); well-formed but unintegrable structs are quarantined, not rolled back. YLimits options with no allocation sized from unchecked length claims; MaxPendingBytes rejects the new update. No recursion on document depth; only Any depth is capped. Observer exceptions are contained and reported, not rethrown (divergence). Local-API errors replicate defined JS behaviour, separate exception family. OOM never caught; internal invariant breach faults the doc. Tests: three-bucket oracle policy (both-reject, both-accept, y-sharp-stricter), small-stack thread for recursion, timeouts for hangs, GC allocation bounds, before/after snapshots for atomicity, fuzz asserts no faulted doc. Verified by grilling session with the user; no code run.
<!-- SECTION:FINAL_SUMMARY:END -->
