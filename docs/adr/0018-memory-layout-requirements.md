# Memory layout is bounded by invariants and a spike gate, not by a prescribed design

ADR 0004 makes GC pressure the main risk of a pure C# implementation and ADR 0009 measures it. This ADR states what the implementation must satisfy so the budgets can be met, without choosing the data structures. A requirement here is something a test, a counter or a review can check.

## Storage model

The implementation chooses how items are represented (an object per item, or structs addressed by index in arenas). It must satisfy these invariants either way:

- **Chunked storage.** No growing structure the document owns, such as a client's struct list, a content pool or the delete set, is one contiguous allocation larger than about 64 KB. It grows by adding chunks, so steady-state edits never grow the large object heap.
- **No per-item machinery.** An item carries no finalizer, lock, event object, delegate or closure. Neighbour and parent links, origin ids and the parent sub-key do not require a separate heap object per link.
- **Document-owned memory.** All memory held for a document is reachable only from that document. There are no finalizers, no static or global caches keyed by document, and no timer or handler that roots a document after it is destroyed. Destroying a document releases every chunk and unsubscribes everything it subscribed (the JS leak in PUD-05 is not reproduced).
- **Declared exceptions.** One caller-supplied payload larger than the chunk size (a single big binary or string) may be one allocation. The LOH allowance in ADR 0009 covers it and nothing else.

## Content

- Text and binary content are slices of shared chunked buffers, so splitting an item when an update integrates in the middle of it allocates nothing and copies nothing.
- Decoded `YValue` and Any payloads are copied once into document-owned storage and never alias caller buffers (ADR 0007).
- Deleted content shrinks to the minimal form the wire needs, and its payload storage is released. A garbage-collected struct keeps only what encoding a state vector and a delete set requires.

## Transient memory

- Decode and encode use rented buffers and writers. Transactions, event objects and dispatch lists are reused per document or per thread (ADR 0008).
- Pending-struct queues and every other transient structure are bounded by `YLimits` (ADR 0005). Transient growth is proportional only to input size.
- Every rented buffer is returned on every exit path, including decode errors, rejected structs and faulted documents. The hostile-input post-condition (ADR 0013) checks that nothing stays rented after a failed apply.

## Not required

Compaction of fragmented chunks and a fixed per-item byte count are not requirements. The bytes-per-item ceiling in ADR 0009 is the only footprint gate, and a heavy-delete fragmentation workload can be added later if the slow tier shows a problem.

## How the budgets gate implementation

1. **Spike gate (M0).** Before decode and integrate are built on a store, a spike of the chosen storage model runs the three slow-tier workloads (1M text inserts, 1M array pushes, 100k map keys) and the steady-state check (zero gen2 collections, no LOH growth over 100k edits). Bytes per item must be at or below the Yjs figure. The choice between object-per-item and indexed arenas is recorded in a follow-up ADR with those measurements. Nothing builds on a store that fails.
2. **Milestone gate.** The milestones are: store and integrate, V1 codec, V2 codec, shared types, transactions and events, awareness and sync. A milestone is done only when its hot paths from ADR 0009 are in `AllocationBudgets` with measured values (plus 25%), the zero-byte paths read 0, and the slow memory tier is green. Budgets only move down after that.
3. **Pending hot paths.** A hot path whose milestone is still open may be pending. Pending paths are counted and the count never grows, like pending registry entries (ADR 0010). No milestone closes with a pending path of its own.
4. **Layout changes.** Changing the storage model after M0 reruns the spike workloads and is a reviewed change with a reason.
