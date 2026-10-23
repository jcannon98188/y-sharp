---
id: TASK-1.21
title: 'Research: JS-compatible JSON writer and reader'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:38'
labels:
  - 'wayfinder:research'
dependencies: []
references:
  - docs/research/quirks.md
parent_task_id: TASK-1
ordinal: 22000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
V1 embeds and formats, awareness states and XML attribute text all depend on JSON text matching JavaScript byte for byte. Number formatting, string escaping and lone surrogates differ from .NET defaults.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Findings in docs/research/json.md: shortest round-trip number rules, string escaping, lone surrogate handling, parse leniency, with oracle-run examples
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Researched JS JSON behaviour against the pinned sources and Node: JSON is V1-only; number layout, string escaping and parse rules documented with oracle-run examples; System.Text.Json cannot match, so a custom writer/reader is proposed. Gap: ECMAScript QuoteJSONString and JSON.parse sections not quoted; pinned by oracle runs instead. Findings in docs/research/json.md.
<!-- SECTION:FINAL_SUMMARY:END -->
