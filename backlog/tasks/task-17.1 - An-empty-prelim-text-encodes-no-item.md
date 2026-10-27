---
id: TASK-17.1
title: An empty prelim text encodes no item
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 12:17'
updated_date: '2026-10-27 12:19'
labels:
  - bug
  - types
dependencies: []
parent_task_id: TASK-17
ordinal: 74000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pushing `new YXmlText("")` (or `new YText("")`) into a fragment from client 7001 encodes `0102d936000701016606040d936000000` with a zero-length string item, where Yjs encodes no item (`0101d9360007010166060`), and y-sharp itself rejects that output as Malformed (ENC-88), as Yjs does. Yjs `YText.insert` returns on an empty string, including the constructor pending insert. A host avoids it only while no server edit creates a text node.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 new YText("") and new YXmlText("") integrate with no content item, and the update they produce matches the Yjs bytes
- [x] #2 A test pins the Yjs bytes for an empty prelim text pushed into a fragment
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Skip the constructor's pending insert for an empty string (Yjs YText.insert returns on length 0); pin the Yjs v13.6.33 bytes for client 7001.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Suite changes: TextTests.NewText_WithAnEmptyString_IntegratesWithNoContentItem (new, Yjs bytes 0101d93600070101660600 for YXmlText in a fragment, 0101d93600070101610200 for YText in an array). TextTests 46/46 pass.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
new YText("") and new YXmlText("") no longer queue an insert, so they integrate as the type alone with Yjs-identical bytes; verified by a test pinned to Yjs output.
<!-- SECTION:FINAL_SUMMARY:END -->
