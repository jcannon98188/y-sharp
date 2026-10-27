---
id: TASK-6
title: 'Registry: promote the XML fragment, XML text and hook rows'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 00:03'
updated_date: '2026-10-27 00:04'
labels:
  - gap
  - registry
dependencies: []
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
docs/research/types.md section 4 defines X-F1..X-F8 (YXmlFragment), X-T1..X-T5 (YXmlText) and X-H1..X-H3 (YXmlHook), but docs/inventory/types.yaml only carries X-1, X-2 and X-E1..X-E8. Spec 26 (TASK-4.26) implemented these behaviours and pinned them with oracle tests in tests/YSharp.Tests/Types/XmlTests.cs, but the tests cannot cite registry ids. Examples: fragment push is insert(length), not an append after deleted children; XmlText toString renders formats as tags; insertAfter; the tree walker; hook clone. As a result, the traceability matrix does not show these behaviours or require coverage for them, and spec 40 (XML differential) has no ids to target.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Each X-F, X-T and X-H row of docs/research/types.md section 4 that describes in-scope behaviour has a registry entry (toDOM rows are recorded as out of scope)
- [x] #2 The XmlTests oracle regressions added by TASK-4.26 cite the new ids
- [x] #3 The traceability gate passes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Added 16 registry entries to docs/inventory/types.yaml and id-lock.txt: X-F1..X-F8, X-T1..X-T4 and X-H1..X-H3 are active; X-T5 (toDOM) is excluded as not-applicable (ADR 0008). The X-H1 note records that its toDOM part is out of scope.

Gate correction: the traceability checker's ID format accepted only an E sub-letter (X-E1). It now accepts E, F, T or H, so the research doc's ids are kept as written.

Suite changes:
- TestKit TraceabilityChecker: IdFormat allows [EFTH] before the number.
- XmlTests: existing TASK-4.26 tests now cite X-F1..X-F5, X-F7, X-F8, X-T2, X-T3, X-H1 and X-H2. New tests: Length_BeforeAttach_IsThePrelimContentLength (X-F6), ToJson_OfAFragment_IsItsXmlString (X-F5), XmlText_HasTextBehaviour_Attributes_AndSiblings (X-T1), HookName_SurvivesEncodeAndDecode (X-H3).
- SharedTypeTests: the per-type event test also covers X-F8, X-T4 and X-H1.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The XML fragment, XML text and hook rows from docs/research/types.md section 4 are now in the registry, and XmlTests cites them; the traceability gate passes. X-T5 (toDOM) is excluded as out of scope. The checker's ID format now accepts X-F, X-T and X-H ids (gate correction).
<!-- SECTION:FINAL_SUMMARY:END -->
