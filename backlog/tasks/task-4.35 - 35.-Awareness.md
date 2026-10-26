---
id: TASK-4.35
title: 35. Awareness
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 09:24'
labels:
  - spec
  - 'model:sonnet'
milestone: m-6
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Awareness state, wire format, timeouts on `TimeProvider` (ADR 0014).

**Features:** `Awareness`
**Owns:** `src/YSharp/Protocols/Awareness/`
**Registry:** `AW` entries
**Test files:** `Protocols/Awareness*Tests.cs`
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
Implemented src/YSharp/Protocols/Awareness/Awareness.cs: own Monitor gate (never the doc lock, CC-5/CC-19), events fire synchronously under it; TimeProvider timer 3000/3000 renews the local state at 15000 ms and removes remote clients at 30000 ms (origin "timeout"); TryApplyUpdate decodes the whole batch first and then applies JS's clock rules; equalityDeep port (NaN != NaN, zeros equal, key order ignored); Dispose fires Destroyed, clears the local state, stops the timer and unsubscribes from doc.Destroyed; idempotent.
Verification: Debug and Release suites green; skip audit shows no skip naming Awareness (remaining: Sync, Sim, Concurrency, Fuzz); with Sync/Concurrency temporarily ungated locally every awareness fixture and CC-19 test passed (only sync/auth and one Concurrency checker test failed, both later specs); npm run check up to date.
Fix during verification: the gate first used System.Threading.Lock, which CC-5 forbids (LockModelTests); now a Monitor object.
Suite changes: none.
Owner sign-off:
- SetLocalState(Undefined) throws ArgumentException (JS stores undefined).
- Invalid UTF-8 in a state string fails the whole batch (strict varString; JS substitutes U+FFFD).
- EncodeUpdate throws YException when a state cannot be JSON-written (e.g. BigInt); TryEncodeUpdate returns the error.
- Handler exceptions go to doc.ObserverFaulted and are counted in ApplyResult.ObserverFaults (JS emit would throw out of applyAwarenessUpdate).
- SetLocalStateField on a non-object state follows JS object-spread semantics (array/string/bytes spread to index keys; primitives to {}).
- Awareness unsubscribes from doc.Destroyed on Dispose (JS keeps the listener).
- TryModifyUpdate decodes with default limits (static, no doc).
- An id with a state but no meta in TryEncodeUpdate gives YError.Rejected (JS would throw on undefined meta).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Awareness (y-protocols awareness.js) ported: local/remote state, meta, the wire format (encode, apply, modify, remove), timeouts on an injected TimeProvider, change/update events and dispose, under its own Monitor lock. All gating suites green in Debug and Release; awareness fixtures and CC-19 verified; sign-offs recorded in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
