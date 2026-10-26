---
id: TASK-3
title: 'y-sharp implementation planning: wayfinder map'
status: Done
assignee: []
created_date: '2026-10-24 16:51'
updated_date: '2026-10-24 19:43'
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
- [Module boundaries and file ownership in src/YSharp](TASK-3.6): module folders per milestone; storage hidden behind an ItemRef handle; generic V1/V2 codec seam; partial-method lifecycle hooks; one owner per file, partial classes for shared public types; an M0 layout-skeleton spec (ADR 0021).
- [M0 spike design](TASK-3.5): both models spiked local-only over a shared content layer behind a driver the real store later reuses; bytes per operation, median of 3; eliminate failures, prefer headroom, then simplicity (ADR 0018 spike amendment, ADR 0009 wording).
- [Rules for changing the suite during implementation](TASK-3.7): free / with-evidence / owner levels; the oracle decides disputed tests; new divergences, retirements, budget raises and any weakening need the owner; suite: commits and a Suite changes list in every spec's DoD (ADR 0022).
- [Package the map into the implementation plan](TASK-3.8): 44 specs under TASK-4 across milestones M0-M7, with gate specs per budgeted milestone, the first live differential at the M4 gate, all differential generators in scope, model: and hitl labels, and TASK-2.2's sim criterion moved to TASK-4.38.

## Not yet specified

Nothing. The destination is reached: see TASK-4.

## Out of scope

- NuGet packaging, versioning and publishing.
- Yjs v14.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Destination reached: the implementation plan is TASK-4 (44 specs, milestones m-0 to m-7). Decisions are in ADRs 0005, 0009, 0018, 0020, 0021 and 0022 and in two research notes.
<!-- SECTION:FINAL_SUMMARY:END -->
