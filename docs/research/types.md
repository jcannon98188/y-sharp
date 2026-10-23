# Research: shared types and events inventory (TASK-1.3)

Oracle: `yjs/` v13.6.33 (submodule). All refs are `file:line` relative to the repo root.
Sources abbreviated: `AT` = `yjs/src/types/AbstractType.js`, `TX` = `yjs/src/utils/Transaction.js`,
`YE` = `yjs/src/utils/YEvent.js`, `YA`/`YM`/`YT`/`YXF`/`YXE`/`YXT`/`YXH` = `yjs/src/types/Y{Array,Map,Text,XmlFragment,XmlElement,XmlText,XmlHook}.js`,
`Item` = `yjs/src/structs/Item.js`. Upstream tests are in `yjs/tests/*.tests.js` ("UT" below).

Scope: observable behaviour of the shared types and events, written as numbered, testable statements. Wire encoding, GC,
undo, snapshots and sync are covered by other research tickets; they are mentioned only where they change type/event behaviour.
Each ID is intended to map to one (or a small table-driven group of) test(s) in the C# suite. "Differential" means: run the same
script against the JS oracle and the port, compare outputs.

Conventions: `index`/`length` count UTF-16 code units for YText (and elements for lists). "Local" = produced by a call on this
doc; "remote" = produced by `applyUpdate` (transaction.local === false, `TX:415` `transact(..., local)`).

---

## 0. Shared structure (applies to all types)

### 0.1 Class hierarchy and event classes

| Type | Base | Event class | Observer entry point |
|---|---|---|---|
| YArray | AbstractType | `YArrayEvent extends YEvent` (`YA:29`) | `YA:118` |
| YMap | AbstractType | `YMapEvent` (adds `keysChanged: Set<string>`) (`YM:27-37`) | `YM:115` |
| YText | AbstractType | `YTextEvent` (adds `childListChanged`, `keysChanged`) (`YT:600-626`) | `YT:920` |
| YXmlFragment | AbstractType | `YXmlEvent` (`childListChanged`, `attributesChanged`) (`yjs/src/types/YXmlEvent.js:15-40`) | `YXF:261` |
| YXmlElement | YXmlFragment | `YXmlEvent` (inherited, no override) | `YXF:261` |
| YXmlText | YText | **`YTextEvent`** (inherited; NOT YXmlEvent) | `YT:920` |
| YXmlHook | YMap | **`YMapEvent`** (inherited) | `YM:115` |

- T-0.1 A differential test must assert the concrete event class per type, notably that `YXmlText` events are `YTextEvent`
  (have `keysChanged`, no `attributesChanged`) and `YXmlHook` events are `YMapEvent`.
- T-0.2 `AbstractType.parent` returns `_item.parent` or `null` for root types (`AT:299-301`). Root types: `_item === null`.
- T-0.3 A bare `AbstractType` placeholder is created by `doc.get(name)` with no constructor (`yjs/src/utils/Doc.js:215`); a later
  `doc.get(name, Y.Text)` upgrades it in place, moving `_map/_start/_length` and re-parenting every item (`Doc.js:221-239`).
  Requesting a *different* concrete constructor than the existing one throws
  `Type with the name <name> has already been defined with a different constructor` (`Doc.js:243`).
  Tests: upgrade from placeholder to each concrete type; conflict matrix (Array vs Map vs Text vs XmlFragment vs XmlElement; note
  `XmlElement` and `XmlFragment` have different constructors so `getXmlElement(n)` after `getXmlFragment(n)` throws).
  Also `ydoc.get('', Y.XmlText)` is how UT creates a root XmlText (`y-xml.tests.js` testYtextAttributes).
- T-0.4 Premature access: reading `length/get/toArray/toJSON/toString/forEach/map/size/keys...` on a type with `doc === null`
  calls `warnPrematureAccess()` which logs a warning (`AT:25`, `YA:108`, `AT:418`, `AT:454`, `YM:125,181`, `AT:890`) and returns
  the *empty* value (not the prelim content). Exception: `YXmlFragment.length` returns `_prelimContent.length` before integration
  (`YXF:187`), whereas `YArray.length` returns `_length` = 0 (`YA:107-110`). Edge case worth pinning in the port's API contract
  (decide: replicate warn+empty, or throw). The warning itself need not match; the return values must.

### 0.2 Prelim (pre-integration) content

- T-0.5 Before a type is attached to a doc it buffers writes: YArray `_prelimContent` array (`YA:44,145,162,187`), YMap
  `_prelimContent` Map (`YM:57-63,209,227,263`), YXmlFragment `_prelimContent` (`YXF:140,324,347-350,367`), YXmlElement `_prelimAttrs`
  (`YXE:37,144,164`), YText `_pending` closure list (`YT:861`; every mutator pushes a closure: `YT:995,1140,1162,1184,1212,1231,1251`).
- T-0.6 On `_integrate` the buffered content is replayed (`YA:76-80`, `YM:76-82`, `YXF:161-165`, `YXE:66-72`, `YT:887-895`). Replay for
  YArray/YMap/Xml happens inside the transaction that integrates the parent; for YText replay exceptions are caught and
  `console.error`'d, not rethrown (`YT:889-893`) - a YText with a bad pending op silently drops the remainder.
- T-0.7 Constructor sugar: `new Y.Text('abc')` queues `insert(0,'abc')` (`YT:861`); `new Y.Map(iterable)` (`YM:51-64`, UT
  testMapHavingIterableAsConstructorParamTests); `Y.Array.from(items)` (`YA:57-64`, UT testArrayFrom); `new Y.XmlElement(name='UNDEFINED')`
  (`YXE:31`); `new Y.XmlText('init')` goes through YText constructor.
- T-0.8 Nested prelim types: a prelim type nested in a prelim type is integrated recursively; the order of replay determines the
  item order/clock layout (relevant to update bytes and event counts, see 4.12).
- T-0.9 `clone()` produces an unattached copy that is only readable after attachment: YArray `YA:96-105`, YMap `YM:98-107`, YText
  `YT:908-912` (via `toDelta`/`applyDelta`), YXmlFragment `YXF:178-183`, YXmlElement `YXE:90-100` (copies attributes then children),
  YXmlText `YXT:39-43`, YXmlHook `YXH:38-44` (note: YXmlHook.clone copies values with `set` but does NOT deep-clone nested types,
  unlike YMap.clone). `AbstractType.clone` base throws `methodUnimplemented` (`AT:332`).

### 0.3 Content types accepted in list/map positions

- T-0.10 List insert (`typeListInsertGenericsAfter`, `AT:645-698`): accepts `null`, `Number`, `Object`, `Boolean`, `Array`, `String`
  (packed into one `ContentAny` run), `Uint8Array`/`ArrayBuffer` (ContentBinary, copied), `Doc` (ContentDoc, subdoc), any
  `AbstractType` (ContentType). Anything else (e.g. `undefined`, `Date`, `BigInt`, functions, class instances) throws
  `Unexpected content type in insert operation` (`AT:691`).
  Consecutive JSON-ish values become one Item (one clock range); a binary/type/doc breaks the run (`AT:675`). Test that
  `[1,2,new Y.Map(),3]` creates 3 items and is observably 4 elements.
