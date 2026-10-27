---
id: TASK-19
title: >-
  Pick the later of two equally near search markers, as Yjs findMarker does, so
  a delete's format cleanup matches the oracle
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-27 17:51'
updated_date: '2026-10-27 17:55'
labels:
  - bug
  - text
  - interop
dependencies: []
priority: high
ordinal: 79000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Nightly run 40547040408 on main (tree-scripts differential, FsCheck replay 8044439443565728359,11400714819323198485) found a text delete whose update also deleted a bold-end format item that Yjs keeps. Peer c's text keeps search markers because c first asked for it after the formatted text arrived (YText's constructor; ContentFormat.integrate only drops markers on a text that already exists). With markers at index 2 and 6, Delete(4, 1) walked from the earlier one; Yjs findMarker's reduce((a, b) => |index - a.index| < |index - b.index| ? a : b) keeps the later. The walk from 2 stopped on the bold end with no current attributes, so cleanupFormattingGap deleted it, and every later update differed from the oracle's.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 SearchMarkers.Find and Peek choose the later of two equally near markers
- [x] #2 A unit test pins the oracle's bytes for the delete (no Node)
- [x] #3 A Smoke live differential replays the shrunk nightly script and matches the oracle
- [ ] #4 The Nightly tier passes on main
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. In SearchMarkers.Find and Peek, use <= in the nearest-marker scan so ties go to the later marker (Yjs reduce). 2. Unit test in TextTests pinning c's delete update 000167010001 (red before: 00026701000165010A01). 3. Smoke live test EquallyNearMarkersTests replaying the script. 4. Gate, smoke, API check; PR; Nightly on main.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Red before the fix: the unit test's update was 00026701000165010A01 (also deleting 101:10); the live test differed at a.v1[2]. Green after. YSHARP_RELEASE_GATE=1 full suite 5,087 total, 0 failed; Smoke tier 0 failed; API surface unchanged. The replay of the saved nightly script matches the oracle.
<!-- SECTION:NOTES:END -->
