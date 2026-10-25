---
id: TASK-4
title: y-sharp implementation plan
status: To Do
assignee: []
created_date: '2026-10-24 16:51'
updated_date: '2026-10-25 07:39'
labels: []
dependencies: []
documentation:
  - docs/handoff-implementation-planning.md
  - docs/adr/0018-memory-layout-requirements.md
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The y-sharp implementation plan: 44 session-sized spec tasks (TASK-4.1 to TASK-4.44, spec N is TASK-4.N) across Backlog milestones m-0 (M0 Foundations) to m-7 (M7 Integration and release). It builds y-sharp itself against the verification suite (TASK-2). Charted by the wayfinder map TASK-3, where every decision behind it is indexed.

## Orchestration (read this before spawning anything)

- **Models.** Every spec carries a `model:<opus|sonnet|haiku>` label. Spawn each spec's implementer subagent with that model (the Agent tool's `model` parameter). The label is a recommendation the owner may override per task.
- **HITL.** Specs labelled `hitl` (7, 43) need the owner: stop and hand them the checklist. Spec 6 becomes HITL only if an ADR 0018 selection rule escalates.
- **Owner decisions.** A spec may raise an "Owner decision needed:" comment under ADR 0022. It keeps working on the rest, but it cannot be Done until the owner answers.
- **Frontier.** Dependencies are wired between the spec tasks. Take any To Do spec whose dependencies are all Done. Within a milestone the specs run in parallel unless a dependency says otherwise. File ownership (ADR 0021) keeps parallel specs out of each other's files.

## Order

- **M0:** feature gates (1) and source layout skeleton (2) first. Then the spike: harness (3), candidates A and B (4, 5), selection and storage ADR (6). CI on a real runner (7, HITL) runs alongside.
- **M1:** values (8 to 10), sequential.
- **M2:** core (11 to 16), sequential; gate (17).
- **After M2:** M3 V2 (18 to 20), M4 types and events (21 to 28) and M6 protocols (35 to 37) run in parallel.
- **M5:** doc features (29 to 34), after the M3 and M4 gates.
- **M7:** integration and release (38 to 44) closes.

## Every spec

- Its description gives the spec's purpose, then: **Features** (what it adds to `Features.Landed`, ADR 0020), **Owns** (modules and files, ADR 0021), **Registry** prefixes, **Test files**, and where they apply **Seams provided**, **Budgets** and **Handoff section-7 items**.
- Shared acceptance criteria:
  - every test whose features have landed passes
  - this spec's features are added to `Features.Landed`
  - the traceability gate passes with no new pending entry
- Shared definition of done:
  - 0 warnings
  - Debug and Release test runs green
  - fixture drift check clean
  - ADR 0022 followed (`suite:` commits, a Suite changes list, no open owner decision)

## Decisions

ADRs 0005 (ERR-20 is permanent), 0009 (bytes per operation), 0018 (milestone and spike amendments), 0020 (feature gates), 0021 (modules and ownership), 0022 (suite-change rules). Research: `docs/research/test-surface.md` and `docs/research/storage-layout.md`.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Standing owner approval (2026-10-25): when a test is gated on a landed feature but fails only because it calls an API owned by a later spec, adding [Requires] for that later feature is pre-approved under ADR 0022. Record each one in the spec's task notes. Any other test, gate, budget or registry change still goes to the owner.
<!-- SECTION:NOTES:END -->
