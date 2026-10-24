---
id: TASK-2.5
title: 'Spec 5: Values and codecs'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 01:13'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0007-yvalue-contract.md
  - docs/adr/0002-yvalue-pinned-any-mapping.md
  - docs/research/encoding.md
  - docs/research/json.md
  - docs/research/quirks.md
parent_task_id: TASK-2
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. YValue, the JS-compatible JSON writer and reader, V1/V2 encode and decode, merge, diff and convert.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests and fixtures cover all ENC-* entries and the number and string quirks (Q*)
- [ ] #2 Ported encoding, compatibility and updates upstream tests have ledger outcomes
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Generate wire fixtures for ENC-* and number/string quirks with the Node generator (tools/fixture-gen/scenarios/encoding.mjs): any-values, embeds-and-formats, strings-utf16, ids-clocks-lengths, delete-sets, struct-layouts, merge-diff-convert-edges, malformed-codec-extras.
2. C# side in tests/YSharp.Tests/Codecs: mergeUpdates/diffUpdate/convertUpdate and insert-kind steps for the ops interpreter, fixture replay (updates, state, sv, results, malformed contract, truncation).
3. Unit tests with oracle goldens: YValue/Any quirks, JS-compatible JSON writer/reader, V1/V2 layouts, delete sets, malformed input (ENC-29..69), strings (Q16-19), merge/diff/convert.
4. Port the 13 ledger rows (encoding, compatibility, updates upstream tests); obfuscate skipped with reason (ENC-26 excluded, no API).
5. Registry: owned entries active (or excluded with reason), kinds set to diverge where ADR 0003/0005 decide; ledger outcomes; ratchet pending high-water mark.
6. Verify: dotnet build, dotnet test (stub => skips), fixture check, traceability gate; attach matrix; merge integration/verification-suite.
<!-- SECTION:PLAN:END -->
