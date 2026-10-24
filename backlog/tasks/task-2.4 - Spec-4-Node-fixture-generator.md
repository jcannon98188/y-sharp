---
id: TASK-2.4
title: 'Spec 4: Node fixture generator'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 00:33'
labels: []
dependencies:
  - TASK-2.2
references:
  - docs/adr/0011-fixture-format-and-generator.md
  - docs/adr/0016-ops-script-vocabulary-and-dump-formats.md
parent_task_id: TASK-2
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 1. Produces the golden fixtures all area specs depend on, plus the drift job.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 tools/fixture-gen replays ops scripts on pinned Yjs and lib0 and writes fixtures/<area>/<scenario>.json with manifest and version headers
- [x] #2 Node version pinned via .nvmrc; npm run gen is deterministic
- [x] #3 Drift CI job fails on any diff in fixtures/
- [x] #4 Stored malformed cases and truncation outcomes supported per ADR 0011
- [x] #5 Fixture header carries yjs and lib0 versions, seed and covers list
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node generator (resolve yjs/lib0/y-protocols from submodules via loader hook), tagged values in TestKit wire shape. 2. ops.schema.json (normative) + fixture.schema.json, validated by ajv and cross-checked against interpreter kinds. 3. Scenarios per area, truncation outcomes, malformed cases, manifest. 4. C# round-trip tests. 5. Drift verified by regenerate+diff.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Aligned to TestKit: tagged values {$:nan|inf|-inf|-0|undefined|bigint|bytes|str|obj}, header {schema,covers,versions,node,generator,seed}, step field 'step'. TestKit change: text.insert value now read as Tagged string so lone surrogates are expressible. Decisions: .nvmrc set to 24 (was 22; installed Node is 24); header node records the major only and the generator refuses another major. Observation shapes {api,value} defined in fixture.schema.json; 'results' observation holds pure mergeUpdates/diffUpdate/convertUpdate outputs; embeds must be JSON-serialisable (Yjs JSON.stringifies them). No real js-crash case exists yet; the classification is supported. Validation: dotnet build ok; dotnet test 180 total, 0 failed (incl. traceability gate and GeneratedFixtureTests); npm run gen twice produced identical output; npm run check passes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added tools/fixture-gen (loader hook to pinned submodules, interpreter, observations, ajv-validated schemas, 17 fixtures plus index.json manifest incl. truncation outcomes and malformed cases), Node pinned to 24 in .nvmrc, C# round-trip tests. Verified: dotnet build/test green (180 tests, 0 failed), generator run twice with no diff, npm run check passes; drift CI job (regenerate + git diff) unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
