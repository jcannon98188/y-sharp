---
id: TASK-2
title: 'y-sharp verification suite: implementation specs'
status: Done
assignee: []
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 19:43'
labels: []
dependencies: []
references:
  - docs/adr/0019-implementing-agent-spec-breakdown.md
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Parent of the 14 specs that build the y-sharp verification suite decided in the wayfinder map (TASK-1). Breakdown, ownership rules and definition of done are in ADR 0019; each spec is one agent session. Specs are built against the API stub; registry entries start pending and ratchet to active. The y-sharp implementation itself is out of scope.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
API gap (from Spec 5): stub StateVector/ItemId use uint client ids but Q12/ENC-37 accept clients up to 2^53-1 on the wire; stub also lacks parseUpdateMeta/logUpdate/obfuscateUpdate (ENC-26/57-59 excluded). Needs an ADR 0008 decision before implementation.

Implementation seams (from Spec 6): internal string Doc.DumpStructs() (ADR 0016 structs dump) and internal bool Doc.HasPending are required by the structs tests; see tests/YSharp.Tests/Structs/StructsSupport.cs. Q22 pending (needs ADR 0003 decision), Q25 pending (Spec 7 deep-observer order).

Orphan: Q25 had no real owner (Specs 6/7 each pointed at the other); assigned to Spec 10 via AC. API gaps from Spec 7: no root YXmlText for Doc.Get; no YArray.Slice/ForEach/Map; Transact returns nothing; AfterAllTransactions has no transaction list; item identity not public (compareIDs, getTypeChildren approximated); YEvent invalid after dispatch (JS shared-event semantics not reproducible). Need ADR 0008 decisions.

Spec 8 API gaps: seams by reflection (Doc.CollectGarbage/ShouldLoad/AutoLoad/CollectionId/Meta/SignalLoaded/SignalSynced; UndoManager.Undoing/Redoing/CurrStackItem). Stub gaps: no root XmlText getter, no UndoManagerOptions.Doc, ItemView exposes only id, XmlFragment can't hold a subdoc, no whenLoaded/whenSynced, no GetSubdocGuids. Open: RP-05, PUD-05 follow JS for now.

Spec 9 API changes/gaps: added Awareness.TryEncodeUpdate and optional YSync.TryReadMessage errorHandler to the stub (AW-31/32, SY-8/19/20); client ids uint vs 53-bit (AW-34); GetStates snapshot vs live semantics unspecified (AW-13); AU-5 assumes ArgumentNullException. Needs ADR 0008 decisions.

Spec 10: Q25 now active (owner 10). API gaps: no Y.cleanupYTextFormatting counterpart (internal int YText.CleanupFormatting() seam); Q25 fixture assumes YEvent.GetPath() relative to CurrentTarget. fixtures/sim ~18MB. Randomized gating budget (~60s) unmeasured until implementation exists. Spec 11: ERR-20 pending (needs fault-injection seam); limit semantics unspecified (MaxAnyDepth, MaxStructsPerUpdate, MaxStringBytes, MaxUpdateBytes); ERR-19 assumes OOM from observer propagates. 2-min gating budget unproven. AFL++ job untested. Merged high-water: registry 36 / ledger 0.

Spec 12 API gaps: needs internal static class YSharp.ScheduleSeam (settable Action<string>? Point, Action<string, Func<bool>>? Block) per tests/YSharp.TestKit/Concurrency/ScheduleSeam.cs; CC-9 exception type for stale views unspecified; TryTransact under held read lock unspecified; Transact(body, origin, timeout) with null origin binds to generic overload (use named args); CC-18 lock-order checker is Debug-only. Pending: registry 16 / ledger 0.

Spec 13 limits: live properties unrun until implementation exists (expect unrecorded divergences needing normalisers); no awareness/sync.* or XML/nested/subdoc generators yet (SharpSide.Extensions hook only); events/structs/ds not compared on the y-sharp side yet.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All 14 suite specs are Done. The one criterion that could not run against the stub (end-to-end sim harness) moved to TASK-4.38 in the implementation plan.
<!-- SECTION:FINAL_SUMMARY:END -->
