---
id: TASK-1.2
title: 'Research: encoding and update behaviour inventory'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:11'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 3000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Inventory V1/V2 encoders and decoders, DeleteSet, state vectors, mergeUpdates, diffUpdate, convertUpdateFormat*, and the lib0 primitives and Any encoding they use (yjs/src/utils/encoding.js, updates.js, UpdateEncoder/Decoder.js, lib0/). Output: docs/research/encoding.md including exact byte layouts and malformed-input behaviour in JS.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Wrote docs/research/encoding.md: exact V1/V2 byte layouts (struct, content, V2 column container), state vector, delete set, lib0 varint/RLE/OptRLE/Any/string primitives, merge/diff/convert/applyUpdate behaviour, and JS malformed/truncated-input behaviour. Claims have file:line refs and were verified by running the oracle source; includes hex goldens and a checklist of version-specific traps.
<!-- SECTION:FINAL_SUMMARY:END -->
