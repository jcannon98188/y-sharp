---
id: TASK-10
title: Update the API surface baseline for the server tier
status: To Do
assignee: []
created_date: '2026-10-26 22:02'
labels:
  - api
dependencies: []
ordinal: 66000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
tools/api-surface/check.sh reports the baseline is stale since TASK-5.2: YRelay (now with TryPrepareUpdate, Commit, PreparedUpdate), YUpdate.TryDecode and DecodedUpdate, YSync.TryReadFrame and the span WriteStep1/WriteStep2 overloads are not in it. The baseline changes are an owner review (public API).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The baseline lists the current public API of YSharp and YSharp.Server and check.sh passes
- [ ] #2 Each added member is reviewed against docs/api-decisions.md
<!-- AC:END -->
