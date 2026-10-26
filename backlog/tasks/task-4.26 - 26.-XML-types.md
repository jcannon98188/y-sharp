---
id: TASK-4.26
title: 26. XML types
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 00:07'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.21
  - TASK-4.24
parent_task_id: TASK-4
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`YXmlFragment`, `YXmlElement`, `YXmlText`, `YXmlHook`.

**Features:** `Xml`
**Owns:** `src/YSharp/Types/Xml/`
**Registry:** `X` entries
**Test files:** `Types/XmlTests.cs`, `Structs/StructModelTests.cs` (XML cases)
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (2026-10-25): YXmlFragment, YXmlElement, YXmlText and YXmlHook over TypeList/TypeMap, following Yjs 13.6.33.
- Prelim fragments buffer their children and elements buffer their attributes.
- Integration inserts the children first, then the attributes in insertion order.
- Push is insert(length).
- InsertAfter goes through TypeList.InsertAfterItem without markers.
- The tree walker is pre-order and iterative. querySelector compares upper-cased names and never matches an empty node name.
- toString uses JS String() coercion (XmlStrings).
- XmlText.toString renders formats as sorted nested tags.
- The XML list types keep no search markers (Branch TypeRefs.UsesMarkers), as in Yjs.

JS parity: a throwaway probe compared trees, attributes, push after deleted children, insertAfter, XmlText formats and embeds, attribute stringification, clone and prelim ordering, hooks, and querySelector/treeWalker against yjs 13.6.33. Strings and update bytes were identical in every scenario.

API surface: the baseline gains the overrides YXmlElement.Clone, YXmlHook.Clone and YXmlText.ToString (additive).

Suite changes (ADR 0022):
- Gate corrections (pre-approved; the failures are a missing later feature):
  - XmlTests NodeName_DefaultsToUndefined_AndKeepsAGivenName, Xml_SetAttribute_IsReadBackLocallyAndOnThePeer, Xml_HasAttribute_TrueAfterSetAndFalseAfterRemove and Xml_TreeWalker_VisitsNodesInDocumentOrder gain [Requires(Sync)] (Net.Init uses YSync).
  - SharedTypeTests ConstructorSugar_InitialContent_IsBuffered gains [Requires(Sync)].
  - StepKindTests Observe_AnyEvents_AreRecordedInFiringOrder gains [Requires(Events)].
- New oracle regressions in XmlTests (expected values from yjs 13.6.33):
  - push after deleted children
  - insertAfter
  - XmlText toString
  - attribute String() conversion
  - clone ordering
  - hook printing and clone
  - querySelectorAll

Owner sign-off:
1. An integrated InsertAfter throws ArgumentException when the reference is not a child of the node. JS does not check, and would corrupt or link elsewhere.
2. Search markers are removed from XmlFragment and XmlElement to match Yjs. Positions are unchanged; this is a performance trade.
3. QuerySelector uses ToUpperInvariant and the element toString uses ToLowerInvariant. JS toUpperCase maps e.g. 'ß' to "SS", and toLocaleLowerCase is locale-dependent.
4. The fragment indexer throws when the child is not a type; JS returns the raw content. A hook's Clone deep-clones nested types; JS re-sets the live instances, which then fail to integrate.
5. A prelim fragment's Insert past the end throws "Length exceeded!", mirroring YArray; JS splice appends.
6. Gap ticket TASK-6: the X-F, X-T and X-H rows are missing from docs/inventory/types.yaml.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
XML types landed (Feature.Xml): fragment, element, text and hook, at byte-level parity with yjs 13.6.33 on the probed scenarios. Six sign-off items are recorded in the notes. Gap ticket TASK-6 covers the missing registry rows.
<!-- SECTION:FINAL_SUMMARY:END -->