- T-0.11 Map set (`typeMapSet`, `AT:846-879`): additionally accepts `Date` and `BigInt` (ContentAny), `undefined` and `null`
  both become `ContentAny([value])` (`AT:851`). So `ymap.set('k', undefined)` is accepted; `ymap.get('k')` returns `undefined`
  but `ymap.has('k') === true` and `toJSON()` contains `k: undefined`. Unsupported constructors throw `Unexpected content type` (`AT:874`).
  (Contrast: lists reject `undefined`; maps and `Date`/`BigInt` are not accepted by list insert.)
- T-0.12 Returned references: `ymap.set(k, v)` returns `v` itself (so the prelim type object becomes the live integrated type)
  (`YM:221-230`, UT testObserversUsingObservedeep style `a.set('b', new Y.Map())`).
- T-0.13 Inserting a type that is already integrated elsewhere is undefined behaviour in Yjs (no guard); do not oracle-test, but
  record in the port's contract (suggest: throw).

---

## 1. YArray

### 1.1 API surface

`length`, `insert(index, content[])`, `push(content[])`, `unshift(content[])`, `delete(index, length=1)`, `get(i)`, `toArray()`,
`slice(start=0,end=length)`, `toJSON()`, `map(f)`, `forEach(f)`, `[Symbol.iterator]`, `clone()`, `Y.Array.from()` (`YA:37-266`).

### 1.2 Behaviours

- A-1 `insert(i, [..])` inserts before element `i`; `insert(length, ..)` appends; one transaction => one event. (`YA:139-147`, `AT:711-750`)
- A-2 `insert(i>length)` throws `Length exceeded!` (`AT:712-714`, `AT:700`). Negative index: not guarded (see A-20).
- A-3 `insert(0, [])` and `push([])`: still opens a transaction; no content => no Item, no `changed` entry, so no observer call and no `update`
  event (guard `TX:131`). Test: observer count stays 0.
- A-4 `push` appends after the last item (`AT:763-773`); `unshift` is `insert(0)` (`YA:171-173`).
- A-5 `delete(i, n)` deletes `n` elements; `n === 0` returns without opening a transaction (`AT:785`); `i+n > length` throws `Length exceeded!`
  **after** having deleted what existed (`AT:804-816`): partial effect remains (the transaction commits, observers fire). Pin this exactly.
  Default `length=1`.
- A-6 `delete` on an empty array with `n>=1` throws `Length exceeded!` (same path).
- A-7 `get(i)` out of range returns `undefined` (no throw) (`AT:618-634`).
- A-8 `slice(start,end)`: negative values are offset from `length` (`AT:419-424`); `end` default `length`; result clamps silently; `start >= end`
  returns `[]`. UT testSlice covers `slice(0,-1)`. Edge: `slice(-5)` with length 3 yields start=-2 (no clamp to 0 - verify with oracle;
  current code produces elements from index 0 because loop `c.length <= start` is false for negative start and `i = start` negative
  reads `c[-2]` = `undefined`: **this is a latent bug**; record as "oracle quirk, differential-test, decide whether to replicate").
- A-9 Nested types are returned by reference: `arr.get(0) === theTypeInserted` after integration.
- A-10 `toJSON()` recursively calls `toJSON` on nested `AbstractType`s; `Uint8Array` passes through; `Doc` subdocs pass through
  (`YA:227-229`). `toArray()` returns nested types un-JSONed.
- A-11 `forEach(f)` passes `(value, index, type)`; `map(f)` passes the same triple (`AT:501-534`). Iteration skips deleted items.
- A-12 Iterator (`AT:543-583`): lazily walks items, skips deleted *at item boundaries*; mutating the array during iteration is
  unspecified, do not oracle-test (keep one explicit "no crash" smoke test).
- A-13 Length bookkeeping: `_length` increments on integrate and decrements on `Item.delete` for countable, list items
  (`Item.js:517-519`, `Item.js:620-622`). Test length after concurrent insert/delete merges (via sync suite).
- A-14 Search markers (`AT:27-215`, max 80): purely an optimisation but affects correctness bugs (UT testLengthIssue, testLengthIssue2,
  testSearchMarkerBug1 in y-text). Markers are cleared on remote transactions (`AT:359-363`). Port should have a randomised
  "indexed ops vs naive List<T> model" test with the search-marker disabled and enabled (differential against JS `testRepeatGeneratingYarrayTests*`).
- A-15 Binary content: `Uint8Array` and `ArrayBuffer` are *copied* on insert (`AT:679`); reading returns `Uint8Array`.
- A-16 Mixed run splitting: inserting `[1,"a",{x:1},[2]]` yields one ContentAny item of length 4. Deleting the middle splits the item
  (`getItemCleanStart`, `AT:739,798,807`). Test index arithmetic across splits.
- A-17 Insert inside a deleted region: indices count only non-deleted countable items (`AT:734-745`).
- A-18 Concurrent insert ordering at the same index is deterministic (YATA); cover in the sync/convergence ticket, but event test
  A-E2 below checks the delta on the receiving side.
- A-19 Array as list of arrays: an inserted JS `Array` value is stored as a plain `ContentAny` value (a JS array), NOT a YArray; likewise a plain
  object. UT testFailsObjectManipulationInDevMode (dev-mode freeze) is JS-specific; port: document that inserted plain objects are
  stored by value (copy or reference?) - contract decision.
- A-20 Negative / fractional / NaN indices are not validated by Yjs (`insert(-1,..)`: `index > _length` false, `index === 0` false,
  falls into `findMarker`/walk; result is effectively insert at 0 or at end depending on marker state). Record as "undefined; port should
  throw `ArgumentOutOfRange`"; exclude from oracle differential.

### 1.3 YArray events

- A-E1 One transaction => at most one `YArrayEvent` per array, regardless of number of ops (UT testInsertAndDeleteEventsForTypes2).
- A-E2 `event.changes.delta` ops are `{insert: any[]} | {retain: n} | {delete: n}` (arrays, not strings, for inserts), with trailing `retain`
  removed and adjacent ops of the same kind merged (`YE:189-232`; the final `retain` is dropped at `YE:229`). Reference values
  (UT testChangeEvent, `y-array.tests.js:384-409`):
  - `insert(0,[newArr,4,'dtrn'])` => `[{insert:[newArr,4,'dtrn']}]`, `added.size===2`... note `added` counts *Items* (the type item + one ContentAny item = 2), not elements.
  - `delete(0,2)` => `[{delete:2}]`, `deleted.size===2`.
  - `insert(1,[0.1])` => `[{retain:1},{insert:[0.1]}]`.
- A-E3 `changes.added`/`changes.deleted` are `Set<Item>`; **item identities are an implementation detail**; in the port assert
  counts only via an agreed projection (see section 8 open questions).
- A-E4 Insert-then-delete of the same elements in one transaction: no net `delta` entries (`YE:204-211` skips items that are both
  added and deleted), but the type is still in `transaction.changed` so the observer **is** called with an empty `delta` (`[]`) (cf. UT
  testBasicInsertAndDelete for text: `delta` equals `[]`). Test: observer count 1, delta `[]`.
