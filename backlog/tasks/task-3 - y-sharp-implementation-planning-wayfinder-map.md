---
id: TASK-3
title: 'y-sharp implementation planning: wayfinder map'
status: To Do
assignee: []
created_date: '2026-10-24 16:51'
updated_date: '2026-10-24 17:13'
labels:
  - 'wayfinder:map'
dependencies: []
documentation:
  - docs/handoff-implementation-planning.md
ordinal: 42000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Destination

A spec-ready **y-sharp implementation plan** in Backlog: a parent task whose children are session-sized spec tasks with acceptance criteria, assigned to Backlog milestones M0 (storage spike) through the last implementation milestone. For each milestone the plan names the registry IDs and test files that move from skipped to passing, the budgets it records, the seams it provides and the unproven items (handoff section 7) it settles. The plan ends at the release gate: skip count zero, traceability gate green, budgets recorded, `YSHARP_RELEASE_GATE=1` passing. No implementation code is written and the spike is not run; the spike is the plan's first spec.

## Notes

- Inputs: `docs/handoff-implementation-planning.md` (start here), ADRs 0004, 0005, 0006, 0008, 0009, 0018, 0019, `docs/api-decisions.md`, `docs/inventory/`, the stub in `src/YSharp`, `docs/research/*.md`. Vocabulary in `GLOSSARY.md`.
- Tracker: Backlog.md. Map = this task; tickets = its subtasks labelled `wayfinder:<type>`; blocking = task dependencies; claim = assignee; resolution = Final Summary + Done. Never edit Backlog markdown by hand.
- Plan, don't do. Research tickets write Markdown into `docs/research/` on `main`.
- Standing decisions from charting: milestones are Backlog milestones, and specs are one agent session each (ADR 0019 rule). ADR 0018's gate *rules* are fixed; its milestone *boundaries and order* may be amended if the data shows they cannot hold, recorded as an amendment to ADR 0018. M0 spikes both storage models (object-per-item and indexed arenas), minimally, with no codec. The milestone order is in the ADR 0018 amendment (TASK-3.4). Pushing to GitHub and getting CI green on a real runner is an M0 HITL task in the plan. Packaging and publishing are out of scope. TASK-2.2's open criterion (end-to-end sim harness through `ISimInternals`) is absorbed by the plan; TASK-2.2 and TASK-2 close with a pointer to the plan task that owns it.
- Consult skills: grilling + domain-modeling for HITL tickets, research for AFK, codebase-design for the module-boundaries ticket.

## Decisions so far

- [Faulted document behaviour (ERR-20)](TASK-3.1): subagent behaviour confirmed; the fault is permanent, recover by loading `EncodeStateAsUpdate` into a fresh `Doc` (ADR 0005).
- [Research: test surface by entry point](TASK-3.2): skip is one global probe; nearly everything needs V1 apply and transactions; doc features and YValue fit no ADR 0018 milestone (`docs/research/test-surface.md`).
- [Research: Yjs and Yrs struct layout and the baseline workloads](TASK-3.3): Yjs figures are bytes per op; array needs a dense 8-byte chunked column; "item" and the steady-state mix are undefined (`docs/research/storage-layout.md`).
- [Milestone boundaries and order](TASK-3.4): M0 spike and M1 values in parallel, then M2 core (store, integrate, transactions and lock, V1), then a fan-out of M3 V2, M4 types and events, M5 doc features and M6 protocols, closing with M7 integration and release (ADR 0018 amendment).
- [Skip gating once the stub starts disappearing](TASK-3.9): declared spec-sized feature gates replace the global stub probe; tests whose features have not landed skip before running, the checker rejects untagged tests, NotImplementedException always fails; built by an M0 spec that blocks M1 (ADR 0020).

## Not yet specified

- Which differential generators (XML, nested types, subdocs, `sync.*`, awareness) and which extra compared fields (`events`, `structs`, `ds`) get built, and at which milestone.
- The spec template: what each plan task contains beyond acceptance criteria (owned IDs, test files, seams, budgets, file ownership) and the per-spec definition of done.
- Closing TASK-2.2 and TASK-2 once the plan task that absorbs the sim-harness criterion exists.
- How the reflection seams (`DocSeams`, `UndoSeams`, `ScheduleSeam` adapter) and `ISimInternals` are wired as the implementation lands.

## Out of scope

- NuGet packaging, versioning and publishing.
- Yjs v14.
<!-- SECTION:DESCRIPTION:END -->
