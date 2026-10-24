---
id: TASK-3.9
title: Skip gating once the stub starts disappearing
status: To Do
assignee: []
created_date: '2026-10-24 17:00'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - tests/YSharp.TestKit/Stubs/ApiStub.cs
  - docs/research/test-surface.md
parent_task_id: TASK-3
ordinal: 52000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

`ApiStub.IsStub` (tests/YSharp.TestKit/Stubs/ApiStub.cs) is one global probe on `YValue.Null`. When the first spec implements `YValue`, every test in an area that is still unimplemented stops skipping and fails with `NotImplementedException`. How should "not implemented yet" be expressed per area or per milestone so the suite stays green while milestones land, without hiding real `NotImplementedException` bugs or weakening the rule that every skip is a to-do? Which plan spec makes the change, and does it have to land before M0 code?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The per-area or per-milestone skip mechanism is decided
- [ ] #2 Which plan spec changes it, and when, is decided
<!-- AC:END -->