- A-E5 `event.keys` for YArray is an empty Map (only `null` sub in `changed`) (`YE:95-96`).
- A-E6 `event.target === array`, `event.currentTarget === array` in `observe`; in `observeDeep` `currentTarget` is the observed ancestor
  (`TX:301-303`, UT testEventTargetIsSetCorrectlyOnLocal/Remote).
- A-E7 Re-entrancy: an observer that mutates the array does not nest; the second event is delivered after the first handler returns
  (UT testNestedObserverEvents: vals `[0,1]`; mechanism `TX:415-447` `_transactionCleanups` queue).
- A-E8 A child type created and mutated inside the same transaction that inserts it does not emit its own event (UT
  testNewChildDoesNotEmitEventInTransaction; mechanism `addChangedTypeToTransaction` `TX:159-164`: skip if `type._item.id.clock >= beforeState`
  or item deleted).
- A-E9 Deleting an array that has observers: no event is fired for a type whose `_item.deleted` at cleanup (`TX:282-284`).
  Also events for children of a deleted parent are filtered in deep observers (`TX:297-299`).
- A-E10 `observe`/`unobserve`: `unobserve` of a never-registered function logs `[yjs] Tried to remove event handler that doesn't exist.`
  and does not throw (`yjs/src/utils/EventHandler.js:330-337`). Registering the same function twice calls it twice; `unobserve`
  removes **all** copies (filter by identity) (`EventHandler.js:333`).

---

## 2. YMap

### 2.1 API surface

`size`, `keys()`, `values()`, `entries()`, `forEach(f(value,key,map))`, `[Symbol.iterator]` (= entries), `set`, `get`, `has`, `delete`,
`clear`, `toJSON`, `clone` (`YM:46-273`).

### 2.2 Behaviours

- M-1 `set(k,v)` creates a new Item; the previous Item for `k` is deleted (`Item.js:509-515`). Last writer by item order wins; `get` reads
  `content.getContent()[length-1]` (`AT:892`).
- M-2 `get(missing)` => `undefined`; `get(deleted)` => `undefined` (`AT:889-893`).
- M-3 `has(k)` true iff Item present and not deleted, **including keys whose value is `undefined` or `null`** (`AT:924-928`).
- M-4 `delete(missing)` is a no-op: no transaction effect, no event, no `update` (`AT:830-835` guard; though `transact` still opens an
  empty transaction and `beforeTransaction`/`afterTransaction` fire - assert doc-level events fire even when nothing changes, see TX-3).
- M-5 `clear()` deletes every live key inside **one** transaction (`YM:255-265`); one event whose `keysChanged` contains all cleared keys
  (UT testThrowsDeleteEventsOnClear `y-map.tests.js:526`).
- M-6 `size` counts live keys (`YM:144-146`); `keys()/values()/entries()` iterate in `Map` insertion order of the underlying `_map`
  (i.e., order of first-ever creation of the key in this replica, not alphabetical, not sync-stable across replicas). **Cross-replica
  iteration order is therefore not guaranteed; tests must sort keys before comparing**. Within a replica, a re-set of an existing key keeps its
  original position (the same `_map` entry is overwritten, `Item.js:511`).
- M-7 `toJSON` recursively JSON-ifies nested types (`YM:124-137`); includes keys with `undefined` values.
- M-8 Value kinds round-trip: string, number, boolean, null, object, array, `Uint8Array`, `Date`, `BigInt`, nested types, subdocs (T-0.11).
  Number precision: `ContentAny` uses lib0 any-encoding (int/float32/float64/bigint choice); semantic value must be preserved (cross-check
  with the encoding research ticket).
- M-9 Concurrent set on same key: deterministic winner by YATA ordering (client id tie-break) - sync suite. Event-side: loser replica sees
  `update` with `oldValue` = its own local value (UT testYmapEventHasCorrectValueWhenSettingAPrimitiveFromOtherUser).
- M-10 Constructor with iterable (`YM:51-64`); `new Y.Map(undefined)` is empty; duplicates in the iterable follow `Map` semantics (last wins).
- M-11 Key edge cases: empty string `''` is a valid key; `'__proto__'` is safe because `_map` is a `Map` but `toJSON` assigns onto a plain object
  (`YM:129-135`) - JS would set the prototype; port should use `Dictionary`; mark as "JS-only hazard, do not replicate". Unicode keys round-trip
  (encoded via lib0 `writeKey`, string table in V2).
- M-12 Deleting the value (a nested type) deletes the whole subtree: `ContentType.delete` (`yjs/src/structs/ContentType.js:105-125`) deletes all
  children and all map entries. Test: set nested map with children, delete the key, assert `nested.get('a') === undefined` and `nested.doc` still non-null
  (type object stays referenced but deleted; `_item.deleted === true`).

### 2.3 YMap events

- M-E1 `event.keysChanged: Set<string>` = keys touched in the transaction, **including keys whose net effect is nothing** (e.g. set then
  delete in same txn): `keysChanged` contains `'c'` but `event.keys` / `changes.keys` omits it (UT testChangeEvent `y-map.tests.js:550-596`,
  `YE:108-115,129`: `return` skips). Test both.
- M-E2 `changes.keys.get(k)` is `{action, oldValue}` with exact semantics (`YE:87-138`):
  - first ever set: `add`, `oldValue: undefined`
  - set over existing live value: `update`, `oldValue = previous value` (last element of previous content)
  - delete of live value: `delete`, `oldValue = previous value`
  - several sets in one txn: `oldValue` is the value *before the transaction* (UT: `set a=3; set a=4` after `a=2` => update, oldValue 2)
  - add then update in same txn: `add` with `oldValue undefined`
  - set then delete in same txn on a new key: absent
  - delete then set in same txn: `update`, oldValue = value before the txn
- M-E3 `oldValue` for nested types is the type object (possibly already deleted).
- M-E4 `event.changes.delta` for YMap is `[]`, `added`/`deleted` are empty sets (`YE:191-192` only loops when `changed.has(null)`).
- M-E5 Event data may only be computed during the callback. Accessing `event.keys`/`changes`/`delta` after the outermost transaction is cleaned
  up throws `You must not compute changes after the event-handler fired.` (`YE:10,89-91,175-177`; UT testMapEventError). However already
  computed (cached `_keys/_changes`) values stay readable. Test: compute in handler, read after => fine; first access after => throws.
- M-E6 Exceptions in observers: a throwing `observe` or `observeDeep` handler does not prevent (a) other handlers from running
  (`lib0 callAll` calls each, rethrows the first error afterwards), (b) the `update` event, (c) the data being committed. The exception propagates
  out of the outermost mutating call (`map.set` throws) (UT testYmapEventExceptionsShouldCompleteTransaction `y-map.tests.js:598-646`).
  Mechanism: `try/finally` in `TX:267-400`, `transact` finally `TX:431-449`. Port requirement: same guarantees (AggregateException vs first exception
  - decide, but must still complete cleanup).
- M-E7 Map event after a remote update carries `target` = local type (UT testEventTargetIsSetCorrectlyOnRemote for arrays; same for maps).

