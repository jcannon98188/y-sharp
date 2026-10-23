---
id: TASK-2.4
title: 'Spec 4: Node fixture generator'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
references:
  - docs/adr/0011-fixture-format-and-generator.md
  - docs/adr/0016-ops-script-vocabulary-and-dump-formats.md
parent_task_id: TASK-2
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 1. Produces the golden fixtures all area specs depend on, plus the drift job.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 tools/fixture-gen replays ops scripts on pinned Yjs and lib0 and writes fixtures/<area>/<scenario>.json with manifest and version headers
- [ ] #2 Node version pinned via .nvmrc; npm run gen is deterministic
- [ ] #3 Drift CI job fails on any diff in fixtures/
- [ ] #4 Stored malformed cases and truncation outcomes supported per ADR 0011
- [ ] #5 Fixture header carries yjs and lib0 versions, seed and covers list
<!-- AC:END -->
