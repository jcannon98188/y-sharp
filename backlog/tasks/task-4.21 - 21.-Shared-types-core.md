---
id: TASK-4.21
title: 21. Shared types core
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 22:32'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The common machinery under every shared type: `YType`, list and map cores, search markers, `Doc.Get*` and `YUntyped`, `YEntry` content and `ToJson`.

**Features:** `TypesCore`
**Owns:** `src/YSharp/Types/` (core), `Doc.Types.cs`
**Registry:** `TX` and `Y` core entries, `Q` type quirks (Q10, Q29)
**Test files:** `Types/SharedTypeTests.cs`
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. YType core: Branch.Owner/TypeRef/TypeName, Doc/Parent/IsIntegrated, prelim buffer (T-0.4/T-0.5), attach replays buffered content in the integrating transaction (T-0.6/T-0.8).
2. YEntry content mapping: Any/Binary/Type/Doc to Content*; reject an already-integrated type (T-0.13, Q10).
3. List core (insert/delete/get/iterate over Branch items) with search markers; map core (set/get/delete/has, last-writer by parent sub).
4. Doc.GetArray/GetMap/GetText/GetXml*/Get placeholder upgraded in place; Share snapshot; Doc.ToJson.
5. YUntyped Clone/ToJson.
6. Land Feature.TypesCore; run suites; gate corrections under the standing approval, listed in notes.
7. Debug + Release tests, fixture-gen check, api-surface check, memory gate.
Commit after each step (session-limit safety).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Progress (resume point): YEntry done (d88ae4b). YType owns a Branch (private protected ctor, WireTypeRef override per class, AttachedDoc, Parent via Branch.Item.Parent.Owner; XML element/hook names in Branch.TypeName) (7b177bb). Next: Doc.Get*/root placeholder upgrade (Doc.Types.cs over RootBranch), set AttachedDoc on roots and in HookTypeIntegrated, then list/map cores, prelim buffering, YUntyped, ToJson.

Decision: prelim buffering is per type, not a generic op queue in the core. Yjs replays a prelim YArray as one insert(0, prelimContent) and splices prelim deletes away, so replaying buffered operations one by one would put deleted items on the wire. The core supplies the seam: HookTypeIntegrated sets AttachedDoc and calls the virtual YType.OnIntegrated(tx) inside the integrating transaction (T-0.6, recursive for nested prelim types, T-0.8); specs 22-26 override it with their own buffers.
Other choices: the untyped placeholder upgrade keeps the root Branch (items are never re-parented); nested types read from an update are wrapped on first read (YType.Of); TypeMap.Entries is in key-slot order, which is the order keys first appeared (JS Map insertion order); Doc entries throw Sketch.NI until subdocs (spec 33).
Validation: temporary internal probe (not committed) matched Yjs V1 bytes for array insert [1,2] and map set k=1, ran 400 random list inserts/deletes through the search markers against a model, and round-tripped a nested map plus placeholder upgrade to a second doc. dotnet build YSharp.slnx 0 warnings; Debug and Release 8750 tests, 0 failed, no skip reason names TypesCore; fixture-gen check clean (141 files); api-surface unchanged (733); memory gate 36/36.
Suite changes: none (Features.Landed gains TypesCore in the landing commit, per ADR 0020). Gate corrections: none.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shared types core landed: YEntry; YType owns its Branch (wire type ref, XML names, Doc, Parent); Doc.Get*/Get with the untyped placeholder upgraded in place and YTypeConflictException on a different constructor; Share and Doc.ToJson; TypeList (typeListToArray/Get/InsertGenerics/Delete with search markers) and TypeMap (typeMapSet/Get/Has/Delete/GetAll), validating before the first change; YType.Of wraps nested types read from updates; HookTypeIntegrated attaches prelim types and calls OnIntegrated for per-type replay. Feature.TypesCore landed. Verified by Debug/Release suites (8750, 0 failed), an internal probe matching Yjs bytes and a random list model, fixture drift, API surface and memory gate.
<!-- SECTION:FINAL_SUMMARY:END -->