---

## 3. YText

### 3.1 Data model

A YText is a linked list of Items whose content is one of: `ContentString` (countable, length = UTF-16 units), `ContentEmbed` (countable, length 1,
`yjs/src/structs/ContentEmbed.js:20-37`), `ContentType` (embedded shared type, length 1), `ContentFormat` (key,value; **not countable**,
`ContentFormat.js:37`), plus tombstones. `type._length` counts string units + embeds, never formats (`Item.js:517-519`). Presence of any format sets
`_hasFormatting = true` and sets `_searchMarker = null` (disables search markers for rich text forever on that replica)
(`yjs/src/structs/ContentFormat.js:67-73`).

`YXmlText extends YText` (same data model, plus sibling accessors `YXT:15-26`, XML `toString` `YXT:68-103`). It also has map-style attributes via
`setAttribute` etc. (`YT:1225-1281`), shared by YText and YXmlText.

### 3.2 Plain-text behaviours

- T-1 `insert(i, s)` inserts at UTF-16 offset `i`; `s.length <= 0` returns immediately: no transaction (`YT:1125-1127`). Index beyond `length`:
  `findPosition`/`findNextPosition` just stops at the end, i.e. **clamps to end silently** (`YT:91-115`), unlike YArray which throws. Test.
- T-2 `delete(i, n)`: `n === 0` no-op; deleting beyond the end deletes what exists, **no throw** (`YT:539-567`, loop ends when `right === null`). Contrast YArray A-5.
- T-3 `toString()` concatenates non-deleted `ContentString`s only, embeds are skipped (`YT:935-949`). `toJSON()` = `toString()` (`YT:957`).
  `length` = string units + embed count.
- T-4 `new Y.Text('x')` + attach: content appears after integration; `length` before attach logs warn and returns 0.
- T-5 Surrogate pairs: Yjs counts UTF-16 units, so `'👾'.length === 2`. Splitting a pair (insert/delete/format at offset between the two units) replaces
  both halves with U+FFFD (`yjs/src/structs/ContentString.js:51-68`; UT testSplitSurrogateCharacter `y-text.tests.js:2183-2214`). **Port decision:** C# strings are UTF-16
  as well, so lengths/offsets match natively; the lone-surrogate replacement behaviour must be replicated; test three UT scenarios (insert in the middle, partial delete,
  format in the middle) and assert both replicas converge and contain `�`.
- T-6 `applyDelta` accepts `{insert}`, `{retain}`, `{delete}` ops (`YT:971-997`). Unknown op shape: silently ignored. A sequence is a single transaction (one event).
  With `sanitize=true` (default) trailing newline of the last insert is kept; with `{sanitize:false}` a trailing `\n` of the *last* op at end-of-text is stripped
  (`YT:983`). Test both flags.
- T-7 Prelim `applyDelta` before attach is queued (`YT:995`) but note it re-calls `applyDelta(delta)` with default opts - the `sanitize` option is **lost** for
  pre-attach calls. Edge case.
- T-8 `applyDelta` insert of empty string is skipped (`YT:984`).
- T-9 `applyDelta` `delete`/`retain` beyond end: retain beyond end inserts newline characters (`YT:338-349`: `formatText` appends `length` newlines when `length > 0`
  after reaching the end - the "Quill assumes trailing newline" behaviour). Test `retain: 5` on empty text with attributes => text becomes `\n\n\n\n\n`... check
  exact: only when attributes force iteration; with no attributes `formatText` still loops and appends newlines. Pin with oracle differential: `applyDelta([{retain:3}])` on `'ab'`.
  Mark HIGH VALUE edge case.

### 3.3 Formats

- F-1 `insert(i, s, attrs)`: wraps content with `ContentFormat(key,value)` start items and negating format items at the end so surrounding text is unaffected
  (`YT:224-276`, `insertAttributes` + `insertNegatedAttributes`). Redundant format items are not created when the surrounding attributes already equal the requested ones
  (`minimizeAttributeChanges` `YT:200-212`; UT testBasicFormat "Does not insert duplicate attribute marker" `y-text.tests.js:1737`).
- F-2 `insert(i, s)` **without** attributes inherits the formatting at position `i` (`YT:1132-1136`: copies `currentAttributes`); with an explicit attributes object (even
  `{}`) it does NOT inherit and any active attributes are negated (`YT:255-259`: every currentAttribute not mentioned gets `null`). Test truth table: inherit vs `{}` vs
  `{bold:null}`.
- F-3 Attribute value `null` means "remove attribute" (`YT:184-191`, `updateCurrentAttributes`); `toDelta` therefore never shows `null` attributes. In event deltas, `null` is
  shown on `retain` ops to mean "attribute removed" (UT testBasicFormat last assertion; testDeltaAfterConcurrentFormatting).
- F-4 Falsy but non-null values (`false`, `0`, `''`) are real attribute values (UT testFalsyFormats `y-text.tests.js:1752`).
- F-5 Attribute equality (`equalAttrs` `YT:42`): `===` or flat-object equality (`object.equalFlat`, **shallow**; nested objects compared by reference). Consequence:
  `{em:{}}` applied twice is considered equal (UT testFormattingBug in y-xml: attributes `{em:{}, strong:{}}`). Port needs shallow structural equality on attribute values.
  Define precisely what a C# attribute value is (JsonElement / object / Any) before writing tests.
- F-6 `format(i, n, attrs)`: `n===0` no-op; if no content at/after `i` (`pos.right === null`) returns without effect (`YT:1206-1208`). Splits strings at both ends. Formatting a range
  that already has the format produces no new items. Range crossing embeds formats embeds too (embed counts 1).
- F-7 Format cleanup: after a delete, now-redundant `ContentFormat` items inside the deleted gap are deleted (`cleanupFormattingGap` `YT:366-416`, called from `deleteText`
  `YT:559-561`); UT testFormattingRemoved (`Y.getTypeChildren(text).length === 1` after deleting all formatted text), testFormattingRemovedInMidText (3).
  Item-count assertions rely on `getTypeChildren` which is test-only API; the port needs an internal accessor (or assert via encoded update size / `toDelta` only - decide).
- F-8 Remote-change cleanup: after a *remote* transaction touching a YText with formatting, `cleanupYTextAfterTransaction` runs in the same cleanup cycle **after**
  `afterTransaction` (`TX:316-320`, `YT:924-927`, `YT:486-528`) in a *new transaction* (fires its own events/updates). It removes duplicate/redundant format items. Observable:
  an extra `update` message may be emitted after a remote update (only if cleanup changed something). Test with UT testFormattingBug-style concurrent formatting + assert convergence and
  that a second `update` event with origin `null` appears only when needed (differential on count).
- F-9 Concurrent formatting results (UT testDeltaAfterConcurrentFormatting): after merge, `text1` observer sees `[{retain:3,attributes:{bold:true}},{retain:2,attributes:{bold:null}}]`?
  (exact value pinned by that test; quirk: second op carries `bold:null` because delta computation compensates for format items already present). Differential-test.

### 3.4 Embeds

