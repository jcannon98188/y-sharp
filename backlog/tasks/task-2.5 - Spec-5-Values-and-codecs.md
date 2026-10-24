---
id: TASK-2.5
title: 'Spec 5: Values and codecs'
status: Done
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
documentation:
  - docs/inventory/matrices/spec-5-traceability.md
parent_task_id: TASK-2
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. YValue, the JS-compatible JSON writer and reader, V1/V2 encode and decode, merge, diff and convert.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tests and fixtures cover all ENC-* entries and the number and string quirks (Q*)
- [x] #2 Ported encoding, compatibility and updates upstream tests have ledger outcomes
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Evidence: dotnet build YSharp.slnx 0 warnings/0 errors (Debug and Release). dotnet test --solution YSharp.slnx: Debug 451 total, 0 failed, 167 passed, 284 skipped (skips are tests that need the implementation; each skips on NotImplementedException from the API stub, per ADR 0019); Release 451/0 failed/171 passed/280 skipped; gating tier runs in about 3-4 s. Traceability gate (TraceabilityGateTests) passes with 0 violations: owner 5 has 105 entries, 101 active, 4 excluded not-applicable (ENC-26 obfuscate, ENC-57/58/59 JS-typed arguments), 0 pending. Pending high-water mark ratcheted 571 -> 466 (registry) and 210 -> 197 (ledger). Ledger: 12 rows ported, testObfuscateUpdates skipped with reason. Fixtures: npm run check in tools/fixture-gen reports 25 files up to date (8 new from scenarios/encoding.mjs; hostile.mjs gains Q12 divergence on the 2^63 client id; fixture.schema.json covers pattern now accepts Q-ids). C# interpreter gains mergeUpdates, diffUpdate, convertUpdate, text.insertEmbed, array.insertType, map.insertType in tests/YSharp.Tests/Codecs (registered only when the shared registry lacks them). Kinds changed from match to diverge where ADR 0003/0005 already decide: ENC-37, 46, 51, 60, 62, 64, 65, 67, 68, each with a [Diverges] test. Open API questions for the implementer: StateVector and ItemId use uint client ids, but Q12 and ENC-37 say the wire accepts clients up to 2^53-1 (tests assert acceptance via TryApplyUpdate only); the stub has no parseUpdateMeta, logUpdate or obfuscateUpdate; doc/struct/ds dumps of fixtures are compared by specs 6 and 7 (readers live there). Matrix: docs/inventory/matrices/spec-5-traceability.md.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Spec 5 delivered as tests, fixtures and registry state against the API stub. Added 8 Node-generated fixtures (any values, embeds/formats, UTF-16 strings, ids/clocks/lengths, delete sets, struct layouts, merge/diff/convert edges, extra malformed inputs) and C# tests under tests/YSharp.Tests/Codecs: oracle-golden unit tests for YValue/Any, the JS-compatible JSON writer and reader, V1/V2 layout and columns, delete sets, malformed input (ENC-29..69), string quirks, merge/diff/convert, bytewise fixture replay, and ports of the 13 encoding, compatibility and updates upstream tests. All 105 owned entries are active or excluded with a reason, ledger rows are resolved, the pending mark ratcheted down, fixtures regenerate without drift and the gate passes. Verified with dotnet build, dotnet test (Debug and Release, 0 failures; implementation-dependent tests skip on the stub) and npm run check.
<!-- SECTION:FINAL_SUMMARY:END -->
