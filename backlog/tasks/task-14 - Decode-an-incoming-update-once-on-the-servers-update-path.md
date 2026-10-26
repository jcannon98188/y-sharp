---
id: TASK-14
title: Decode an incoming update once on the server's update path
status: To Do
assignee: []
created_date: '2026-10-26 23:57'
labels:
  - server
  - performance
  - api
dependencies: []
ordinal: 70000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the performance audit after PR #2: DocumentMailbox.AcceptAsync decodes each peer update up to four times: YUpdate.TryConvert (to V2 for the store), the validation hook's DecodedUpdate, YRelay.TryPrepareUpdate (decodes the V2 again) and the hot Doc's TryApplyUpdate. EditAsync does the same for server edits. Decode once, then write the V2 record, prepare the relay and apply to the Doc from the decoded form. YSharp.Server reaches the core only through public API (ADR 0024), so this needs public overloads that take an already-decoded update (for example on DecodedUpdate from YUpdate.TryDecode). That is an API decision for the owner (docs/api-decisions.md). Nothing a peer sees changes: the bytes stored and broadcast stay the same.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An accepted peer update is decoded once on the server's path, a server edit at most once after it is captured
- [ ] #2 Stored records and broadcast bytes are unchanged on the existing server and simulation tests
- [ ] #3 Any new public members are recorded in the API baseline and approved by the owner
<!-- AC:END -->