- E-1 `insertEmbed(i, obj, attrs?)` where `obj` is an object or an `AbstractType` (`YT:1154-1164`). It occupies exactly 1 index unit. `text.length` counts it. `toString()` skips it (T-3).
  `insertText` picks `ContentEmbed` for non-string non-AbstractType, `ContentType` for AbstractType (`YT:265`). **Quirk:** `insertEmbed` with falsy `attributes` calls `findPosition(..., useSearchMarker = !attributes)`.
- E-2 Embed value is JSON-like ("any") content; non-object primitives (numbers) are not validated by Yjs but typings say Object. Edge: `insertEmbed(0, 5)` stores `5` as ContentEmbed(5);
  `toDelta` gives `{insert:5}`. Port contract: restrict.
- E-3 `toDelta` emits embeds as `{insert: embed, attributes?}`; `attributes` key omitted when none active (UT testToDeltaEmbedNoAttributes `y-text.tests.js:1956`; with attrs 1945).
- E-4 `applyDelta([{insert:{linebreak:'s'}}])` round-trips (UT testGetDeltaWithEmbeds). `applyDelta([{insert: new Y.Map([['key','val']])}])` inserts a nested Y type; remote observer's
  `event.delta[0].insert` is the live YMap (UT testTypesAsEmbed `y-text.tests.js:1851-1872`).
- E-5 Deleting an embedded type deletes the subtree (ContentType.delete) and `deleteText` handles `ContentType/ContentEmbed/ContentString` (`YT:545-554`).
- E-6 Embeds do not merge with neighbouring strings; string runs adjacent to an embed are separate ops in `toDelta` (`YT:1065-1082` calls `packStr` first).

### 3.5 `toDelta` (snapshot-free)

- D-1 Output: array of `{insert: string|object|AbstractType, attributes?}` with adjacent strings of identical attributes merged into one op (merging occurs because format items between
  equal-attribute strings only call `packStr` when the format item is *visible*, `YT:1084-1089`; two strings separated by a format item that does not change attributes *are* split into two ops. This
  is the source of the "not canonical" delta issue; test: `insert(0,'a',{b:true}); insert(1,'b',{b:true})` yields one op `ab`, but ops across an `{b:null}`-then-`{b:true}` round trip
  may yield two ops with equal attributes (pin with oracle)).
- D-2 `attributes` object only present when `currentAttributes.size > 0` (strings: `addAttributes` flag `YT:1035`).
- D-3 `toDelta(snapshot, prevSnapshot, computeYChange)` adds `ychange` attribute to changed ranges (`YT:1009-1112`); covered by the snapshot research ticket; mentioned here because it runs inside a
  transaction with origin `'cleanup'` (`YT:1107`, UT doc.tests testOriginInTransaction `doc.tests.js:25-50`: origins `['first','cleanup','nested']`).
- D-4 `toDelta` of empty text is `[]`.

### 3.6 Attributes on YText / YXmlText (map side)

- X-1 `setAttribute/getAttribute/getAttributes/removeAttribute` use the same map machinery as YMap (`YT:1225-1281`). They are separate from rich-text formats.
  Events: key appears in `keysChanged` and `changes.keys` (UT testYtextAttributes `y-xml.tests.js:124-132`: `{action:'add', oldValue:undefined}`).
- X-2 `getAttributes()` returns only live attributes (`AT:902-914`).

### 3.7 YTextEvent

- Y-1 `childListChanged` true iff the sequence changed; `keysChanged` = attribute names (`YT:619-625`).
- Y-2 `event.delta` (`YT:655-839`): Quill-style ops, `{insert}`/`{delete}`/`{retain, attributes?}`, trailing attribute-less `retain` ops removed (`YT:826-834`), `delete`/`retain` counted in
  units. Reference values from UT testBasicInsertAndDelete / testBasicFormat (see above): `[{insert:'abc'}]`, `[{delete:1}]`, `[{retain:1},{delete:1}]`, `[]`, `[{retain:1},{retain:1,attributes:{bold:null}}]`.
- Y-3 `event.changes` for YText: `{keys, delta, added: new Set(), deleted: new Set()}`; **`added`/`deleted` are always empty** (`YT:631-645`). Do not port any expectation of populated sets.
- Y-4 **Side effect in a getter**: computing `delta` may call `item.delete(transaction)` on redundant format items (`YT:784`, `YT:810`) and always opens a (nested, reused) `transact(y, ...)`
  (`YT:662`). Therefore reading `event.delta` can mutate the doc during the observer phase (it deletes only format items that are redundant). Test: compare doc state/update-bytes with and without a
  delta-reading observer after concurrent formatting; document as an oracle quirk. Because delta computation happens lazily, **delta values depend on when they are first read**; always read inside the handler.
- Y-5 Insert+delete in same txn => delta `[]` (UT). Embeds/Types in delta: `insert` is the embed object / live type (`YT:723-731`).
- Y-6 Concurrent format event deltas: see F-9 and UT testFormattingDeltaUnnecessaryAttributeChange `y-text.tests.js:1990-2020` (both replicas get identical single-op delta).
- Y-7 `event.keys` works as for maps; `event.path` as for all events (section 5).

---

## 4. YXmlFragment / YXmlElement / YXmlText / YXmlHook

### 4.1 YXmlFragment (list of YXmlElement | YXmlText | YXmlHook)

- X-F1 API: `length`, `firstChild`, `insert`, `insertAfter(ref, content)`, `delete(index,length=1)`, `push`, `unshift`, `get`, `slice`, `toArray`, `forEach`, `createTreeWalker`, `querySelector`,
  `querySelectorAll`, `toString`, `toJSON`, `toDOM`, `clone` (`YXF:134-440`). `toDOM` is browser-only; **out of scope for the C# port** (record decision in API contract ticket).
- X-F2 `insert/delete/push/get/slice/toArray/forEach` share the list machinery (same semantics and errors as YArray: `Length exceeded!`).
- X-F3 `insertAfter(ref, content)`: `ref` is `null` (insert at start), an `AbstractType` (uses `ref._item`) or an `Item`; attached: `typeListInsertGenericsAfter` (`YXF:338-343`). Before attach: if `ref`
  not found in prelim content throws `Reference item not found` (`YXF:347-349`; UT testInsertafter `y-xml.tests.js:154-182` checks `t.fails`). When attached with a `ref` that is not a child: no
  validation (undefined).
- X-F4 `firstChild` = first non-deleted child or `null` (`YXF:146-149`, `AT:344-350`).
- X-F5 `toString()` = concatenated child `toString()` (`YXF:270-272`); `toJSON()` = `toString()` (`YXF:277-279`) - **NOT a JSON structure** (XML string). Differs from YArray.
- X-F6 `length` before attach = prelim length (T-0.4).
- X-F7 Tree walker (`YXF:57-123`): depth-first pre-order over `YXmlElement`/`YXmlFragment` descendants (descends only into those two constructors, not YXmlText/Hook); filter receives the *type*;
  `querySelector(q)` upper-cases both sides and matches `nodeName` (so YXmlText/Hook, which lack nodeName, never match: `element.nodeName && ...`); `querySelectorAll` returns all matches in document order.
  Skips deleted nodes. UT testTreewalker (`y-xml.tests.js:105-122`). Edge: query is only a tag name; no CSS selector support. Deleted subtree: not walked. Empty root: `next().done`.
  Edge: walker's first-call logic checks the first child with the filter (`YXF:90`), subsequent calls advance; mutation during iteration unspecified.
