# Research: catalog of the existing JS tests (TASK-1.5)

Scope: every test in `yjs/tests/*.js` (Yjs v13.6.33, `yjs/package.json:3`) plus the y-protocols tests, the runner mechanics they depend on (`lib0/testing.js`, `lib0/prng.js`), and visible gaps against the source in `yjs/src` and `y-protocols`. Paths are relative to the repo root. `file:line` refs point at the export (the `export const testX` line) unless noted.

Primary sources only: the test files, `lib0` (v0.2.99, `lib0/package.json:3`) and the `src` trees in the submodules. Nothing here comes from docs or write-ups.

## 1. Summary

- **209 `test*` exports** in the yjs suite across 11 modules (`yjs/tests/index.js:27-29`), plus **1** in y-protocols (`y-protocols/awareness.test.js:9`). `testHelper.js` and `index.js` hold no tests. The task description says 13 test files; the directory has 13 `.js` files: 11 test modules, `testHelper.js` and `index.js`.
- Of the 209: **48 are named `testRepeat...` (randomized fuzz, time-repeated)**, 1 more is randomized but not repeated (`testInsertAndDeleteAtRandomPositions`), 5 are performance/benchmark style with no or few assertions, and the remaining ~155 are deterministic hand-written scenarios.
- Many tests (roughly 100, exact count not tallied) build their documents through the multi-client simulation (`init()` from `testHelper.js`). They end with `compare(users)` (convergence check) or explicit assertions.
- The suite proves **convergence of replicas inside one JS implementation** much better than it proves **bit/ordering compatibility with another implementation**. Only 3 tests (compatibility) pin exact bytes. See section 6.

| Module | File | Tests | Randomized | Notes |
|---|---|---|---|---|
| doc | `doc.tests.js` | 11 | 0 | Doc API, events, subdocs |
| map | `y-map.tests.js` | 41 | 16 | |
| array | `y-array.tests.js` | 42 | 16 | |
| text | `y-text.tests.js` | 44 | 16 + 1 | 2 giant fixture tests, 3 perf |
| xml | `y-xml.tests.js` | 11 | 0 | |
| encoding | `encoding.tests.js` | 4 | 0 | |
| undoredo | `undo-redo.tests.js` | 26 | 0 | |
| compatibility | `compatibility.tests.js` | 3 | 0 | Golden bytes from v13.2.0 |
| snapshot | `snapshot.tests.js` | 12 | 0 | |
| updates | `updates.tests.js` | 6 | 0 | |
| relativePositions | `relativePositions.tests.js` | 9 | 0 | |
| awareness (y-protocols) | `y-protocols/awareness.test.js` | 1 | 0 | |

(The 48 repeat tests: array 16, map 16, text 7 `RepeatGenerateTextChanges*` + 9 `RepeatGenerateQuillChanges*`. Array has 3 and map has 3 of them skipped unless `--production`.)

## 2. Runner and randomness mechanics

### 2.1 How tests are discovered and run
- `tests/index.js:27-29` assembles `{doc, map, array, text, xml, encoding, undoredo, compatibility, snapshot, updates, relativePositions}` and calls `runTests` (`index.js:36`). Process exits 0/1 on overall success (`index.js:38-40`). A `nodejs` module is commented out (`index.js:33`).
- `lib0/testing.js:571-598` `runTests`: iterates modules in object order, then exports in definition order. Only exports whose name starts with `test` or `benchmark` run (`testing.js:573`). Each runs once at this level (`repeatEachTest = 1`, `testing.js:586`).
- Test signature: `(tc: TestCase) => void | Promise`. Sync and async both supported (`testing.js:166-169`). Async tests in the suite: `testLoadDocsEvent`, `testSyncDocsEvent` (`doc.tests.js:277,293`), `testPermanentUserData` (`encoding.tests.js:46`), `testFormattingBug` (`y-text.tests.js:2253`), `testUndoDoingStackItem` (`undo-redo.tests.js:790`).
- Failure = any thrown error. `t.skip(cond)` throws a `SkipError` that is reported as skipped, not failed (`testing.js:184,201,625-633`). The `--production` flag (`lib0/environment.js:128`, `testing.js:61`) enables the heavy fuzz tests (guarded by `t.skip(!t.production)`).
- npm scripts (`yjs/package.json:16-17,23`): `test` = `NODE_ENV=development node dist/tests.cjs --repetition-time 50`; `test-extensive` = `--production --repetition-time 10000`; `preversion` = `--repetition-time 1000`. `NODE_ENV=development` matters: it turns on `isDevMode` (`y-array.tests.js:9`) which gates `testFailsObjectManipulationInDevMode`.
- y-protocols: `y-protocols/test.js:12-19` runs `{awareness}` through the same `lib0/testing` runner. `npm test` = lint + rollup + `node dist/test.cjs` (`y-protocols/package.json:18-20`). Note `awareness.test.js:4` imports `./awareness` (no `.js`) and `yjs` as a bare specifier, so it depends on the rollup build.

### 2.2 Seeds and the PRNG
- `TestCase` (`testing.js:68-122`): `seed` is lazily created at first access: `envSeed ?? random.uint32()` (`testing.js:101-108`). `envSeed` comes from the `--seed N` CLI/URL param (`testing.js:66`). `tc.prng = prng.create(seed)` (`testing.js:115-121`), i.e. one PRNG per test case, lazily created.
- Because `envSeed` is global, `--seed N` applies to every test in the run. Combined with `--filter "<regex>"` (matched against `[i/n] module: uncamelized name`, `testing.js:126-142`) a single failing test can be reproduced. On failure the runner prints exactly that command with `tc._seed` (`testing.js:194-195`). On success it prints the seed too.
- PRNG implementation: `prng.create(seed)` -> `Xoroshiro128plus` (`lib0/prng.js:27-35`, `lib0/prng/Xoroshiro128plus.js`). It is **not** textbook xoroshiro128+: it is a 32-bit-word variant seeded through `Xorshift32` (`Xoroshiro128plus.js:25-33`), and `next()` alternates between two output phases via a `_fresh` flag (`Xoroshiro128plus.js:36-60`), returning floats in [0,1) (first call after construction/each odd call: `((state[0]+state[2])>>>0)/(2^32)`, the other phase advances the xoroshiro state). A C# port that wants to replay JS seeds must reproduce this generator bit-for-bit, including the `_fresh` toggling and the `Uint32Array` wrap-around semantics. If the port instead uses its own PRNG, seeds are not portable and cross-implementation replay needs recorded op logs (see TASK-1.10/1.11).
- Helper draws used by the tests (`lib0/prng.js`): `int32/uint32/int31(gen,min,max)` = `floor(next()*(max+1-min)+min)` (inclusive both ends, `prng.js:76,86,98`); `bool = next() >= 0.5` (`prng.js:42`); `oneOf` = `array[int31(0,len-1)]` (`prng.js:171`); `letter` = a-z (`prng.js:116`); `word(gen,min=0,max=20)` = `len` draw then `len` letter draws (`prng.js:130`); `utf16Rune` = code point `int31(0,256)`, i.e. Latin-1 only, **no surrogates, no lone surrogates, no BMP above U+0100** (`prng.js:146`, the source itself says "produces invalid runes. Does not cover all of utf16"); `utf16String(gen,max=20)` (`prng.js:154`). Number and order of draws per op define the sequence, so draw order has to match if replaying seeds.

