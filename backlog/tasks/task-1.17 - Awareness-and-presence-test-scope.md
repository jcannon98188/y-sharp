---
id: TASK-1.17
title: Awareness and presence test scope
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:25'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.6
parent_task_id: TASK-1
ordinal: 18000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide how y-protocols awareness/sync/auth are specified for testing: scope, clock/timeout testing without real time, message fixtures.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided awareness/sync/auth test scope, recorded in docs/adr/0014-awareness-and-protocol-test-scope.md. All three modules in scope, providers/envelope out; TimeProvider/FakeTimeProvider as time seam; match/diverge split for AW/SY quirks (atomic bad batch, idempotent destroy, Try* failures); structural-equality change filter; awareness ops-script steps and golden vectors from oracle; awareness and sync in the live differential. Verified by grilling session with the user; no code run.
<!-- SECTION:FINAL_SUMMARY:END -->
