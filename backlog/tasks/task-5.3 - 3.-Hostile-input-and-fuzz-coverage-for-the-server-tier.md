---
id: TASK-5.3
title: 3. Hostile-input and fuzz coverage for the server tier
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:38'
updated_date: '2026-10-26 22:01'
labels:
  - server
  - 'model:opus'
dependencies:
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 56000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The relay tier and mailbox accept bytes from untrusted peers and hold state across many updates, so growth of pending state and repeated bad input matter more than for a single apply. Extends ADR 0013 to these paths. **Suggested model:** opus (override freely): triage of any core bug found here goes through the ADR 0022 evidence rules.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Fuzz targets cover the relay type and the mailbox entry point, with a post-condition that nothing stays rented or retained after a rejected update
- [x] #2 Relay memory stays bounded by YLimits under a stream of adversarial updates (huge clocks, missing dependencies, many client ids, decompression-style size amplification)
- [x] #3 Any core bug found is recorded with oracle evidence per ADR 0022, and fixed or escalated as an owner decision
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Fuzz harness for the relay (RelayHostile guard and post-condition) and the ADR 0013 layers folded into a YRelay under default and tiny limits.
2. Stream bounds tests: huge clocks, missing dependencies, many client ids, amplification; BufferPool rentals.
3. Mailbox entry point fuzzed with hostile messages next to a good peer, with restart over the same store.
4. Fix whatever the targets find, with the ADR and registry updated.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Findings and fixes:
- The relay held pending structs and deletes (waiting on a missing clock) without bound: a stream of gapped updates under YLimits grew it forever. New RLY-7 (ADR 0024): the V1 encoding of what is pending past each client's state, plus clipped delete ranges past it, may not exceed Limits.MaxPendingBytes; an update over it is LimitExceeded and changes nothing. Accepted folds give the same bytes as before, so the Yjs oracle tests (RelayTests) still hold; Yjs mergeUpdates has no bound, so there is no oracle divergence to record, only a YSharp limit.
- YRelay gained TryPrepareUpdate/Commit (two-phase, version-checked) so the mailbox can check before it persists.
- The mailbox appended to the store before the relay and Doc applied, so the log could hold an update the relay or Doc refused, and replay would differ. SRV-8 is now: limits -> hook -> relay prepare -> hot Doc apply -> store append -> relay commit -> broadcast. A partial Doc reject or a failed append rebuilds the hot Doc from the relay.
- Awareness messages were relayed without being parsed; garbage now closes the sender with ProtocolError and reaches no one.

AC #3: no bug in the core's apply/merge was found (all core outcomes stayed None/Malformed/LimitExceeded/Rejected without faults); the relay fixes above are server-tier design gaps, no oracle evidence needed.

Owner flags:
- RLY-7 and the SRV-8 reorder are new guarantees in ADR 0024; please confirm.
- The hot Doc runs ahead of the log between apply and append (rolled back on failure); matters for TASK-5.8 observers.
- A relay-only document (no hook) passes on structs a Doc would reject (a parent that is not a type); unchanged from TASK-5.2, matches y-websocket's relay behaviour.
- Each relay fold is O(state) in time and allocation (merge rebuilds the list), so a stream is quadratic; the fuzz guard allows 128 bytes per state byte. Possible follow-up.
- The API surface baseline (tools/api-surface) is stale since TASK-5.2 (YRelay, YUpdate.TryDecode, YSync span overloads, now TryPrepareUpdate/Commit/PreparedUpdate).

Suite changes:
- tests/YSharp.Fuzz.Tests: Guards/RelayHostile.cs, Tests/RelayHostileProperties.cs, Tests/ServerTierBoundsTests.cs (new); csproj references YSharp.Server.
- tests/YSharp.Tests/Codecs/RelayTests.cs: pending bound at the limit and one over (V1/V2), pending deletes, prepare/commit.
- tests/YSharp.Server.Tests/Mailbox/MailboxTests.cs: valid presence JSON; malformed awareness theory.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fuzz targets now cover YRelay (ADR 0013 byte and model layers under default and tiny limits) and the mailbox entry point (hostile Update/Step2/awareness/raw messages next to a good peer, with restart over the same store), with post-conditions that nothing stays rented (BufferPool.Outstanding) or retained (weak reference) after a rejected update. Stream tests bound relay memory under huge clocks, missing dependencies, many client ids and amplification. Findings fixed: relay pending was unbounded (new RLY-7, MaxPendingBytes, two-phase TryPrepareUpdate/Commit), the mailbox persisted before the relay/Doc checks (SRV-8 reordered; hot Doc rebuilt from the relay on a partial reject or failed append), and malformed awareness was relayed (now closes the sender). No core apply/merge bug found. Full suite: 4676 total, 0 failed. Owner flags are in the notes: RLY-7 and the SRV-8 reorder, hot Doc briefly ahead of the log, relay-only docs pass Doc-invalid structs, O(state) folds, stale API baseline.
<!-- SECTION:FINAL_SUMMARY:END -->
