---
id: TASK-6
title: 'Registry: promote the XML fragment, XML text and hook rows'
status: To Do
assignee: []
created_date: '2026-10-26 00:03'
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
- [ ] #1 Each X-F, X-T and X-H row of docs/research/types.md section 4 that describes in-scope behaviour has a registry entry (toDOM rows are recorded as out of scope)
- [ ] #2 The XmlTests oracle regressions added by TASK-4.26 cite the new ids
- [ ] #3 The traceability gate passes
<!-- AC:END -->
