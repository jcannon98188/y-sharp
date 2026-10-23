# y-protocols v1.0.7: behaviour inventory (awareness, sync, auth)

Research for TASK-1.6 (feeds TASK-1.17 awareness/presence test scope). Planning only.

Primary sources, all under `y-protocols/` (submodule, v1.0.7 per `package.json:3`):

| File | Lines | Role |
|---|---|---|
| `awareness.js` | 295 | Awareness CRDT class and encode/apply helpers |
| `sync.js` | 134 | Sync step 1 / step 2 / update messages |
| `auth.js` | 33 | Permission-denied message |
| `awareness.test.js` | 53 | The only test (one test case, `testAwareness`) |
| `test.js` | 19 | Runner. Registers only `awareness` |
| `PROTOCOL.md` | | Prose spec. Partly wrong, see section 5 |

Helper semantics were checked in `lib0/` (`time.js:22`, `function.js:70` `equalityDeep`, `observable.js:80,152` `emit`/`destroy`). Public entry points are `./sync`, `./awareness`, `./auth` (`package.json` `exports`). `.d.ts` files are generated and not in the submodule.

Behaviour IDs: `AW-` awareness, `SY-` sync, `AU-` auth, `PR-` protocol-doc discrepancies. "Quirk" means behaviour that looks unintended but is the oracle's observable behaviour. A faithful port must match it, or the divergence must be an explicit decision.

Byte examples marked (verify) were derived by reading the code, not run. Confirm them against the JS oracle when the tests are written.

---

## 1. Awareness

### 1.1 Constants and data model

- AW-1. `outdatedTimeout = 30000`, in milliseconds (`awareness.js:13`). `time.getUnixTime` is `Date.now` (`lib0/time.js:22`), so every timestamp and clock-age comparison is in ms.
- AW-2. `Awareness` extends lib0 `Observable`. Event names are `change`, `update`, `destroy` (`:39,132,134,185,186,286,291,80`).
- AW-3. Fields: `doc`, `clientID` (copied from `doc.clientID` at construction; later changes to `doc.clientID` are not followed, `:45-49`), `states: Map<number, object>`, `meta: Map<number, {clock, lastUpdated}>`, `_checkInterval` (`:50-77`).
- AW-4. `states` holds only live states. `meta` holds a clock for every client ever seen, including removed ones. Removed clients keep their meta (tombstone clock), which is what stops stale updates from resurrecting them.
- AW-5. State values are JSON-compatible objects. Wire format is `JSON.stringify`, so values that do not round-trip lose information. `undefined` fields are dropped, `NaN` becomes `null`, and `Date` becomes a string. Behaviour after encode/apply, not the in-memory value, is what peers see.

### 1.2 Construction and local state

- AW-6. The constructor calls `setLocalState({})` (`:81`). This leaves local clock `0`, local state `{}`, and emits `update` with `added:[clientID]` and `change` with `added:[clientID]`. No listener can exist yet, so nothing is observed. The test relies on this: "the local client is already marked as available" (`awareness.test.js:32`). First observable `setLocalState` therefore yields clock `1` (test `:30`).
- AW-7. `getLocalState()` returns `states.get(clientID) || null` (`:95`). It is `null` after the local state is removed.
- AW-8. `setLocalState(state)` (`:101-135`):
  - The new clock is `0` if there is no local meta, else `meta.clock + 1`. The clock increments on every call, including a call with a deep-equal state (test `:45-47`: clock goes 1, 2, 3).
  - `meta.lastUpdated` is set to now.
  - `state === null` deletes the local state and reports `removed:[clientID]`.
  - Previous state `null`/`undefined` and new non-null reports `added:[clientID]`.
  - Otherwise `updated:[clientID]`, and `filteredUpdated` only if `!equalityDeep(prev, new)`.
