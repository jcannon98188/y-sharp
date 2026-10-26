---
id: TASK-5.8
title: '8. Hot-Doc lifecycle: server-side edits and reads, idle eviction'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 21:28'
updated_date: '2026-10-26 22:12'
labels:
  - server
dependencies:
  - TASK-5.6
parent_task_id: TASK-5
ordinal: 64000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TASK-5.6 built the per-document mailbox (src/YSharp.Server/DocumentMailbox.cs) with a hot Doc held only when a validation hook is registered, rehydrated from storage before the first message (ADR 0024 SRV-18, SRV-19). Still missing from ADR 0024: (1) SRV-5 / SRV-18: a host posts a server-side edit or read to a document's mailbox (materializing the Doc on demand); an edit must go through persist and broadcast like a peer's update, and a read runs in a read transaction on the worker; (2) SRV-18: an indexing subscriber that asks for the Doc; (3) SRV-20: idle eviction of the hot Doc after a configurable time with no connections and no queued messages, and eviction of the relay state of a document with no connections, rebuilt from storage on next open. Today an opened document's mailbox lives until the server shuts down.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A host can post an edit and a read to a document; the edit is persisted and broadcast like a peer update, and the Doc is materialized only on demand
- [x] #2 An idle document with no connections drops its hot Doc and then its relay state after configurable times, and is rebuilt from storage on the next open with no lost update
- [x] #3 Tests cover edit, read, eviction during and after traffic, and reopen after eviction (SRV-5, SRV-18, SRV-20)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Host work as mailbox envelopes (MailboxWork): EditAsync, ReadAsync, SubscribeAsync on YServer, run on the worker; Doc materialized from the relay on demand.
2. Attach counting under the server lock (connections, posted work, subscribers) so eviction never races a new user.
3. Idle eviction on a TimeProvider: hot Doc after HotDocumentIdleTimeout, mailbox and relay after DocumentIdleTimeout.
4. ADR 0024 SRV-5/SRV-20 wording, registry rows, docs/server.md, tests with VirtualTimeProvider.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design:
- YServer.EditAsync(docId, Action<Transaction>, origin) -> bool (false when nothing changed); ReadAsync<T>(docId, Func<Doc,T>); SubscribeAsync(docId, Action<Doc>) -> IAsyncDisposable. Each opens the document if needed. The token only cancels the wait for mailbox room.
- An edit captures its V1 update from Doc.Update, converts to V2, relay prepare, append, commit, broadcast to every peer, notify subscribers. It skips the validation hook (server's own edit). On a throw or failed append the hot Doc is rebuilt from the relay and the caller gets the error.
- Subscribers run in a read transaction on the worker, once on subscribe and after every accepted update; a throw is swallowed.
- Mailbox.Attached counts connections, posted work and subscribers, under YServer's lock. TryEvict closes a mailbox only with nothing attached; otherwise the idle clock restarts.
- YServerOptions: HotDocumentIdleTimeout (default 1 min), DocumentIdleTimeout (default 5 min), TimeProvider.

Bug found and fixed (from TASK-5.6): YConnection.DisposeAsync closed the connection before the worker handled messages it had already accepted, and the worker dropped messages from closed connections, so an update sent right before a disconnect was lost. The worker now still handles a disposed connection's queued messages; only server-closed connections are cut off. Regression test AnUpdatePostedJustBeforeTheHostDisposesItsConnection_IsStillStoredAndSent.

Owner flags:
- Default timeouts (1 min hot Doc, 5 min document) are my pick; y-websocket drops a doc when the last client leaves.
- Server-side edits bypass the validation hook.
- A peer whose first Step2 answer repeats updates the server already has gets them appended to the log again (the relay dedups; y-websocket persists them too). Harmless but grows the log until compaction; could skip updates that add nothing.

Suite changes:
- tests/YSharp.Server.Tests/Mailbox/LifecycleTests.cs (new): edits, reads, failing edits, subscribers, dispose regression, idle eviction, hooked rebuild, subscriber hold, eviction racing traffic.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
A host can now edit, read and subscribe to a document through YServer.EditAsync, ReadAsync and SubscribeAsync. All three run on the document's mailbox and materialize the Doc from the relay only on demand. An edit is persisted and broadcast like a peer update, without the hook. An idle document with no connections or subscribers drops its hot Doc after HotDocumentIdleTimeout (1 min) and closes after DocumentIdleTimeout (5 min); attach counting under the server lock keeps eviction from racing new users, and the next use reloads from the store. Also fixed a TASK-5.6 bug: an update queued just before the host disposed its connection was dropped. ADR 0024 SRV-5/SRV-20 wording, registry rows SRV-5 and SRV-20, and docs/server.md updated. Full suite: 4686 total, 0 failed. Owner flags are in the notes: default timeouts, edits bypass the hook, duplicate Step2 content is re-appended to the log.
<!-- SECTION:FINAL_SUMMARY:END -->