- X-F8 Events: `YXmlEvent` with `childListChanged` and `attributesChanged` (for YXmlFragment attributes are always empty; map entries on a fragment cannot be set via API). `delta` works as for arrays (`YE`).

### 4.2 YXmlElement

- X-E1 `nodeName` default `'UNDEFINED'` (`YXE:31`); it is part of the type encoding (`_write` writes ref id + key, `YXE:248-251`) and is immutable.
- X-E2 Attributes are a string-keyed map of any-values (typed string in docs): `setAttribute/getAttribute/hasAttribute/removeAttribute/getAttributes(snapshot?)` (`YXE:138-205`). `getAttribute(missing)`
  => `undefined`. Prelim attrs applied on integrate in insertion order (`YXE:66-72`).
- X-E3 `toString()` (`YXE:113-130`): `<lowercased-nodeName attr="value" ...>children</nodeName>`; attributes sorted by key (JS default sort, UTF-16 code unit order); `nodeName.toLocaleLowerCase()` -
  **culture-sensitive lowercasing** (Turkish `I`!) - port must pick invariant lowercasing or replicate; attribute values are stringified with `+` (so numbers print as `42`, objects `[object Object]`,
  `undefined`, no escaping of `"`/`<`/`&`). Test: escaping not performed; attribute order sorted; nested children.
- X-E4 `nextSibling/prevSibling` = neighbour Items in the **parent's** list including deleted? Uses `_item.next/prev` (`YXE:43-54`; `Item.next` skips deleted, `Item.js:538-558`), returns the
  neighbour's type, `null` when none or unattached. UT testSiblings `y-xml.tests.js:138-152`: `first.nextSibling === second`, `first.parent === yxml`, `yxml.parent === null`.
- X-E5 Events: attribute changes => `attributesChanged` Set contains the key; child changes => `childListChanged` (UT testEvents `y-xml.tests.js:65-103`); both on local and remote; removal of an
  attribute also reports the key.
- X-E6 `clone` copies attributes and deep-clones children (`YXE:90-100`); clone is unattached; assert `clone.toString() === original.toString()` after attach (UT testClone).
- X-E7 Element attribute events `changes.keys` use map semantics (section 2.3).
- X-E8 `getXmlElement(name)` on the doc creates a root element with nodeName `'UNDEFINED'` (constructor default) - `Doc.get` constructs with no args (`Doc.js:218`). Its `toString()` is
  `<undefined>...</undefined>`. Pin.

### 4.3 YXmlText

- X-T1 All YText behaviour (section 3) plus attributes plus siblings. Embeds and formats allowed.
- X-T2 `toString()` renders formatting as nested tags (`YXT:68-103`): each attribute *name* becomes a tag whose attributes are the (sub-)object's keys: `{bold:true}` renders as `<bold>text</bold>` (value
  `true` is an object? no: `for key in true` iterates nothing) ; `{link:{href:'x'}}` renders `<link href="x">text</link>`. Tag nesting is alphabetical by attribute name; inner attrs sorted by key; closes in reverse.
  No escaping. Embeds are appended as `delta.insert` string coercion (`[object Object]`). Tests: pin with oracle outputs for several attribute shapes incl. empty-object attributes (UT testFormattingBug).
- X-T3 `toJSON()` = `toString()` (`YXT:105-107`) so `YXmlText.toJSON()` includes tags, unlike `YText.toJSON()` (plain text). Differential-test both.
- X-T4 Events are `YTextEvent` (Table 0.1).
- X-T5 `toDOM` out of scope.

### 4.4 YXmlHook

- X-H1 A named YMap (`hookName`, written as key in `_write` `YXH:84-87`). All YMap semantics (section 2) apply, including events (`YMapEvent`). `toDOM` out of scope.
- X-H2 `clone` shallow-copies values (T-0.9). `_copy` preserves `hookName`.
- X-H3 Hook name round trip through encode/decode (cross-check with encoding ticket): `readYXmlHook(decoder) => new YXmlHook(decoder.readKey())`.

### 4.5 Type-ref ids (for decode dispatch tests)

`YArrayRefID=0, YMapRefID=1, YTextRefID=2, YXmlElementRefID=3, YXmlFragmentRefID=4, YXmlHookRefID=5, YXmlTextRefID=6` (verified at `yjs/src/structs/ContentType.js:28-34`; wire-format constants). Each concrete type's `_write` emits the ref id (+ key for Element/Hook).

---

## 5. YEvent (common)

- V-1 Fields: `target`, `currentTarget` (initially == target), `transaction`, lazily `path`, `keys`, `delta`, `changes` (`YE:16-53`).
- V-2 `path` (`YE:68-70`, `getPathTo` `YE:256-277`): from `currentTarget` down to `target`; map parents contribute the key (`parentSub`), list parents contribute the *index among live countable
  items* (`YE:263-273`); empty `[]` when target === currentTarget. Property: walking `type.get(path[i])` reaches `target`. UT testObservedeepIndexes `y-array.tests.js:363-382` => `['my-array', 3]`;
  testPathsOfSiblingEvents `y-map.tests.js:391-421` => `[['map'],['map','text1']]`. Path index for a YXmlText/Element child of YXmlFragment counts units of live items (each ContentType item length 1).