- AW-9. Event split (`:131-134`). `change` fires only if `added`, `filteredUpdated` or `removed` is non-empty, and carries `updated: filteredUpdated`. `update` always fires, with the unfiltered `updated`. Both pass origin `'local'`. Setting an identical state therefore emits `update` (heartbeat) but no `change` (test `:45-48`).
- AW-10. Quirk: `setLocalState(null)` when already `null` still reports `removed:[clientID]`, emits both events, and bumps the clock (`:119-120`).
- AW-11. Quirk: `setLocalState` stores the caller's object by reference, and `getStates()` returns the live internal map (`:154`). The previous-state comparison uses that stored reference, so mutating a state object in place and then setting it again yields "not changed" (`equalityDeep(x, x)`).
- AW-12. `setLocalStateField(field, value)` (`:141`) is a no-op if the local state is `null`. Otherwise it calls `setLocalState({...state, [field]: value})`. This is a shallow merge, it bumps the clock, and the new object is always a different reference.
- AW-13. `getStates()` returns the same Map instance each time (`:154`).

### 1.3 Timers and timeout

- AW-14. The check interval is `floor(outdatedTimeout/10) = 3000` ms (`:77`). It is created in the constructor and cleared in `destroy`.
- AW-15. Local renewal (`:61-64`). If the local state is non-null and `now - meta(local).lastUpdated >= 15000`, then `setLocalState(getLocalState())`. This bumps the clock and emits `update`/`change` with origin `'local'`. It is a heartbeat, so `change` carries `updated:[]` and therefore does not fire.
- AW-16. Remote expiry (`:69-76`). For every meta entry with `clientid !== this.clientID`, `now - lastUpdated >= 30000` and `states.has(clientid)`, collect it. If any were collected, call `removeAwarenessStates(this, ids, 'timeout')` once with all of them.
- AW-17. Boundaries are inclusive (`<=`), so exactly 15000 ms and 30000 ms trigger. With a 3 s tick, the effective expiry is 30 to 33 s after the last accepted update. Tests need a fake clock and fake timer. Port design needs an injectable clock and scheduler.
- AW-18. A removed remote client has `states.has == false`, so it is not removed again. Its meta stays, still with the old clock.
- AW-19. The local client is never expired by the interval, only renewed.
- AW-20. Quirk: if local state is non-null but `meta.get(clientID)` is missing, the interval would throw (non-null assertion at `:61`). This cannot occur through the public API.
- AW-21. The interval keeps a Node process alive (no `unref`). `destroy` must be called.

### 1.4 destroy

- AW-22. `destroy()` (`:84-89`), in order:
  1. emit `destroy` with `[awareness]`;
  2. `setLocalState(null)`, which emits `change` and `update` with `removed:[self]`, origin `'local'`;
  3. `Observable.destroy()`, which drops all listeners;
  4. `clearInterval`.
  Listeners therefore see `destroy` and then the final removal, which providers use to broadcast the offline message. The removal event reaches listeners; after step 3 there are none.
- AW-23. `doc.on('destroy', () => this.destroy())` is registered in the constructor (`:78`). Destroying the Y.Doc destroys the awareness.
- AW-24. Quirk: `destroy` is not idempotent. A second call emits the removal path again (see AW-10), with no listeners.

### 1.5 removeAwarenessStates(awareness, clients, origin)

- AW-25. For each id in `clients` that is currently in `states` (`:169-182`): delete the state and add it to `removed`. Ids not in `states` are silently skipped.
- AW-26. If the removed id is the local client, `meta` is replaced with `{clock: clock+1, lastUpdated: now}`. Remote clients keep their meta untouched, including clock and lastUpdated. A later remote update must have `clock > old clock` to revive them (or an equal-clock null to re-remove, which is a no-op).
- AW-27. If `removed` is non-empty, emit `change` then `update`, both `{added:[], updated:[], removed}` with the supplied origin (`:183-186`). Nothing is emitted if nothing was removed.
- AW-28. Duplicates in `clients`: the second occurrence is skipped because the state is already gone. The local client is therefore bumped only once.
- AW-29. The function does not itself produce a network message. Providers react to the `update` event. The local-clock bump in AW-26 is what makes the null state broadcastable and accepted by peers (clock newer).

### 1.6 encodeAwarenessUpdate(awareness, clients, states = awareness.states)

