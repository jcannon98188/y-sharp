# Glossary

**Oracle** — The pinned reference implementation (Yjs v13.6.33 with lib0 v0.2.99 and y-protocols v1.0.7) that y-sharp must match.

**Update** — A binary message that carries document changes between replicas, in V1 or V2 encoding.

**State vector** — A per-client summary of how much of the document a replica has seen.

**Fixture** — A checked-in record of operations plus the oracle's resulting update bytes and document state, replayed by xUnit.

**Differential harness** — A test that runs the oracle live and compares its output with y-sharp's.

**Behaviour inventory** — The list of oracle behaviours, each with an ID, that every test must trace to.

**Presence** — The awareness feature of y-protocols: ephemeral per-client state shared between replicas.

**Divergence** — A deliberate, recorded departure from oracle behaviour.

**Decode error** — A failure to read an update's bytes into structs, as opposed to a failure to apply well-formed structs. A decode error changes nothing.

**Rejected struct** — A well-formed struct that cannot be integrated, for example because its parent is not a type. It is dropped with its dependents and never retried.

**Faulted document** — A document in which an internal invariant was found broken. It stays readable but refuses further changes.

**Transaction** — A scope that holds a document's lock while changes are made or read, and commits them when it ends.

**Implicit transaction** — A transaction opened automatically around a single call made without an explicit one.

**Thread-affine** — Must be finished on the same thread that started it.

**Observer fault** — An exception thrown by an observer handler, contained and reported instead of propagated.

**Wire equality** — Equality of two values by what they would encode as, so NaN equals NaN and positive zero differs from negative zero.

**Attribute equality** — The oracle's looser comparison used to decide whether a text format changes anything: NaN is unequal to itself, the two zeros are equal, and nested values are equal only if they are the same instance.

**Hot path** — An operation named in the test plan because its memory behaviour is asserted, not just measured.

**Allocation budget** — The most memory a hot path may allocate per operation; zero for the paths that must not allocate.

**Registry entry** — One assertable behaviour in the behaviour inventory, with a permanent ID.

**Feature** — A capability of the library the size of one implementation spec, such as text or V2 encoding. A test names the features it needs and is skipped until they have landed.

**Landed feature** — A feature whose implementation is in, so the tests that need it must pass. A missing implementation there is a failure, not a skip.

**Pending entry** — A registry entry whose tests have not been written yet; allowed during build-up and counted, never growing.

**Ops script** — The declarative list of steps in a fixture that both the oracle and y-sharp replay to reach the same document.

**Observation** — One named output a fixture records from the oracle after its ops script runs, such as update bytes or a document dump.

**Oracle-misbehaved** — The outcome when the oracle itself crashes or hangs on a generated input. It is counted and reported, not a failure of y-sharp.

**Delivery schedule** — The part of an ops script that says which updates reach which replica, in what order, with duplicates and drops.

**Hostile class** — One named family of malicious or corrupt input that the suite attacks, such as truncation or oversized lengths.

**Hostile corpus** — The checked-in set of minimised hostile inputs, replayed on every commit.

**Post-condition** — What must hold after any hostile input: the document is not faulted, can still be encoded, and still accepts a good update.

**Canary** — A small separate process that runs only the cases able to overflow the stack, so a crash is reported as a named failure.

**Heartbeat** — A presence update that re-sends an unchanged state to show the client is still there.

**Presence expiry** — Dropping a remote client's presence when no update has arrived for the timeout.

**Structural equality** — Comparison of two values by content, ignoring object key order, used to decide whether a presence state changed.

**Seed list** — The committed seeds `1..N` that a randomized test runs in the gating tier, where N depends on its size class.

**Size class** — A band of upstream operation counts that fixes how many seeds a randomized test runs in gating.
