---
id: TASK-3.9
title: Skip gating once the stub starts disappearing
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 17:00'
updated_date: '2026-10-24 17:13'
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
- [x] #1 The per-area or per-milestone skip mechanism is decided
- [x] #2 Which plan spec changes it, and when, is decided
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Survey how the stub gate is reached (support classes, fuzz, interop). 2. Grill the mechanism. 3. Record as an ADR and glossary terms.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Survey: about 23 files reach the gate, mostly through per-area support classes. Fuzz and interop each have their own stub checks. All of them depend on the global YValue.Null probe. Owner agreed Q1-Q6 in chat on 2026-10-24.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Declared feature gates replace the global stub probe (ADR 0020). Tests carry [Requires(Feature.X)], defaulting at class level. Features are spec-sized and milestones are sets of them. One typed TestKit list records landed features. A test whose features have not landed skips before it runs; a NotImplementedException is always a failure. The checker requires a gate on every library-touching test. Fuzz and interop use the same mechanism. The plan's first spec, Feature gates, sits in M0, blocks M1, and gets its feature list from the packaged plan. Glossary gains Feature and Landed feature. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