- AW-30. Wire format (`:194-207`; PROTOCOL.md "Awareness protocol"):
  ```
  varUint(count) · per client: varUint(clientID) · varUint(clock) · varString(JSON.stringify(state))
  ```
  `state` is `states.get(id) || null`, so a client with no state is encoded as the JSON text `null`. The clock is read from `awareness.meta`.
- AW-31. Entries are written in the order given in `clients`. Duplicates are written twice. An empty `clients` yields the single byte `[0]`.
- AW-32. A client id absent from `meta` throws a `TypeError` (`:201`, unguarded `.clock`). A client present in `meta` but absent from `states` is encoded as `null` with its tombstone clock. This is how removals are broadcast.
- AW-33. The optional third argument `states` substitutes the state map but not the meta. It is public API, though undocumented, and unused in the repo.
- AW-34. Client ids are `varUint` (lib0 53-bit). Yjs ids are random uint32, so ids at or above 2^31 must encode correctly (multi-byte varint, no sign issues). Clocks have the same width.
- AW-35. Strings are UTF-8 with a varUint byte-length prefix (not a char count), so non-ASCII state content needs a byte-length test.

### 1.7 modifyAwarenessUpdate(update, modify)

- AW-36. Decodes the whole update, runs `modify(state)` on each decoded state (`:219-234`), and re-encodes with the same ids and clocks. Intended for servers that rewrite identity fields.
- AW-37. `modify` is also called for `null` states (removals), so the callback must tolerate `null`.
- AW-38. If `modify` returns `undefined`, `JSON.stringify` yields `undefined` and `writeVarString` fails. Returning `null` is the valid way to blank a state.
- AW-39. Trailing bytes after the declared entries are ignored. Output is canonical, so re-encoding re-serialises JSON (key order follows the object, whitespace is dropped).
- AW-40. An identity `modify` gives a byte-identical result for input produced by `encodeAwarenessUpdate`.

### 1.8 applyAwarenessUpdate(awareness, update, origin)

Per entry (`:249-284`):

