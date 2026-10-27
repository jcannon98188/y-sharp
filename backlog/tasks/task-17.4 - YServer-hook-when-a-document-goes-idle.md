---
id: TASK-17.4
title: YServer hook when a document goes idle
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 12:17'
updated_date: '2026-10-27 12:56'
labels:
  - server
  - api
dependencies: []
parent_task_id: TASK-17
ordinal: 77000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A host may write a snapshot when a document goes idle; YServer unloads an idle document (SRV-20) with no hook for that. Owner asked to include it in preview.2 (2026-10-27). Public API addition: needs an api-decisions entry and baseline update.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 YServerOptions offers a hook that runs on the document worker when an idle document is about to be unloaded, with the document id and its state
- [x] #2 A failing or slow hook does not keep the document loaded forever or break the server
- [x] #3 The decision is recorded in api-decisions.md and the server API baseline is updated
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
YServerOptions.DocumentUnloading (Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask>?) is awaited on the worker from EvictAsync just before TryEvict, with the relay's state as a V1 update; DocumentUnloadingDeadline (5 s) cancels its token and bounds the wait (Task.WaitAsync on the server TimeProvider), and a throwing, cancelled or late hook is swallowed and the document unloaded anyway, since the store holds every accepted update. Messages wait while it runs; a document opened meanwhile stays loaded and the hook runs again at the next idle unload. Not called on shutdown or DeleteAsync. Recorded as SRV-23 (ADR 0024, server.yaml, id-lock), api-decisions E7 (shape chosen in implementation; owner asked for the hook, 'Include it now'), docs/server.md; baseline.server.txt gains the two properties. Tests: four in LifecycleTests (SRV-23 section).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YServerOptions.DocumentUnloading gets an idle document's id and V1 state on its worker just before unload, bounded by DocumentUnloadingDeadline (5 s); a throwing or overrunning hook does not keep the document loaded, and a document opened meanwhile stays loaded (SRV-23, api-decisions E7, docs/server.md, server API baseline). Tests: four in LifecycleTests. Verified: YSHARP_RELEASE_GATE=1 full suite 5074 total, 0 failed; YSHARP_TIER=Smoke interop 119/120 (replay env-gated); fixture-gen npm run check and the API surface check clean.
<!-- SECTION:FINAL_SUMMARY:END -->