- V-3 `path` is cached in `_path`; `observeDeep` resets `_path = null` and sets `currentTarget` before every deep handler call because the event object is **shared** by all ancestors
  (`TX:296-305`, fix for yjs#768; UT testDeepEventPathsWithMultipleDeepObservers `y-map.tests.js:423-476`: `observedA` paths `['b']`,`['c']`, `observedC` path `[]`).
  Consequence: a handler that stores the event object and reads `path`/`currentTarget` later sees the values of the *last* handler invocation. Test by storing events from two nested deep
  observers and reading after the transaction.
- V-4 `adds(struct)`: `struct.id.clock >= beforeState[client]` (`YE:160-162`). `deletes(struct)`: id in transaction deleteSet (`YE:80-82`). Both are true for "added and deleted in same txn".
- V-5 `keys` semantics: M-E2 table; derived from the *current* head item for the key and its `left` chain (`YE:103-116`).
- V-6 `changes` / `delta` for list types: algorithm `YE:172-236`. Merging rules: consecutive deletes sum; consecutive inserts concatenate into one array; consecutive retains sum; **trailing retain
  dropped** (`YE:229`), leading retain kept. Note: `retain` ops from untouched content are emitted even when they separate two changes. Test matrix: insert at head/middle/tail, delete head/middle/tail, insert+delete adjacent (`[{delete:2},{insert:[..]}]` ordering: items are visited in list order, so a deleted run before an insert yields delete then insert; deleted items AFTER inserted (same position) order depends on item order - pin with oracle).
- V-7 Deleted-then-reinserted values count separately (no "update" for lists).
- V-8 Events are only created for types in `transaction.changed`; i.e. types that existed before the transaction (`TX:159-164`). A type created in the transaction never gets its own event but its
  ancestors do (via the parent's change).
- V-9 `transaction.changed` maps type -> Set of parentSubs (`null` for list changes); a type that only had its **children** changed is not in `changed` but is in `changedParentTypes` (deep only).

---

## 6. observe / observeDeep and transaction/event ordering

### 6.1 Transaction lifecycle (single outermost `doc.transact`)

Order of operations (`TX:415-451`, `TX:260-402`):

1. `beforeAllTransactions` (only when this is the first queued transaction, `TX:426-428`), then `beforeTransaction(tr, doc)` (`TX:429`).
2. User function runs; nested `transact` calls reuse the same transaction (`TX:422-433`), so inner `origin` arguments are ignored (inner origin does NOT replace outer).
3. On exit of the outermost call (and only if this transaction is first in the cleanup queue): `cleanupTransactions` index 0:
   a. `sortAndMergeDeleteSet`, `afterState` computed (`TX:268-269`).
   b. `beforeObserverCalls(tr, doc)` (`TX:270`).
   c. Observers: for each entry of `transaction.changed` **in map insertion order**, call `type._callObserver` (which builds the event and calls `observe` handlers) unless the type's item is deleted (`TX:280-286`).
      `_callObserver` also fills `changedParentTypes` (`AT:243-255`), so entries are appended in order (child, parent, grandparent, ...).
   d. After step c's closures: deep observers: for each entry of `changedParentTypes` (insertion order) that has at least one `observeDeep` handler and is not deleted: filter out events whose target is deleted, set `currentTarget`,
      reset `_path`, **stable sort by `path.length` ascending** (JS `Array.sort` is stable on modern engines; C# `List.Sort` is NOT stable - use `OrderBy`/stable insertion) and call handlers `(events[], transaction)` (`TX:287-313`).
   e. `afterTransaction(tr, doc)` (`TX:315`).
   f. YText formatting cleanup if `_needFormattingCleanup` (`TX:316-320`) - runs a new transaction (nested into the cleanup queue).
   g. `finally`: GC (if `doc.gc`), merge structs, client-id collision check (`TX:360-363`), `afterTransactionCleanup` (`TX:365`), `update` (V1 encoded, only if there are listeners and content) then `updateV2`
      (`TX:366-379`), subdocs bookkeeping and `subdocs` event (`TX:380-392`).
   h. If more queued transactions (created by observers) process the next recursively (`TX:394-399`); else `afterAllTransactions(doc, transactions[])` (`TX:396`).
- TX-1 Event order (single transaction, no nested): `beforeAllTransactions` < `beforeTransaction` < [user code] < `beforeObserverCalls` < type `observe` handlers (in `changed` order) < `observeDeep` handlers (ordered by
  `changedParentTypes` insertion, events inside each sorted by path length) < `afterTransaction` < (YText cleanup transaction, if any) < `afterTransactionCleanup` < `update` < `updateV2` < `subdocs` < `afterAllTransactions`.
  UT testDeepEventPathsWithMultipleDeepObservers pins `['observe c','observeDeep a','observeDeep c','afterTransaction']`. Build a generic "recorder" test that logs all of these and compare with the oracle log for ~10 scenarios.
- TX-2 `observe` handlers for *all* changed types run before *any* deep handler (even a deep handler on a child), because deep calls are queued in the second closure (`TX:287`).
- TX-3 `doc.transact(f)` with an empty body still emits `beforeTransaction`, `beforeObserverCalls`, `afterTransaction`, `afterTransactionCleanup`, `afterAllTransactions`, but **no `update`** (`TX:131`).
- TX-4 `transact` returns `f`'s return value (`TX:450`). The transaction object passed to `f`; `origin` defaults to `null` (`TX:415`, `Doc.js:184`) - hence `transaction.origin === null` for plain mutations.
  `transaction.local` is `true` for `transact`; `applyUpdate` uses `local=false`.
- TX-5 Nested `transact` inside `f`: single transaction, single event per type, one `update` (UT testBasicInsertAndDelete uses `users[0].transact(() => {insert; delete})`).
- TX-6 Mutation inside an observer: creates a *new* transaction appended to `_transactionCleanups` and processed after the current cleanup completes - never nested, never interleaved with the current transaction's
  remaining observers (`TX:431-447`, UT testNestedObserverEvents, doc.tests testAfterTransactionRecursion/testOriginInTransaction: origins `['first','cleanup','nested']`).
  `afterAllTransactions` fires once, with the list of all transactions processed in this cycle (`TX:396`). Test with chains of depth 3, plus an observer that mutates unconditionally guarded by a counter.
- TX-7 Exceptions: see M-E6. A throwing handler: remaining handlers and post-processing still execute; the first error is rethrown from the outermost call. If an exception is thrown from user code `f` itself, cleanup still runs
  (`TX:434-448` finally) and the thrown error propagates; partial mutations stay committed (no rollback). Test: assert `update` emitted and data present.
- TX-8 `transaction.changed`: Map<type, Set<parentSub|null>>; `changedParentTypes`; `deleteSet`; `beforeState`/`afterState` (state vectors); `meta` map; `subdocs*` sets (`TX:48-123`). Port exposes at least `Origin`, `Local`,
  `BeforeState`, `AfterState`, `DeleteSet`, `Changed`, `ChangedParentTypes`, `Meta`; tests assert their contents for scripted edits (e.g. `afterState.get(client) - beforeState.get(client)` == number of inserted units).
- TX-9 `observe` is NOT called for a type whose `_item.deleted` at cleanup time (`TX:282`), e.g. a map whose parent key was overwritten in the same transaction after being modified. Test: create child map, modify, then overwrite parent key in
  the same txn => child observer not called; parent observer called.
- TX-10 Event dedupe: a type is observed once per transaction regardless of how many ops. A child changed *and* parent changed in the same txn: both `observe`d independently; parent's `observeDeep` gets both events (child first
  or parent first by path length, UT testObserveDeepEventOrder `y-array.tests.js:339-361`: `path` lengths non-decreasing).
- TX-11 Deep observer on a type whose own `observe` is not registered still works (`TX:292`: only checks `_dEH.l.length`).
- TX-12 Handler list mutation while dispatching: `observe`/`unobserve` during dispatch - `removeEventHandlerListener` replaces the array (`EventHandler.js:333`) so the in-flight `callAll` iterates the old array; added handlers
  during dispatch (`push`) do mutate `l` in place and may be called in the same dispatch. Document and test both (port must snapshot or replicate).
- TX-13 `unobserveDeep` symmetrical to `unobserve` (`AT:397-399`).
- TX-14 Remote updates: `applyUpdate(doc, bytes, origin)` produces a transaction with `local=false` and `origin` = given; event `target` is the local type; `delta` computed relative to local state before apply.
  Search markers cleared (`AT:360`).
- TX-15 `doc.on('update')` handlers are only encoded when listeners exist (`TX:366`): `doc._observers.has('update')`; port may encode lazily but event args `(update, origin, doc, transaction)` must match.
- TX-16 Same-client-id guard (`TX:360-363`): remote update that moves our own clock changes `clientID` (UT testClientIdDuplicateChange) - belongs to Doc ticket, flagged.
- TX-17 `YEvent` data availability: `event.transaction.doc._transactionCleanups.length === 0` => compute error (M-E5). Port: define "events are valid only during dispatch; computing afterwards throws".

### 6.2 Which types appear in `changedParentTypes`

- TX-18 Every changed type and all its ancestors up to the root get the event appended (`AT:246-253`). A root type with an `observeDeep` handler receives the events of its entire subtree (including from nested YXml/YText/YMap/YArray).
  Event objects are the same instances across ancestors (V-3).
- TX-19 Cross-type depth: deep events through YMap->YArray->YMap->YText->embedded YMap all appear with correct `path` mixing keys and indices (UT testObservedeepIndexes, testPathsOfSiblingEvents, y-map.tests testObserveDeepProperties
  `y-map.tests.js:330-365`, testObserversUsingObservedeep `367-389`).

---

## 7. Suggested test groups for the C# suite (derived)

| Group | Contents | Oracle |
|---|---|---|
| G1 list-type semantic table | A-1..A-17, X-F2 (same cases run against YArray and YXmlFragment) | scripted JS runner -> JSON expectation files |
| G2 map-type semantic table | M-1..M-12, X-H1 | same |
| G3 text plain/format/embed table | T-1..T-9, F-1..F-9, E-1..E-6, D-1..D-4 | same + UT values |
| G4 event deltas | A-E2, M-E2, Y-2, V-6 matrices; every row computed by JS and stored | same |
| G5 event ordering recorder | TX-1..TX-13: log of all doc/type events per scenario | same |
| G6 errors | A-2/A-5 throws, T-0.10/0.11 unsupported content, `Length exceeded!`, Doc type conflict, M-E5 compute-after-dispatch, M-E6 observer exceptions | exception type + message mapping table |
| G7 xml | X-F*, X-E*, X-T*, X-H* with `toString` pinned outputs | same |
| G8 property/fuzz | random ops on YArray/YMap/YText vs. naive model (List, Dictionary, string+attribute run model) + two-replica convergence | UT `testRepeatGenerate*` generators (`y-array.tests.js:520-691`, `y-map.tests.js:672-818`, `y-text.tests.js:2290-2610`) ported with the same PRNG seeds |

A workable oracle harness: a small Node script under `tools/` that executes a JSON scenario (ops + recorder) against `yjs/` and writes `expected/<id>.json`; the C# tests replay the same scenario file. Scenario DSL needs: op list per
doc, `transact` grouping with origin, `sync(a,b)`, `observe/observeDeep` recorders (store `delta`, `keys`, `path`, `target path`, `currentTarget path`), `doc.on(<event>)` recorders.

---

## 8. Open questions / decisions for the API contract ticket (TASK-1.14)

1. Representation of "any" values (JSON-like content, attribute values, embeds): `JsonNode`/`JsonElement`/`object`? Shallow `equalAttrs` semantics (F-5) depend on it.
2. `changes.added/deleted` (Set<Item>) - expose Items publicly? Suggest exposing counts only, or not at all (YText never populates them, Y-3).
3. Event object lifetime (V-3, M-E5, Y-4): C# should either invalidate after dispatch or snapshot eagerly; but eager `delta` for YText has side effects (Y-4) - decide whether laziness is part of the contract.
4. Index validation (A-2, A-20, T-1, T-2): Yjs is inconsistent (array throws on overflow, text clamps). Replicate exactly or normalise? Tests above assume replicate for observable positive cases, throw for negative.
5. Exception model for observer failures (M-E6, TX-7): first-error rethrow vs AggregateException.
6. `YXmlText.toString/toJSON`, `YXmlElement.toString` formatting rules incl. `toLocaleLowerCase` (X-E3); invariant lowercasing recommended.
7. `toDOM` and anything referencing `document` is out of scope.
8. Sorting: deep-event sorting must be stable (TX-1 step d).
9. Unstable/undefined behaviour list to exclude from differential testing: A-12 (mutation during iteration), A-20, T-0.13, M-11 `__proto__`.
10. Verify wire constants in 4.5 and the `slice` negative-start quirk (A-8) against the oracle before freezing expectations.

## 9. Upstream test inventory worth porting verbatim (behavioural, not fuzz)

- y-array: testSlice, testArrayFrom, testLengthIssue, testLengthIssue2, testInsertAndDeleteEvents, testNestedObserverEvents, testInsertAndDeleteEventsForTypes(2), testObserveDeepEventOrder, testObservedeepIndexes, testChangeEvent,
  testNewChildDoesNotEmitEventInTransaction, testEventTargetIsSetCorrectlyOnLocal/Remote, testIteratingArrayContainingTypes (`yjs/tests/y-array.tests.js`).
- y-map: testMapEventError, testMapHavingIterableAsConstructorParamTests, testBasicMapTests, testGetAndSetAndDeleteOfMapProperty, testSetAndClearOfMapProperties, testObserveDeepProperties, testObserversUsingObservedeep,
  testPathsOfSiblingEvents, testDeepEventPathsWithMultipleDeepObservers, testThrowsAddAndUpdateAndDeleteEvents, testThrowsDeleteEventsOnClear, testChangeEvent, testYmapEventExceptionsShouldCompleteTransaction (`yjs/tests/y-map.tests.js`).
- y-text: testDeltaBug/2 (large delta fixtures), testDeltaAfterConcurrentFormatting, testBasicInsertAndDelete, testBasicFormat, testFalsyFormats, testMultilineFormat, testNotMergeEmptyLinesFormat, testPreserveAttributesThroughDelete,
  testGetDeltaWithEmbeds, testTypesAsEmbed, testToJson, testToDeltaEmbedAttributes, testToDeltaEmbedNoAttributes, testFormattingRemoved(InMidText), testFormattingDeltaUnnecessaryAttributeChange, testSplitSurrogateCharacter,
  testSearchMarkerBug1, testFormattingBug, testDeleteFormatting (`yjs/tests/y-text.tests.js`).
- y-xml: all (testSetProperty, testHasProperty, testEvents, testTreewalker, testYtextAttributes, testSiblings, testInsertafter, testClone, testFormattingBug, testElement) (`yjs/tests/y-xml.tests.js`).
- doc: testAfterTransactionRecursion, testOriginInTransaction, testGetTypeEmptyId, testToJSON (`yjs/tests/doc.tests.js`).

## 10. Unverified items (read the source/run the oracle before writing tests)

- A-8 negative `slice` start beyond length: reasoned from code only, not executed.
- T-9 exact output of `retain` beyond the end: reasoned from `formatText` (`YT:338-349`), not executed.
- D-1 canonical-ness of `toDelta` after format round trips: reasoned, not executed.
- 4.5 ref-id numbers: verified at `ContentType.js:28-34`.
- TX-12 mid-dispatch handler list mutation: reasoned from `EventHandler.js` + `lib0/function.js` `callAll`, which iterates the passed array by index (so a handler pushed onto the same array mid-dispatch IS called; one removed via `unobserve` is not skipped because the array is replaced). Confirm by running the oracle.