- AW-41. Decode `clientID`, `clock`, `state = JSON.parse(string)`. Read `clientMeta`, `prevState`, and `currClock = clientMeta ? clientMeta.clock : 0` (unknown client counts as clock 0).
- AW-42. Acceptance rule (`:256`): `currClock < clock` OR (`currClock === clock && state === null && states.has(clientID)`). Everything else is ignored: no state change, no meta change, no lastUpdated refresh, no event contribution.
- AW-43. Consequence (quirk, matters for the port): the first message from an unknown client with clock 0 is ignored, because `0 < 0` is false. Only clock >= 1 introduces a new client. The reference test shows this: the constructor's clock-0 state was never delivered and the first applied clock is 1 (`awareness.test.js:30`).
- AW-44. Equal-clock non-null update is ignored. Equal-clock `null` removes only if the client is currently in `states` (this is how a peer can say "I think X is offline" without bumping X's clock). Lower clock is ignored.
- AW-45. When accepted and `state === null`:
  - If `clientID === awareness.clientID` and the local state is non-null, the removal is refused. The local state is kept and `clock` is incremented by one before storing (`:259-263`). The meta is stored with this bumped clock, so the `update` event the provider sees carries the live state at clock+1, which re-asserts presence to peers.
  - Otherwise `states.delete(clientID)`.
- AW-46. When accepted and non-null, `states.set(clientID, state)`. Note this also allows a remote update to overwrite the local client's own state. There is no origin or ownership check, which is why `modifyAwarenessUpdate` exists for servers.
- AW-47. On acceptance, `meta` is set to `{clock, lastUpdated: timestamp}`. `timestamp` is read once at the start of the call and shared by all entries.
- AW-48. Classification of an accepted entry (`:273-282`), tested in this order:
  1. `clientMeta === undefined && state !== null` goes to `added`.
  2. `clientMeta !== undefined && state === null` goes to `removed`.
  3. `state !== null` goes to `updated`, and also to `filteredUpdated` if `!equalityDeep(state, prevState)`.
  4. `clientMeta === undefined && state === null` goes nowhere.
- AW-49. Quirks following from the classification:
  - Reviving a client whose state was removed but whose meta remains (after remote null or timeout) is reported as `updated`, not `added`. `prevState` is `undefined`, so it is also in `change.updated`.
  - A null with a higher clock for a client already absent from `states` is still reported in `removed`.
  - The local-removal refusal of AW-45 is still reported as `removed` (meta exists), although the state was kept.
  - A null for a client never seen creates a tombstone meta (clock stored) with no event. A later non-null update must have a higher clock.
- AW-50. Events after the loop (`:285-294`). `change` is emitted if `added`, `filteredUpdated` or `removed` is non-empty, with `{added, updated: filteredUpdated, removed}`. `update` is emitted if `added`, `updated` or `removed` is non-empty, with the unfiltered `updated`. Both carry the supplied `origin`. `change` fires before `update`. A same-state update with a newer clock gives `update` only. A stale or empty batch gives no events.
- AW-51. Error behaviour: there is no atomicity. A truncated update, or invalid JSON (`SyntaxError`), throws mid-loop. Earlier entries are already applied to `states`/`meta`, but no event is emitted for them. Trailing bytes after `len` entries are ignored. A count larger than the entries present throws on the missing varint.
- AW-52. A repeated identical `(id, clock)` update is idempotent (equal clock non-null is ignored). Delivery order does not matter between different clients, and for one client the highest clock wins.

### 1.9 Existing test (`awareness.test.js`) and what it covers

Single test `testAwareness`: two awareness instances with fixed clientIDs 0 and 1; aw1's `update` is piped as `encode(added+updated+removed)` into `apply(aw2, enc, 'custom')`.

| Lines | Asserts | Behaviours |
|---|---|---|
| 28-34 | Set `{x:3}`: aw2 has `{x:3}`; `aw2.meta.get(0).clock === 1`; aw2 `change.added == [0]`; local change `{added:[], updated:[0], removed:[]}` | AW-6, 8, 9, 30, 42-43, 48(1) |
| 38-41 | Set `{x:4}`: aw2 updated; local change equals remote change | AW-9, 48(3), 50 |
| 45-48 | Set same `{x:4}`: no `change` on aw2; clock now 3; local change null as well | AW-8, 9, 50 |
| 49-52 | `setLocalState(null)`: aw2 `change.removed.length == 1`; `aw1.getStates().get(0)` is undefined; changes equal | AW-8, 30, 32, 45 |

Not covered by any test: timeout and renewal (AW-14 to 20), `destroy` (22-24), `removeAwarenessStates` (25-29), `modifyAwarenessUpdate` (36-40), local-removal refusal (45), tombstones and stale clocks (42-44, 49), `setLocalStateField` (12), error and truncation handling (51), unicode and multi-client batches (31, 35), and the `update`-versus-`change` difference for heartbeat (9, 50). `test.js:11-13` runs only this file.

Note: the test never tests origin passthrough beyond passing `'custom'`; it does not assert that origin.

---

## 2. Sync protocol (`sync.js`)

### 2.1 Constants and framing

- SY-1. `messageYjsSyncStep1 = 0`, `messageYjsSyncStep2 = 1`, `messageYjsUpdate = 2` (`:38-40`). A message is `varUint(type) · varUint8Array(payload)` (length-prefixed byte array). There is no room name, since the upper layer handles that (`:33`). The outer `[0, ...]` sync versus `[1, ...]` awareness envelope belongs to providers, not to this package (see PR-2).
- SY-2. All payloads are Yjs v1 encoding (`Y.encodeStateVector`, `Y.encodeStateAsUpdate`, `Y.applyUpdate`). This package has no v2 variant.

### 2.2 Writers

- SY-3. `writeSyncStep1(encoder, doc)` (`:48`) writes `[0, len, ...Y.encodeStateVector(doc)]`. For an empty doc this should be `[0, 1, 0]` (verify).
- SY-4. `writeSyncStep2(encoder, doc, encodedStateVector?)` (`:59`) writes `[1, len, ...Y.encodeStateAsUpdate(doc, sv)]`. With `sv` undefined it contains the full document state. With an `sv` it contains only the structs the remote lacks, plus the complete delete set. For an empty doc this should be `[1, 2, 0, 0]` (verify).
- SY-5. `writeUpdate(encoder, update)` (`:96`) writes `[2, len, ...update]` and does not validate the update bytes.
- SY-6. Writers append to the encoder and may be combined into one encoder (for example envelope type first, then these). They never create a new encoder.

### 2.3 Readers

- SY-7. `readSyncStep1(decoder, encoder, doc)` (`:71`) reads the remote state vector and appends a SyncStep2 containing `encodeStateAsUpdate(doc, sv)` to `encoder`. It does not touch the doc. The JSDoc for the two parameters is swapped (decoder "reply", encoder "received"); the code is correct. An invalid state vector throws from the Yjs decoder, uncaught.
- SY-8. `readSyncStep2(decoder, doc, transactionOrigin, errorHandler?)` (`:82-90`) reads a `varUint8Array` and `Y.applyUpdate(doc, update, transactionOrigin)` inside try/catch. On any error (including a truncated length prefix, since the read is inside the `try`, and exceptions thrown by doc event observers) it calls `errorHandler(error)` if given and always `console.error('Caught error while handling a Yjs update', error)`. The error is swallowed and the function returns normally.
- SY-9. `readUpdate` is the same function object as `readSyncStep2` (`:109`), so SyncStep2 and Update are behaviourally identical on receipt.
- SY-10. `transactionOrigin` is passed to `applyUpdate` and so is visible to `update`/`afterTransaction` observers and to `Transaction.origin`. The test helper uses it to avoid echoing (`yjs/tests/testHelper.js:88-92,195`).
- SY-11. `readSyncMessage(decoder, encoder, doc, transactionOrigin, errorHandler?)` (`:118-134`) reads `varUint` type and dispatches to the matching reader. It returns the message type, so callers can tell which reply or application occurred. Unknown type throws `Error('Unknown message type')`; the type varint has been consumed and nothing else is read. It is the only reader that appends a reply, and only for type 0.
- SY-12. Whether a reply was produced is detected by callers via `encoding.length(encoder) > 0` (`testHelper.js:196`). Step 2 and Update never write a reply.

### 2.4 Protocol flows (documented in JSDoc `:14-28`; the package does not implement them, so they are integration-level test material)

- SY-13. Peer to peer: both sides send Step1; each replies with Step2; after that each side forwards its local `update` events as Update messages. Client-server: client sends Step1; server replies Step2 then Step1; client replies Step2. The package gives no SyncDone message.
- SY-14. Convergence property: A applies B's Step2 (answer to A's Step1) and vice versa, after which `encodeStateAsUpdate` of both are equal. Interleaved updates between Step1 and Step2 must still converge, because Step2 is computed from the sender's current state.
- SY-15. The `yjs` tests drive this protocol for every multi-client test via `TestConnector` (`yjs/tests/testHelper.js:5,89-91,113-122,192-198`: `writeUpdate`, `writeSyncStep1`, `readSyncMessage`). All Yjs convergence tests therefore also implicitly exercise sync.js. There is no standalone sync test in y-protocols.

