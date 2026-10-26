---
id: TASK-5.8
title: '8. Hot-Doc lifecycle: server-side edits and reads, idle eviction'
status: To Do
assignee: []
created_date: '2026-10-26 21:28'
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
- [ ] #1 A host can post an edit and a read to a document; the edit is persisted and broadcast like a peer update, and the Doc is materialized only on demand
- [ ] #2 An idle document with no connections drops its hot Doc and then its relay state after configurable times, and is rebuilt from storage on the next open with no lost update
- [ ] #3 Tests cover edit, read, eviction during and after traffic, and reopen after eviction (SRV-5, SRV-18, SRV-20)
<!-- AC:END -->
