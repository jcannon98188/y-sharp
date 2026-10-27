---
id: TASK-17
title: Integration defects found by a host application (0.1.0-preview.2)
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 12:17'
updated_date: '2026-10-27 12:56'
labels:
  - bug
  - server
  - codec
dependencies: []
priority: high
ordinal: 73000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner integrated 0.1.0-preview.1 (357aad3) into a host application, and its integration tests found three defects and one missing hook, all for preview.2.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
All four subtasks Done. Release gate also found that a zero-length GC made a document's own state Malformed after 17.2; fixed under 17.2.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The four host integration defects are fixed for 0.1.0-preview.2: empty prelim text (17.1), zero-length delete range Malformed (17.2), server awareness state (17.3), unloading hook (17.4). Verified: YSHARP_RELEASE_GATE=1 full suite 5074 total, 0 failed; YSHARP_TIER=Smoke interop 119/120 (replay env-gated); fixture-gen npm run check and the API surface check clean. Not yet tagged or published.
<!-- SECTION:FINAL_SUMMARY:END -->