### 2.5 Edge cases worth specifying

- SY-16. Step1 with state vector `[0]` (empty) yields the full document. Step1 with a vector that is ahead of the doc yields an empty update (`[0,0]`), not an error.
- SY-17. Step2 or Update on a doc with missing dependencies leaves structs pending (Yjs `pendingStructs`) and does not throw. A later update completes them.
- SY-18. Applying the same Step2/Update twice is idempotent.
- SY-19. Corrupt update bytes go to the error handler and `console.error`, not to the caller. The port must decide what the equivalent of `console.error` is (logger abstraction); the observable contract is "handler called, no throw".
- SY-20. A message with a valid type but missing payload bytes: for type 0 the error propagates (the read is outside the try); for types 1 and 2 it is swallowed (inside the try). This asymmetry is observable.

---

## 3. Auth (`auth.js`)

- AU-1. One message type: `messagePermissionDenied = 0` (`:6`).
- AU-2. `writePermissionDenied(encoder, reason)` (`:12`) writes `varUint(0) · varString(reason)` (UTF-8, length-prefixed). For reason `"no"` this should be `[0, 2, 110, 111]` (verify). Empty reason is `[0, 0]`.
- AU-3. `readAuthMessage(decoder, y, handler)` (`:29`) reads the type. For type 0 it reads the reason string and calls `handler(y, reason)`. The handler's return value is discarded and the function returns `undefined`.
- AU-4. Quirk: unknown message types are silently ignored (a `switch` with no default). Nothing further is consumed, so no error and the decoder is left after the type varint.
- AU-5. A `null`/`undefined` handler throws a `TypeError` when type 0 is read. A truncated reason string throws from the decoder.
- AU-6. `y` is only passed through to the handler and is never used. The type annotation says `Y.Doc`, but any value works.
- AU-7. No tests exist for this module (`test.js` registers only awareness). It is not referenced from `yjs/`. Treat it as trivial protocol coverage: round-trip, UTF-8 reason, unknown type ignored, handler arguments.