### 2.3 Repetition
- A test is "repeating" iff its **uncamelized name** (after stripping `test`) matches `/^(repeat|repeating)\s/` (`testing.js:131,148`). Exactly the `testRepeat*` tests.
- Repeating tests loop until wall-clock `--repetition-time` ms (default **50**, `testing.js:124`) have elapsed: `testing.js:164-181`. Each iteration calls `tc.resetSeed()` (`testing.js:177`), so **every repetition gets a fresh random seed** (unless `--seed` is set, in which case each repetition replays the same seed). Therefore the number of iterations is time-dependent and non-deterministic; with default settings a cheap test runs thousands of times, an expensive one once.
- On failure the loop stops and the seed of the failing iteration is printed (seed is read after the loop at `testing.js:194`; it is still the failing iteration's seed because `resetSeed` is only called on success).
- Non-`Repeat` tests that use `tc.prng` (every test that calls `init`, and `testInsertAndDeleteAtRandomPositions`) get a new seed per run but run only once.
- Module-level counters used for unique payloads are **not** reset between repetitions or tests: `_uniqueNumber` (`y-array.tests.js:513-514`) and `charCounter` (`y-text.tests.js:2304`). They affect payload values, not control flow, but mean `--filter + --seed` reproduces structure, not necessarily identical payload text.

### 2.4 `init()` and `applyRandomTests()` (`testHelper.js`)
- `init(tc, {users=5}, initTestObject?)` (`testHelper.js:268-298`):
  1. **Consumes one PRNG draw** (`prng.bool(gen)`, line 277) to choose V1 vs V2 encoding. **But both branches end in V1**: `useV2Encoding` logs "sync protocol doesnt support v2 protocol yet, fallback to v1 encoding" and sets V1 (`testHelper.js:57-61`). Also `useV1Encoding()` is called again at the end of `init` (line 296). Net: **randomized and simulation tests never exercise V2 on the wire**; only the draw is consumed.
  2. Creates a `TestConnector(gen)` and `users` `TestYInstance`s with `userID = i` and **`y.clientID = i`** (lines 285-288): client ids are 0..N-1, deterministic. This matters: conflict resolution tie-breaks by client id, so tests such as `testGetAndSetOfMapPropertyWithConflict` (`y-map.tests.js:202`, expects `'c1'`, the higher client wins) are deterministic.
  3. Each user gets `array<i>`, `map<i>`, `xml<i>` (`y.get('xml', Y.XmlElement)`) and `text<i>` roots named `array`, `map`, `xml`, `text` (lines 289-292). Because the root xml is an `XmlElement` created with default nodeName, `xml.toString()` yields `<undefined>...</undefined>` (asserted in `testUndoXml`, `undo-redo.tests.js:308`).
  4. `testConnector.syncAll()` (all connect and flush), then optional per-user `initTestObject`.
- `applyRandomTests(tc, mods, iterations, initTestObject)` (`testHelper.js:429-454`): fixed **5 users**. Loop per iteration, each branch re-draws:
  - `int32(0,100) <= 2` (~3%, comment says 2%): `bool` -> `disconnectRandom()` else `reconnectRandom()`;
  - else `int32(0,100) <= 1` (~1.4% of the remainder): `flushAllMessages()`;
  - else `int32(0,100) <= 50`: `flushRandomMessage()` (~50%).
  - Then pick `user = int32(0, users-1)`, `test = oneOf(mods)`, run `test(users[user], gen, testObject)`.
  - After all iterations: `compare(users)`; returns the init result.
  Draw order is load-bearing for seed replay: up to 3 `int32` draws before the op, plus 1-2 inside the branches.

### 2.5 Multi-client network simulation (`testHelper.js:16-259`)
- `TestConnector` (`:146`) holds `allConns`, `onlineConns` and the PRNG. No sockets, no timers: **fully synchronous, single-threaded, message delivery is pull-based** and controlled by the test/PRNG.
- Each `TestYInstance` (`:63`) is a `Y.Doc` with `receiving: Map<sender, Uint8Array[]>`: one **FIFO queue per (sender -> receiver) pair**. `_receive` just enqueues (`:135-137`).
- On a local update (`doc.on('update')`, `:87-94`): if `origin !== testConnector`, wrap with `syncProtocol.writeUpdate` (y-protocols message type 2) and `broadcastMessage` to every other **online** instance's queue. Every update (local or remote) is also pushed to `this.updates` (for the mergeUpdates oracle in `compare`).
- `flushRandomMessage()` (`:179-203`): among online instances with non-empty `receiving`, pick a random receiver (`oneOf`), then a random sender queue (`oneOf` over the Map entries), shift the head message, and run `syncProtocol.readSyncMessage(decoder, replyEncoder, receiver, receiver.tc)`. The `receiver.tc` (the connector) is passed as transaction **origin**, which is how the `update` handler above knows not to re-broadcast. A non-empty reply (SyncStep1 -> SyncStep2) is enqueued back on the sender's queue. Returns false when nothing is left. `flushAllMessages` loops until false (`:208-214`).
- Consequence: **order within one (sender,receiver) pair is always FIFO; only the interleaving across pairs is random.** There is no message loss, duplication or reordering inside a pair. Out-of-order *structs* (pending structs path) arise only from the interleaving of different senders and from partitions, not from within-pair reordering.
- `disconnect()` clears the instance's own inbound queue and removes it from `onlineConns` (`:101-104`). Messages it already sent that sit in *other* instances' queues are still delivered later; a disconnected instance's local edits are not broadcast (the `broadcastMessage` guard, `:20-27`). Replies to a sender that has since gone offline are appended to its queue and delivered after reconnect (quirk).
- `connect()` (`:110-126`): if not online, add to `onlineConns`, **broadcast SyncStep1 to all others** and **receive a SyncStep1 from each other instance** (queued, not applied). The subsequent flush produces SyncStep2 replies, so reconnect = full state-vector resync. `reconnectAll/disconnectAll/syncAll/disconnectRandom/reconnectRandom` (`:216-258`) compose these.
- All simulated sync is the **v1 encoding of the y-protocols sync messages** (`sync.js:38-40`: step1=0, step2=1, update=2).

### 2.6 The oracle: `compare(users)` (`testHelper.js:309-363`)
Called at the end of nearly every simulation test:
1. `connect()` all, flush everything (`:310-311`).
2. Builds a fresh `Y.Doc` per user from `enc.mergeUpdates(user.updates)` and appends these to `users` (mutating the caller's array!) to cross-check `mergeUpdates` against live replicas (`:314-319`).
3. Asserts `store.pendingDs === null && store.pendingStructs === null` for all (`:324-327`).
4. Iterator consistency: `getArray().toArray()` vs `Array.from(array)`, map `keys()`/`entries()` vs `toJSON()` (`:329-341`).
5. Pairwise equality of: array JSON, map JSON, xml `toString()`, text delta (custom comparer for embedded types), `encodeStateVector`, `compareStructStores` (client count, struct counts, per-struct constructor/id/deleted/length and for Items left/right/origin/rightOrigin/parentSub plus doubly-linked-list integrity, `:376-413`), and `encodeSnapshot(snapshot(...))` (`:343-361`).
6. Destroys all docs.
Known weaknesses visible in the code: `Y.equalDeleteSets(...)` is called without asserting its boolean result (`:358`), so delete-set equality is only indirectly checked via struct `deleted` flags and snapshots; the doc comment at `:300-308` mentions "user 0 gc" and "disconnect & reconnect so gc is propagated" but there is no such code; `compareStructStores` early-returns `t.fail(...)` in one branch (`:403`); the checker asserts that replicas **agree with each other**, never what the agreed result is.

## 3. Test catalog (yjs)

Legend: **Det** deterministic; **Rand** uses `tc.prng`; **Rep** time-repeated; **Sim** uses `init()` simulation (hence 1 PRNG draw, V1 effectively). Inputs are summarized, not exhaustive.

### 3.1 `doc.tests.js` (11 tests, all Det, none use Sim)
| Line | Test | Behaviour | Inputs |
|---|---|---|---|
| 7 | testAfterTransactionRecursion | `toJSON()` inside `afterTransaction` on a doc with 15000 XmlText pushed in one transaction does not blow the stack | 15000 `XmlText('a')`, origin `'test'` |
| 25 | testOriginInTransaction | Order of `afterTransaction` origins when a handler triggers a cleanup transaction (via `toDelta(snapshot)`) and a nested transact: `['first','cleanup','nested']` | one text, 3 transactions |
| 52 | testClientIdDuplicateChange | Doc auto-changes its `clientID` when it receives an update carrying its own client id | both clientID 0 |
| 66 | testGetTypeEmptyId | `getText('')` and `getText()` are the same root; survives sync | 'h','i' |
| 79 | testToJSON | `Doc.toJSON()` shape: empty `{}`, array + nested map | |
| 106 | testSubdoc | Subdoc lifecycle events (`subdocs` added/removed/loaded), `load()`, `destroy()`, `shouldLoad:false`, `getSubdocGuids`, `getSubdocs`; remote apply emits `added` for all incl. duplicate guid `'a'`; delete keeps guid listed | guids a, c, duplicate a |
| 173 | testSubdocLoadEdgeCases | `shouldLoad`/`autoLoad` flags and `loaded`/`added` sets across insert, destroy (re-created doc replaces), load, and remote apply | |
| 220 | testSubdocLoadEdgeCasesAutoload | Same with `autoLoad:true` | |
| 262 | testSubdocsUndo | Undo/redo of inserting a subdoc into an XmlFragment | |
| 277 | testLoadDocsEvent | `isLoaded`, `whenLoaded` promise on manual `emit('load')` | async |
| 293 | testSyncDocsEvent | `isSynced`, `whenSynced`, `sync` event true then false; `whenSynced` replaced after unsync | async |

### 3.2 `y-map.tests.js` (41 tests; 25 Det, 16 Rep/Rand)
Det (Sim unless noted):
| Line | Test | Behaviour |
|---|---|---|
| 14 | testIterators | Only TS type assumptions; `console.log`, **no assertion** |
| 41 | testMapEventError | Accessing `event.keys` outside the observer call throws (yjs#539); JS-specific lifecycle |
| 62 | testMapHavingIterableAsConstructorParamTests | `new Y.Map(iterable)` from entries / pairs / spread of other maps (1 user) |
| 89 | testBasicMapTests | set null/number/string/object/nested Y.Map/Y.Array/bools; 3 users, one offline then reconnected; size 7 |
| 141 | testGetAndSetOfMapProperty | set string, `undefined`, `null`; remote values equal. Encodes JS `undefined` |
| 162 | testYmapSetsYmap | `set` returns the Y.Map; reads back same instance |
| 174 | testYmapSetsYarray | same for Y.Array, toJSON |
| 187 | testGetAndSetOfMapPropertySyncs | one key sync |
| 202 | testGetAndSetOfMapPropertyWithConflict | concurrent set same key, 3 users: higher clientID (`c1`) wins |
| 217 | testSizeAndDeleteOfMapProperty | size after delete (1 user) |
| 231 | testGetAndSetAndDeleteOfMapProperty | concurrent set + delete |
| 247 | testSetAndClearOfMapProperties | `clear()` |
| 265 | testSetAndClearOfMapPropertiesWithConflicts | 4 users concurrent sets then clear |
| 290 | testGetAndSetOfMapPropertyWithThreeConflicts | winner `c3` (client 2's last) |
| 307 | testGetAndSetAndDeleteOfMapPropertyWithThreeConflicts | 4 users; delete wins |
| 330 | testObserveDeepProperties | observeDeep path/keysChanged; concurrent creation of `deepmap` converges to same item ID on all |
| 367 | testObserversUsingObservedeep | deep event paths `[[],['map'],['map','array']]`, 3 calls |
| 391 | testPathsOfSiblingEvents | one deep callback per transaction with sibling events and paths |
| 423 | testDeepEventPathsWithMultipleDeepObservers | (yjs#768) paths relative per observed type; callback order `observe c, observeDeep a, observeDeep c, afterTransaction`; `currentTarget` |
| 492 | testThrowsAddAndUpdateAndDeleteEvents | `keysChanged`, `target` for add/update/delete |
| 526 | testThrowsDeleteEventsOnClear | `keysChanged` for clear |
| 550 | testChangeEvent | `changes.keys` action add/update/delete (with oldValue), including net-zero add+delete in one transaction (empty keys) |
| 598 | testYmapEventExceptionsShouldCompleteTransaction | observer exceptions do not abort cleanup: `update` still emitted, later transactions work |
| 648 | testYmapEventHasCorrectValueWhenSettingAPrimitive | `event.value` (legacy field) |
| 665 | ...FromOtherUser | same for remote |

Rand/Rep (`mapTransactions`, `y-map.tests.js:683-703`): ops `set` (key from `['one','two']`, value `utf16String`), `setType` (new Y.Array filled `[1,2,3,4]` or Y.Map with `deepkey:'deepvalue'`), `_delete`. 5 users via `applyRandomTests`. Tests (line, iterations): `Tests10` (708, **3**), 40 (715), 42 (722), 43 (729), 44 (736), 45 (743), 46 (750), 300 (757), 400 (764), 500 (771), 600 (778), 1000 (785), 1800 (792), 5000 (799 skip unless production), 10000 (807 skip), 100000 (815 skip). Note: the `10` test actually runs 3 iterations (name/arg mismatch).

### 3.3 `y-array.tests.js` (42 tests; 26 Det, 16 Rep)
| Line | Test | Behaviour |
|---|---|---|
| 14 | testBasicUpdate | `encodeStateAsUpdate`/`applyUpdate` roundtrip |
| 26 | testFailsObjectManipulationInDevMode | dev-mode only: mutating a plain object/array after insertion throws (Object.freeze). **JS-specific** |
| 48 | testSlice | `slice(start,end)` incl. negative end |
| 63 | testArrayFrom | `Y.Array.from` |
| 76 | testLengthIssue | (yjs#297) length vs `toArray().length` across insert/delete inside and outside transaction; search-marker bug |
| 105 | testLengthIssue2 | (yjs#314) sequence of transactions, out-of-range insert in nested transaction must throw, then insert at valid index, delete |
| 146 | testDeleteInsert | `delete(0,0)` ok, `delete(1,1)` on empty throws, `delete(1,0)` valid |
| 162 | testInsertThreeElementsTryRegetProperty | toJSON before/after sync |
| 174 | testConcurrentInsertWithThreeConflicts | 3 concurrent inserts at 0 |
| 185 | testConcurrentInsertDeleteWithThreeConflicts | concurrent insert/delete |
| 199 | testInsertionsInLateSync | 2 users offline insert at same index, reconnect |
| 217 | testDisconnectReallyPreventsSendingMessages | offline edits stay local until `connect()`; asserts local values |
| 235 | testDeletionsInLateSync | |
| 249 | testInsertThenMergeDeleteOnSync | offline delete of all, resync |
| 262 | testInsertAndDeleteEvents | observer fires for insert/delete |
| 286 | testNestedObserverEvents | an observer that mutates; nested event runs after current handler (`[0,1]`) |
| 312 | testInsertAndDeleteEventsForTypes | events for inserting/deleting a Y.Array |
| 339 | testObserveDeepEventOrder | deep events sorted so `path.length` non-decreasing |
| 363 | testObservedeepIndexes | (yjs#457) path `['my-array',3]` |
| 384 | testChangeEvent | `changes.added/deleted` sizes and `delta` (`insert`/`delete`/`retain`) |
| 411 | testInsertAndDeleteEventsForTypes2 | exactly one event per insert/delete op |
| 431 | testNewChildDoesNotEmitEventInTransaction | (yjs#155) new type changed in same transaction emits no event |
| 448 | testGarbageCollector | delete-all offline then resync (no assertion that GC occurred) |
| 462 / 479 | testEventTargetIsSetCorrectlyOnLocal / OnRemote | `event.target` |
| 497 | testIteratingArrayContainingTypes | `for..of` over 10 Y.Maps |

Rand/Rep (`arrayTransactions`, `y-array.tests.js:519-576`): `insert` (1-4 copies of a unique number at random pos; asserts the post-state equals a mirror array, exercising search markers), `insertTypeArray` (`[1,2,3,4]` inside), `insertTypeMap` (`someprop` set 42,43,44), `insertTypeNull`, `_delete` (random 1-2 element delete, or with 50% chance delete inside a nested Y.Array). Iterations: 6 (581), 40, 42, 43, 44, 45, 46 (588-623), 300, 400, 500, 600, 1000, 1800 (630-665), 3000 (672, production only), 5000 (680, prod), 30000 (688, prod).

### 3.4 `y-text.tests.js` (44 tests)
Det (all Sim unless noted):
| Line | Test | Behaviour |
|---|---|---|
| 13 | testDeltaBug | (yjs#474) ~400-line fixture delta (table/block attributes) applied via `applyDelta`, then a change delta applied; compares the result against an expected delta (see lines 13-413). **Huge golden data** |
| 419 | testDeltaBug2 | (yjs#503) same, ~1200 lines of fixture; asserts `delta[41]` after applying a changeEvent delta (line 1642) |
| 1657 | testDeltaAfterConcurrentFormatting | concurrent overlapping `format`; observer delta after merge `[{retain:3,bold:true},{retain:2,bold:null}]` |
| 1679 | testBasicInsertAndDelete | event `delta` for insert/delete/retain+delete; no-op transaction yields `[]` |
| 1714 | testBasicFormat | formatting attributes in `toDelta` and event deltas; **reads internals** `text0._start.right.right.right.content.str` (line 1737) to assert no duplicate format marker |
| 1752 | testFalsyFormats | `{falsy:false}` attributes preserved distinct from null |
| 1773 | testMultilineFormat | `applyDelta` retain+attributes across newlines (1 doc, no Sim) |
| 1796 | testNotMergeEmptyLinesFormat | (no Sim) adjacent `\n` with attrs not merged |
| 1816 | testPreserveAttributesThroughDelete | (no Sim) |
| 1838 | testGetDeltaWithEmbeds | embed object round trip |
| 1851 | testTypesAsEmbed | Y.Map as embed, remote event delta contains the type |
| 1874 | testSnapshot | `toDelta(snapshot)` and `toDelta(snap2, snap1)` with `ychange` added/removed attributes (user stripped) |
| 1916 | testSnapshotDeleteAfter | snapshot ignores later inserts |
| 1936 | testToJson | `toJSON()` = plain string |
| 1945 / 1956 | testToDeltaEmbedAttributes / NoAttributes | embed with and without attributes |
| 1967 | testFormattingRemoved | after deleting formatted text only 1 child remains (via `getTypeChildren`, i.e. cleanup) |
| 1977 | testFormattingRemovedInMidText | 3 children remain |
| 1990 | testFormattingDeltaUnnecessaryAttributeChange | (yjs#344) event deltas identical on both peers, only the changed attr |
| 2023 | testInsertAndDeleteAtRandomPositions | **Rand, not Rep**: N=100000 ops, 1 user, `prng.word` insert or delete at `uint32(0,len)`; **no assertions** (final-length `describe`; expected-string mirror is commented out, lines 2029-2046) |
| 2053 | testAppendChars | 10000 appends, `length === N` |
| 2072 | testBestCase | **perf**, builds 100000 raw `Item`s directly; no assertions; touches `Y.Item` constructor |
| 2111 | testLargeFragmentedDocument | **perf** (`t.measureTime`): 100000 single-char inserts at 0 in one transaction, V2 encode, apply; no assertions |
| 2143 | testIncrementalUpdatesPerformanceOnLargeFragmentedDocument | **perf**: 100000 update events, `mergeUpdates` and applying them one by one; no assertions |
| 2183 | testSplitSurrogateCharacter | (yjs#248) emoji `'👾'` split by insert/delete/format while peer offline; must encode and converge (3 sub-cases) |
| 2215 | testSearchMarkerBug1 | (yjs#307) self-applying update handlers, search marker; expects `'a_sda'` |
| 2253 | testFormattingBug | (async, yjs PR#32) three newlines with conflicting url attr; expected delta |
| 2278 | testDeleteFormatting | delete formatting leaves no redundant format items; delta equality on both docs |

Rand/Rep:
- `textChanges` (`y-text.tests.js:2311-2337`): insert (`charCounter++ + prng.word`, mirror string assertion at random pos) and delete (0-2 chars). Tests `testRepeatGenerateTextChanges{5,30,40,50,70,90,300}` (lines 2342-2396); after convergence each asserts `Y.cleanupYTextFormatting(users[0].getText('text')) === 0`.
- `qChanges` (`y-text.tests.js:2418-2516`): insert text with one of `[undefined,{bold},{italic},{italic,color}]`, insert embed (image object or `Y.Map` with random word key/value), delete 0-2, format 0-2 chars, "insert codeblock" via applyDelta (`{insert:'\n', format:{...}}` - note key is `format`, not `attributes`, so the code-block attribute is probably ignored), "complex delta op" (up to 7 sub-ops of retain+format/insert/delete). Tests `testRepeatGenerateQuillChanges{1,2,2Repeat,3,30,40,70,100,300}` (2540-2604). `2Repeat` (2558) loops 1000 times *inside* the test on top of time repetition. 1/2/2Repeat also assert `cleanupYTextFormatting === 0`; 3..300 do not.
- `checkResult` (`y-text.tests.js:2521-2535`) is **vacuous**: it compares `users[i]` with `users[i]` (`p1` and `p2` both use `result.users[i]`, lines 2527-2528). The real checking is `compare()` inside `applyRandomTests` plus the cleanup count.

### 3.5 `y-xml.tests.js` (11 tests, Det)
| Line | Test | Behaviour |
|---|---|---|
| 6 | testCustomTypings | TS generics for `getAttribute`; `console.log`, no assertion. Not portable |
| 36 | testSetProperty | set/get attr local + remote |
| 48 | testHasProperty | `hasAttribute` / `removeAttribute` |
| 65 | testEvents | `attributesChanged`, `childListChanged` locally and remotely |
| 105 | testTreewalker | `querySelectorAll('p')`, `querySelector` over nested elements |
| 124 | testYtextAttributes | `XmlText.setAttribute/getAttribute/getAttributes` + `changes.keys` `{action:'add'}` |
| 138 | testSiblings | `nextSibling`, `prevSibling`, `parent`, `firstChild` |
| 154 | testInsertafter | `insertAfter(null|ref, ...)`, throws for ref not in element |
| 184 | testClone | `clone()` of fragment with children, toJSON equal |
| 201 | testFormattingBug | `XmlText` delta with `{em:{}, strong:{}}` object attrs round trip |
| 216 | testElement | `getXmlElement`, insert children |

### 3.6 `encoding.tests.js` (4 tests, Det, no Sim)
| Line | Test | Behaviour |
|---|---|---|
| 26 | testStructReferences | `contentRefs` table: length 11, indices 1-9 map to specific `readContent*`; 10 reserved for Skip. **Reads internals** (`../src/internals.js`) |
| 46 | testPermanentUserData | (async, `promise.wait(10)`) PermanentUserData mapping across 3 docs; no assertions, only "does not throw" |
| 72 | testDiffStateVectorOfUpdateIsEmpty | (yjs#308) `encodeStateVectorFromUpdate` of an update whose origin is missing = `[0]` (empty SV) |
| 91 | testDiffStateVectorOfUpdateIgnoresSkips | merged updates 0 and 2 -> SV for client has clock 1, size 1 (skip ignored) |

### 3.7 `compatibility.tests.js` (3 tests, Det) - the only wire-format goldens
Header comment says v1 docs were generated with Yjs v13.2.0 from the randomized tests (`compatibility.tests.js:1-6`). Each decodes a base64 V1 update into an empty Doc and compares JSON/delta: `testArrayCompatibilityV1` (15; ~320 ints/objects/nested arrays, `[1,2,3,4]`, `{"someprop":44}`), `testMapDecodingCompatibilityV1` (26; `{"one":[1,2,3,4],"two":{"deepkey":"deepvalue"}}`), `testTextDecodingCompatibilityV1` (38; delta with bold/italic/color attrs, `image` embeds, long text). **Decode only, V1 only; never re-encodes and compares bytes, no V2 golden.**

### 3.8 `undo-redo.tests.js` (26 tests, Det)
| Line | Test | Behaviour |
|---|---|---|
| 6 | testInconsistentFormat | XmlText `format` with null / new attrs merging, also after update roundtrip (gc:false) |
| 49 | testInfiniteCaptureTimeout | `captureTimeout: Number.MAX_VALUE` + `stopCapturing()` splits stack items |
| 62 | testUndoText | insert+delete in one stack item is no-op; follow redone items; undo with concurrent remote text; redo; formatting undo/redo (3 users) |
| 109 | testEmptyTypeScope | (#241) `new UndoManager([], {doc})` + `addToScope` |
| 122 | testRejectUpdateExample | documented pattern: reject changes on a restricted type using UndoManager + `trackedOrigins` + pending struct handling |
| 176 | testGlobalScope | UndoManager on a whole Doc |
| 189 | testDoubleUndo | text '1221' insert/undo/undo/insert -> '12321' |
| 210 | testUndoMap | map set/undo/redo, subtype restore, undo skipped when overwritten by another user, `stopCapturing` grouping |
| 250 | testUndoArray | array undo/redo with concurrent user, nested Y.Map sets, redo order |
| 301 | testUndoXml | XmlElement/XmlText child, format undo, delete undo |
| 326 | testUndoEvents | `stack-item-added` / `stack-item-popped` with `type`, `changedParentTypes`, `meta` |
| 351 | testTrackClass | `trackedOrigins: new Set([Number])` tracks origin by constructor (JS-specific semantic) |
| 366 | testTypeScope | UndoManager scoped to one of two Y.Text children, multi-type scope |
| 387 | testUndoInEmbed | undo of inserting/editing a Y.Text embed |
| 407 | testUndoDeleteFilter | `deleteFilter` option; reads `item.content.type._map` (internal) |
| 427 | testUndoUntilChangePerformed | two live-wired docs, two UndoManagers (discuss.yjs.dev) |
| 458 | testUndoNestedUndoIssue | (#317) nested map/array replacement, `captureTimeout:0`, 3x undo + 3x redo |
| 514 | testConsecutiveRedoBug | (#355) redo chain of Y.Map point |
| 565 | testUndoXmlBug | (#304) XmlElement attributes undo/redo |
| 610 | testUndoBlockBug | (#343) 4-level replacement undo/redo |
| 670 | testUndoDeleteTextFormat | (#392) peer stays consistent after undoing format-delete |
| 708 | testBehaviorOfIgnoreremotemapchangesProperty | `ignoreRemoteMapChanges:true` |
| 731 | testSpecialDeletionCase | (#447) attribute change + delete in one tx, undo |
| 760 | testUndoDeleteInMap | (#500) repeated set/delete on a key, undo chain |
| 790 | testUndoDoingStackItem | (async) `currStackItem` visible during undo/redo, null afterwards |
| 819 | testUndoSetAttributeAndDeleteSyncsAttributes | (#757) XmlText embed attribute restore; V2 apply to remote |

### 3.9 `snapshot.tests.js` (12 tests, Det; 1 uses Sim)
`testBasic` (8), `testBasicXmlAttributes` (20; `getAttributes(snapshot)`), `testBasicRestoreSnapshot` (35), `testEmptyRestoreSnapshot` (50; snapshot with a bogus `sv.set(9999,0)` client), `testRestoreSnapshotWithSubType` (70), `testRestoreDeletedItem1` (93), `testRestoreLeftItem` (109), `testDeletedItemsBase` (127), `testDeletedItems2` (143), `testDependentChanges` (159; the only Sim one: 2 users, sets `doc.gc=false` after creation, snapshot after sync, then restore from both docs), `testContainsUpdate` (203; `snapshotContainsUpdate` true/false for insert and delete updates), `testContainsUpdate2` (229; yjs#767, delete-set partially outside snapshot). All use `new Y.Doc({gc:false})` except the two contains tests.

### 3.10 `updates.tests.js` (6 tests)
| Line | Test | Behaviour |
|---|---|---|
| 103 | testMergeUpdates | (Sim) concurrent inserts; for each of three "encoders" `[V1, V2, via-Doc]` (`updates.tests.js:26-85`) merging state-as-update from all users reproduces the array |
| 119 | testKeyEncoding | (Sim) V2 key-table encoding (`italic` repeated) applied to a peer, delta check |
| 223 | testMergeUpdates1 | 4 inserts, 3 encoders, 5 merge cases (`checkUpdateCases`, lines 140-218): flat merge, overlapping, 3-way overlapping, separated updates with skips, merge of merges. Asserts equal array, `encodeStateVector(doc) == encodeStateVectorFromUpdate(update)`, for V2: `diffUpdate` metadata equals `parseUpdateMeta` of the tail merge and (non-Doc encoder or no deletes) byte-equality `diffed == mergeUpdatesV2([deletesUpdate, partMerged])`, and `parseUpdateMeta` `from` = 0, `to` = last struct end |
| 243 | testMergeUpdates2 | same with deletes in the update stream (`hasDeletes=true`) |
| 263 | testMergePendingUpdates | 5 incremental updates re-applied to chained docs in order 0,1,3,2,4 -> text `'nenor'` (pending structs) |
| 311 | testObfuscateUpdates | `obfuscateUpdate`: text content/attrs/embeds, map keys, array primitives, XmlElement nodeName/attrs, subdoc guid all changed; structure (lengths, counts) preserved |

### 3.11 `relativePositions.tests.js` (9 tests, Det)
Helper `checkRelativePositions` (`:7-20`) loops index 0..len-1 x assoc -1/0/1, encodes -> decodes -> resolves and asserts index/assoc. `Case1`-`Case6` (25-82) differ only in text-building order (including empty text). `Case7` (93; yjs#657) `followUndoneDeletions` true/false on an untouched position. `AssociationDifference` (111): assoc -1 vs 0 after insertion at the position. `WithUndo` (128): rpos follows undone deletion (`follow` flag true -> 1, false -> 6; clone doc -> 6); has a stray `console.log`. Note: **loop bound is `i < ytext.length`, so the end-of-text position is never round-tripped.**

### 3.12 y-protocols `awareness.test.js` (1 test, Det)
`testAwareness` (`awareness.test.js:9-53`): two Awareness instances (clientID 0 and 1) wired through `encodeAwarenessUpdate`/`applyAwarenessUpdate`. Asserts: set state propagates; remote `meta.clock` increments (1 then 3); `change` event `added`/`updated`; local change event shape; setting an equal state fires `update` but not `change`; `setLocalState(null)` produces `removed`. Quirk: the test expects remote clock 3 after the third `setLocalState` of an identical value, showing clock increments even on no-op sets.
There is **no sync-protocol test** (`sync.js`) and **no auth test** (`auth.js`) in y-protocols. The sync messages are only exercised indirectly by `testHelper.js`.

## 4. Test-style and portability notes for the port

- Oracle style: (a) convergence of N replicas (most), (b) exact expected value/delta (hand-written), (c) golden base64 (compat), (d) event shape/order (observers, deep events), (e) "does not throw" (PermanentUserData, perf, GC, OOB indexes).
- Heavy use of white-box access: `contentRefs` (`encoding.tests.js:27`), `_start.right...` (`y-text.tests.js:1737`), `_item.id` (`y-map.tests.js:343`), `store.clients/pendingDs/pendingStructs` (`testHelper.js:324-327,377`), `Item.left.lastId` (`testHelper.js:397`), `content.type._map` (`undo-redo.tests.js:412`), direct `Item` constructor (`y-text.tests.js:2082`), `readClientsStructRefs`/`readDeleteSet`/`UpdateDecoderV2` (`updates.tests.js:4,193-199`). A C# port needs `InternalsVisibleTo` or equivalent for the same invariants.
- JS-only semantics with no direct C# analogue: `undefined` values (`y-map.tests.js:144,153`), `Object.freeze` dev mode (`y-array.tests.js:26`), constructor-as-tracked-origin (`undo-redo.tests.js:354`), event accessed after transaction (`y-map.tests.js:41`), TypeScript typing tests (`y-xml.tests.js:6`, `y-map.tests.js:14`), `toDOM`. Observer exceptions (`y-map.tests.js:598`) are portable in spirit.
- Time-based behaviour: the default UndoManager `captureTimeout` (500 ms) is *never* exercised deterministically; tests either use `Number.MAX_VALUE`, `0`, or `stopCapturing()` (e.g. `undo-redo.tests.js:49,462,614`). Only `promise.wait(10)` (`encoding.tests.js:57`) is a real wait.

## 5. Randomization inventory (what exactly is fuzzed)

| Aspect | Fuzzed? | Evidence |
|---|---|---|
| Users | fixed 5, ids 0-4 | `testHelper.js:431` |
| Network | random cross-pair delivery order; random disconnect/reconnect; no loss/dup/in-pair reorder | `testHelper.js:179-203,434-447` |
| Encoding | Not fuzzed, V1 only | `testHelper.js:57-61` |
| Array content | numbers, null, nested Y.Array (ints), nested Y.Map (ints) | `y-array.tests.js:519-576` |
| Map content | 2 keys; Latin-1 strings; nested array/map | `y-map.tests.js:683-703` |
| Text content | a-z words with counter prefix; 3 attribute sets; image/Y.Map embeds | `y-text.tests.js:2402-2516` |
| XML types | not fuzzed | no xml `*Transactions` array |
| UndoManager, snapshots, subdocs, relative positions | not fuzzed | |
| Transaction shape | each op is its own implicit transaction; ops never batched in `doc.transact` | `testHelper.js:450` |
| Text length units | Latin-1 + surrogate pairs only in deterministic test | `prng.js:146`, `y-text.tests.js:2183` |

## 6. Visible gaps against the source

Method: grep of test files for every exported symbol in `yjs/src/index.js:3-110` and public methods in `yjs/src/types/*`, `utils/*`, plus a read of `y-protocols/{sync,awareness,auth}.js`. "Untested" means no test file references it.

### 6.1 Wire format and updates
- `convertUpdateFormatV1ToV2` / `convertUpdateFormatV2ToV1` (`updates.js:579`, exported `index.js`): **never referenced** by any test.
- `decodeUpdate` / `decodeUpdateV2` (`updates.js:139`), `readUpdate` / `readUpdateV2` (`encoding.js:462`): untested directly. `logUpdate` is placed in the encoder tables but never called.
- V2 on the wire is never fuzzed (see 2.4). V2 coverage is limited to `updates.tests.js`, `testKeyEncoding`, `testUndoSetAttributeAndDeleteSyncsAttributes`, `testLargeFragmentedDocument`. No V1 vs V2 equivalence test (same doc encoded both ways decodes to identical structure), no byte-for-byte golden for **any** encoder output (compat only decodes, V1 only, from a 2018-era release).
- `UpdateEncoderV1/V2` and `UpdateDecoderV1/V2` primitives (RLE of client ids, key table, `writeTypeKey`, `writeLen`, string/any encodings: `UpdateEncoder.js`, `UpdateDecoder.js`): no isolated tests; `lib0/encoding` tests (`lib0/encoding.test.js`) cover the lower layer separately.
- `ContentAny` payload variety: no test inserts floats, large ints (> 2^31 / > 2^53), negative numbers, bigint, `Uint8Array`, `undefined` inside arrays, nested objects with unicode keys, as shared content. Binary content (`ContentBinary`) is never created by a test; `ContentJSON` (legacy) only appears in the `contentRefs` identity check (`encoding.tests.js:29`).
- Malformed / hostile input: truncated updates, wrong content ref, duplicate struct, struct beyond a skip, huge clocks, negative varints. No test. (`StructStore.js:75` integrity check, `updates.js:680,690` and `UpdateEncoder.js:155` unexpected-case branches have no test.)
- Idempotence/commutativity: applying the same update twice, applying updates in every permutation, applying a state update on a doc that already has a superset: only implicit (random sim + `testMergePendingUpdates` order 0,1,3,2,4).
- `encodeStateVector`/`decodeStateVector` round trip: only `decodeStateVector` of a tiny merged update (`encoding.tests.js:105`); `writeStateVector`/`readStateVector` with many clients or large clocks untested.
- `mergeDeleteSets`, `isDeleted`, `iterateDeletedStructs`, `findRootTypeKey`, `equalSnapshots`, `emptySnapshot`, `isParentOf`, `logType`, `tryGc`-via-API: no tests (`tryGc` string only appears in a perf helper `tryGc()` that is a different function, `y-text.tests.js:2100`).
- `diffUpdate` (V1) never asserted (`encoding.diffUpdate` is only called inside `if (enc.updateEventName !== 'update')`, `updates.tests.js:180`, so V1 `diffUpdate` and V1 `parseUpdateMeta` byte-level behaviour are untested).

### 6.2 Core algorithm
- **Convergence is checked, absolute ordering is not.** Except for a handful of fixed-expectation tests (`testGetAndSetOfMapPropertyWithConflict`, `testConcurrentInsert...` has no expected values at all), nothing asserts *which* interleaving YATA picks for concurrent inserts. A port that converges to a different (but internally consistent) order would pass the sim suite yet be incompatible with JS peers. Needs differential/golden tests.
- Concurrent-insert corner cases of `Item.integrate` (origin/rightOrigin conflict loop) are only reached by randomness; no deterministic test for same-origin/different-rightOrigin, interleaved multi-client runs, or insert into a deleted range.
- GC: `testGarbageCollector` (`y-array.tests.js:448`) asserts nothing about GC. No test checks that deleted content becomes `GC`/`ContentDeleted`, that `gcFilter` is honoured, that `Item.keep` blocks GC, that `gc:false` retains content, or that encode/apply of GC structs with `Skip` works. `Doc` option `gcFilter` untested. `ContentDeleted`, `GC`, `Skip` structs are only touched indirectly.
- Struct merging (`Item.mergeWith`, `tryMergeDeleteSet`, `Transaction.js:226-253`): only checked as *agreement between replicas* via `compareStructStores`, never against an expected merged shape.
- Search markers (`AbstractType.js:43-260`): the only direct tests are `testLengthIssue*`/`testSearchMarkerBug1` and mirror comparison in array/text fuzz. Not testable by API for a port that chooses not to implement markers (behavioural equivalence only).
- Pending structs / pending delete set: `testMergePendingUpdates` plus the `compare` null assertions. No test that `pendingDs` gets retried when missing structs arrive; no test of `Y.applyUpdate` with an update that depends on a missing client followed by that client's update.
- Client id collision handling beyond `testClientIdDuplicateChange` (`doc.tests.js:52`), and random `clientID` generation range.
- `Transaction` lifecycle events: `beforeTransaction`, `beforeObserverCalls`, `afterTransactionCleanup`, `beforeAllTransactions`, `afterAllTransactions`, `destroy`, `transaction.changed`, `changedParentTypes`, `deleteSet`, `beforeState/afterState` (`Transaction.js:270,315,365-396,427-429`) are never asserted (only `afterTransaction`, `update`, `subdocs`, `load`, `sync`). `Y.transact` with nested transactions and origin propagation: only `testOriginInTransaction`. `doc.on('update')` payload args (origin, doc, transaction) untested.
- Exceptions: only `Y.Map` observer exception behaviour is tested (`y-map.tests.js:598`); equivalent behaviour for Array/Text/deep observers and `afterTransaction` handlers is not.
- `Doc.get(name, Constructor)` upgrade from `AbstractType` and the "already defined with a different constructor" error (`Doc.js:228-243`): untested. `getXmlElement` only once.

### 6.3 Types
- **Y.Array**: `unshift`, `map`, `forEach`, `clone` (`YArray.js:171,240,249,96`) untested; `insert` with out-of-range index covered only in `testLengthIssue2`/`testDeleteInsert`; `delete` with length past the end; negative `slice` start; `Array.from` with types.
- **Y.Map**: `has`, `forEach`, `clone`, `values()` after deletes, `entries()` of nested types are only reached through `compare`'s iterator check; `new Y.Map(iterable)` once. No test where a nested type is replaced and old child's events/GC fire.
- **Y.Text**: `setAttribute/getAttribute/getAttributes/removeAttribute` (`YText.js:1225-1279`) are **not tested on `Y.Text`** (only on `XmlText`/`XmlElement`); `clone`; `applyDelta` with `sanitize:false`; `toDelta` with `computeYChange` custom function (`YText.js:1009`), `toDelta` with only `snapshot` and no prev; `insertEmbed` with Y.Map inside snapshots; `Y.Text` constructor with initial string and later sync. `format` where `length` exceeds text; `delete` past the end; `insert` of empty string; non-BMP lone surrogate in delete/format boundary only in one test; end-of-text positions.
- **Y.Xml***: `XmlHook` (whole class) untested; `toDOM` untestable in Node (fine, port-irrelevant); `XmlFragment.unshift/slice/forEach/push` partially; `createTreeWalker` only via querySelector; `XmlElement.clone` deep-copy of attributes/children; `XmlText.toString` with nested/overlapping formats (only `<bold>` once); `getAttributes(snapshot)` for elements only in `testBasicXmlAttributes`; XmlElement nodeName default `UNDEFINED` only appears as an expected string.
- **Events**: `YEvent.path`, `changes.delta` for text embeds+attributes in remote case, `changes.keys` for XML attributes beyond one case, `event.keys` after nested transaction, `target`/`currentTarget` after move between parents. `YEvent.js:90,176` error branch.
- **Cross-type nesting**: types inside types inside arrays inside maps exist only in `testBasicMapTests` and fuzz at depth 2; no deep (3+) nesting stress; deleting a parent type that has pending children from a remote.
- **Subdocs**: only local operations and one remote apply (`doc.tests.js:106-271`). Not tested: subdoc option round-trip (`guid`, `collectionid`, `meta`, `shouldLoad`, `autoLoad` in `ContentDoc` encode), `parentDoc`/`_item` linkage, duplicate guid handling, subdoc deletion when parent is GCed, subdocs in fuzz, concurrent subdoc insertion.
- **PermanentUserData** (`utils/PermanentUserData.js`): only "doesn't throw" (`encoding.tests.js:46`), no assertion on `getUserByClientId`/`getUserByDeletedId`.

### 6.4 Undo/redo
- 26 hand-written tests, **no fuzz** and no multi-client + undo randomized test.
- Untested: `canUndo/canRedo`, `clear(undoStack, redoStack)`, `addTrackedOrigin/removeTrackedOrigin`, `destroy`, events `stack-item-updated` and `stack-cleared`, `captureTransaction` option, default time-based `captureTimeout` merging (500 ms, nondeterministic), `ignoreRemoteMapChanges` only in a 2-user happy path, undo with GC enabled and remote changes, undo of subtypes' deletion across transactions with `keep` semantic.

### 6.5 Snapshots and relative positions
- `encodeSnapshot/decodeSnapshot/encodeSnapshotV2/decodeSnapshotV2` have no round-trip test (`compare` uses `encodeSnapshot` only as an equality fingerprint, `testHelper.js:360`); `equalSnapshots` untested.
- `createDocFromSnapshot` throws when `originDoc.gc` is true (`Snapshot.js:172-175`): the error path is untested (all tests carefully set `gc:false`; `testDependentChanges` flips it after init).
- `typeListToArraySnapshot`, `typeMapGetSnapshot`, `typeMapGetAllSnapshot` untested directly.
- Relative positions: only `Y.Text`. Untested: Y.Array / Y.XmlFragment indices, positions at end-of-type (`index == length`, the helper loop stops one short, `relativePositions.tests.js:9`), JSON round trip (`relativePositionToJSON`/`createRelativePositionFromJSON`, `RelativePosition.js:82,105`), `compareRelativePositions` (`:351`), positions when the anchor item was deleted and GCed, concurrent edits from remote peers while a position is held, positions anchored on a type root by name (`tname`) vs by item id vs by type id, `followUndoneDeletions` beyond one test.

### 6.6 Awareness and y-protocols
- Awareness has a single test. Untested: `removeAwarenessStates` (`awareness.js:167`), `modifyAwarenessUpdate` (`:219`), the 30000 ms `outdatedTimeout` and its 3000 ms check interval + auto renewal (`:13,59-77`; timers, needs fake clock), `destroy`, `getStates` multi-client, clock conflict rules in `applyAwarenessUpdate` (`:256`: `currClock < clock || (equal && state === null && has)`), a remote removal of the *local* client being resurrected (the `clock++` branch at `:262`), JSON state encoding for non-trivial states, `update` vs `change` distinction for deep-equal states with multiple clients, malformed updates.
- `sync.js` (134 lines): `readSyncStep1/2`, `writeSyncStep2`, `readUpdate`, `readSyncMessage` unknown-message-type error path, `errorHandler` argument and exact byte layout (`PROTOCOL.md` documents `[0,0,..]`, `[0,1,..]`, `[0,2,..]`) have no direct tests; the layout is exercised implicitly only through the simulation.
- `auth.js` (33 lines, `writePermissionDenied/readAuthMessage`): untested.
- `PROTOCOL.md` has a doc bug (`messageTypes` map uses `SyncProtocolMessageType` twice, `PROTOCOL.md` example), irrelevant to behaviour but a reminder that the doc is not normative: `sync.js`/`awareness.js` are.
- The `lib0` submodule ships its own tests (`lib0/*.test.js`: encoding, decoding, prng, buffer, string, etc.), which are the natural oracle for the primitive layer a C# port must reproduce (varint 53-bit, any-encoding, `Xoroshiro128plus`). They are not part of the yjs suite and not catalogued here.

### 6.7 Test-infrastructure defects that weaken existing coverage (could mislead a port that copies them)
1. `checkResult` compares `users[i]` to itself (`y-text.tests.js:2527-2528`): vacuous.
2. `equalDeleteSets(...)` result discarded in `compare` (`testHelper.js:358`).
3. Code-block fuzz op uses key `format` where `applyDelta` expects `attributes` (`y-text.tests.js:2478`), likely producing plain `\n` inserts.
4. V2 never used by the sim (`testHelper.js:57-61,296`) although the coin-flip draw is consumed.
5. Disconnect probability and flush probabilities in `applyRandomTests` are not what the comments say (`testHelper.js:434-444`: ~3%/~1.4%/~50%).
6. `testRepeatGeneratingYmapTests10` runs 3 iterations (`y-map.tests.js:708-710`).
7. `compare()` mutates the caller's `users` array (pushes merged docs, then destroys everything).
8. `testInsertAndDeleteAtRandomPositions` (`y-text.tests.js:2023`), `testIterators`, `testCustomTypings`, `testBestCase`, `testPermanentUserData` and the two big-document perf tests assert (almost) nothing.
9. Module-level counters (`_uniqueNumber`, `charCounter`) leak between tests. `charCounter` is prepended to inserted text, so its digit count changes the text length, and the lengths feed later draws: a seed reproduces neither payload nor structure unless the counters start equal (SIM-05).
11. The Quill generator shares its three `marks` objects across a run and `insertText` writes null for every active attribute missing from the object it is given (`yjs/src/types/YText.js:254`), so later uses of a mark carry stale nulls (ADR 0017 implementation notes).
10. Relative-position helper never tests index == length (`relativePositions.tests.js:9`).

## 7. Implications for the C# test-suite design (inputs for later tickets)

- To get the same coverage you must port, per behaviour: ~150 deterministic scenarios, the sim harness (`TestConnector`/`compare`), and the three fuzz generators. Port the harness semantics exactly as in 2.5/2.6 (per-pair FIFO, random cross-pair pick, connect = SyncStep1 both ways, origin = connector).
- For seed replay across implementations the only portable seed contract is the Xoroshiro variant in 2.2, including the number and order of draws in `init` (one `bool`) and `applyRandomTests` (up to three `int32` before each op). Otherwise generate fixtures with a Node generator and record op logs (feeds TASK-1.10).
- The sim suite cannot detect a wrong-but-convergent conflict order: differential tests against the JS oracle (golden bytes and op-log replay with state compare) are required to cover section 6.2.
- Gaps in 6.1-6.6 are candidate behaviours for the inventory in TASK-1.8; items flagged "JS-specific" in section 4 should be marked non-portable there.
