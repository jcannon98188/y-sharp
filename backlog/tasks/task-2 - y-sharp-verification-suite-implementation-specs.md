---
id: TASK-2
title: 'y-sharp verification suite: implementation specs'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 01:37'
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
<!-- SECTION:NOTES:END -->