---

## 4. Test-design implications

1. Awareness needs a controllable clock (`getUnixTime`) and a controllable timer (the 3000 ms interval) injected into the port. Without that, AW-14 to AW-20 are untestable without real waits.
2. The highest-value awareness differential tests are the quirks, because the single existing test does not pin them down: AW-43 (clock-0 first message ignored), AW-45 (local removal refused, clock+1), AW-49 (revive reported as `updated`; spurious `removed`; local refusal reported as `removed`), AW-9/50 (`update` versus `change` for heartbeats), AW-10 (null-to-null still reports removal), AW-26 (only the local clock is bumped on removal).
3. Awareness wire bytes can be tested as golden vectors generated from the JS oracle (multi-client, large ids, unicode, null states, empty list). `modifyAwarenessUpdate` is the same with a callback.
4. Sync wire tests: golden bytes for the three message types for empty and non-empty docs, plus reader behaviour (reply produced only for Step1, error swallowing, unknown type, return values), plus a two-peer convergence harness mirroring `TestConnector`.
5. Auth: tiny golden-byte set plus handler dispatch.
6. Out of scope for this package (belongs to providers): outer message framing, SyncDone, read-only filtering (PROTOCOL.md "Handling read-only users"), awareness over transport.

## 5. Protocol-doc discrepancies (PROTOCOL.md versus code)

- PR-1. PROTOCOL.md's example calls `awareness.readAwarenessMessage` and lists it in `messageTypes`. No such export exists in v1.0.7 (`awareness.js` exports `outdatedTimeout`, `Awareness`, `removeAwarenessStates`, `encodeAwarenessUpdate`, `modifyAwarenessUpdate`, `applyAwarenessUpdate`). Providers wire `applyAwarenessUpdate` themselves.
- PR-2. The example object uses `[SyncProtocolMessageType]` twice as a key, which is a typo for `AwarenessProtocolMessageType`. The envelope constants `SyncProtocolMessageType = 0` and `AwarenessProtocolMessageType = 1` are only in the docs, not in code.
- PR-3. The doc says the updater "must only apply updates if the received clock is newer / larger", which omits the equal-clock-null removal exception (AW-42/44) and the local-removal refusal (AW-45). The code is the authority.
- PR-4. The doc says clients not updated for "longer than 30 seconds" are removed. The code uses `>= 30000` ms on a 3 s tick (AW-16/17).
- PR-5. The doc says that after receiving SyncStep2 "a client knows that it is now synced". The code gives no sync-state tracking; that is the provider's job.
- PR-6. The read-only filtering advice (block `[0,1,..]` and `[0,2,..]`) assumes the outer envelope byte `0`. It has no code in this package.

## 6. Open questions for later tickets

- The exact `equalityDeep` semantics (`lib0/function.js:70`, which also compares `constructor`) decide which awareness state changes count as `change`. This belongs in the lib0 inventory; for JSON-only state it needs object (key order independent?), array, number, string, boolean, null cases.
- The byte examples marked (verify) should be generated from the oracle once the golden-vector tooling decision is made.
- Whether the port keeps JSON strings for state or exposes a typed API is a design question for TASK-1.17.
