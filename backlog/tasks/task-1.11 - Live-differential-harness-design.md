---
id: TASK-1.11
title: Live differential harness design
status: Done
assignee:
  - '@jason'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:10'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.10
parent_task_id: TASK-1
ordinal: 12000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide how xUnit drives Node (process model, IPC protocol, timeouts, failure reporting), what is compared (bytes, state, convergence both directions), fuzz generation, and how it runs as a slow CI job.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Checked lib0 time.js: getUnixTime=Date.now is bound at load, so the clock must be patched before import.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided in docs/adr/0012-live-differential-harness.md: stateless ops scripts on one Node child over NDJSON, comparisons both directions plus convergence, FsCheck generation, virtual clock, timeouts, replayable failures, CI tiers. Glossary gained two terms. Verified by user agreement across three grilling rounds.
<!-- SECTION:FINAL_SUMMARY:END -->
