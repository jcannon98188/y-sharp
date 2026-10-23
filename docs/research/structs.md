# Research: struct and integration behaviour inventory (TASK-1.1)

Oracle: `yjs/` v13.6.33 (submodule). All refs are `file:line` relative to `yjs/src/`.
Scope: `structs/*` (AbstractStruct, Item, GC, Skip, Content*), `utils/StructStore.js`,
`utils/Transaction.js`, plus the struct-integration/pending code in `utils/encoding.js`
and delete-set application in `utils/DeleteSet.js` (they own pending-struct behaviour).
Each entry is phrased so a test can assert it (state in, observable out). "Observable" means via
public API, `encodeStateAsUpdate`, `getStateVector`, or internal store inspection where the port exposes it.

Out of scope here (other tickets): YText/YArray/YMap type semantics, UndoManager, lib0 primitives,
update V1/V2 byte layout, DeleteSet encoding. `redoItem` is listed (B) because it lives in Item.js.

Port notes marked **[port]** flag JS-specific behaviour a C# port must decide on.

---

## A. Struct model and invariants

A1. `AbstractStruct(id, length)` stores id and length; `deleted` getter, `write`, `integrate` throw `methodUnimplemented`; base `mergeWith` returns false. (`structs/AbstractStruct.js:12-50`)

A2. **Per-client contiguity**: structs for a client form a gap-free, ordered array; for adjacent l,r: `l.id.clock + l.length === r.id.clock`. `integrityCheck` throws `Error('StructStore failed integrity check')` otherwise. (`utils/StructStore.js:69-79`) Test: after any random op/update sequence, integrityCheck passes.

A3. `addStruct` throws `unexpectedCase` if the new struct's clock != last.clock+last.length for an existing client; for a new client, any starting clock is accepted (no check it is 0). (`utils/StructStore.js:88-100`) Edge: first struct of an unseen client with clock>0 is silently stored (callers prevent this via the offset<0 check, K2).

A4. `getState(store, client)` = last.clock+last.length, or 0 for unknown client; `getStateVector` maps every client to the same. (`utils/StructStore.js:37-61`)

A5. Item `info` bit field: bit1 keep, bit2 countable, bit3 deleted, bit4 search-marker. Countable is initialised from `content.isCountable()`. Setters toggle only on change. `markDeleted` sets bit3 without touching parent length. (`structs/Item.js:306-312, 320-363`)

A6. `Item.lastId` = `id` when length==1 (same object reused), else new ID(client, clock+length-1). (`structs/Item.js:560-563`) **[port]** aliasing is an optimisation only; ID should be a value type.

A7. `Item.next` / `Item.prev` skip deleted neighbours. (`structs/Item.js:538-555`)

A8. Item constructor length = `content.getLength()`; `redone` starts null. (`structs/Item.js:261-313`)

## B. Item helper functions

B1. `followRedone(store, id)`: follows `redone` chain starting at the item containing id, carrying the intra-item `diff`; stops when `redone` is null or the found struct is not an Item (GC). Returns `{item, diff}`. (`structs/Item.js:39-57`) Edge: id inside the middle of an item (diff>0) then redone pointer: next lookup is `redone.clock + diff` (via nextID adjusted on next loop iteration).

B2. `keepItem(item, keep)`: walks item and ancestors (`parent._item`) setting `keep` until it reaches null or an item already at the requested value. (`structs/Item.js:68-73`) Test: keep(true) on a nested item marks all ancestors; stops early at an already-kept ancestor.

B3. **splitItem(tx, left, diff)** (`structs/Item.js:85-121`):
  - right.id = (client, clock+diff); right.origin = (client, clock+diff-1); right.rightOrigin = left.rightOrigin; right.parent/parentSub copied; right.content = `left.content.splice(diff)`.
  - copies `deleted` and `keep`; if `left.redone != null`, right.redone = left.redone + diff (same client).
  - relinks: left.right = right; old right's `.left` = right; **left.rightOrigin is NOT changed** (comment at :107: would break sync).
  - pushes right to `tx._mergeStructs`.
  - if `right.parentSub != null && right.right == null`, `parent._map[parentSub] = right`.
  - `left.length = diff`. Does not insert into the store array (callers splice).
  - Does not adjust parent `_length` (total unchanged), search markers, or delete set.
  Test: split a length-5 string item at 2; assert ids, origins, lengths 2+3, linked list, delete set unchanged, _map head for map-key items repoints to right.
  Edge: diff must satisfy 0<diff<length; no guard (content.splice on Binary/Embed/Type/Doc/Format throws, see D).

B4. `redoItem(...)` (`structs/Item.js:143-245`), used by UndoManager:
  - returns `getItemCleanStart(redone)` if `item.redone != null` (:148-150).
  - if parent item is deleted: needs it redone first; recursively redoes parent only if `redoitems.has(parent)`; else returns null; then follows `redone` chain of parent (:161-169).
  - array item (parentSub null): left = nearest left (walking `.left`, following `.redone`) whose parent item equals the target parent; right likewise walking `.right` (:172-206).
  - map item: if `item.right && !ignoreRemoteMapChanges`, left walks right over items that are redone / in `itemsToDelete` / in undo or redo stack deletions; if that ends with `left.right != null` it returns null (conflict with remote change) (:209-222). Else left = current map value (:224). Cross-parent left is replaced by current map value (#757 fix, :227-229).
  - creates new Item at own `nextClock`, content = `item.content.copy()`, sets `item.redone = nextId`, `keepItem(new, true)`, integrates at offset 0 (:231-244).
  Tests: redo of deleted array item re-inserts in the same neighbourhood; redo of map item with a concurrent remote overwrite returns null; redo when parent deleted and not in redoitems returns null.

## C. Item.getMissing (dependency resolution before integrate)

C1. Returns the **client id** of the first missing dependency, checked in order origin, rightOrigin, parent-as-ID. A dependency is missing iff its client differs from `this.id.client` and `dep.clock >= getState(store, dep.client)`. Same-client deps are never reported (ordering guaranteed by contiguity). (`structs/Item.js:373-381`) Test: item whose origin client has state <= origin.clock yields that client; order of checks affects which client is returned when several are missing.

C2. If all present: `left = getItemCleanEnd(origin)` (splits so origin is last id of left), `origin = left.lastId`; `right = getItemCleanStart(rightOrigin)`, `rightOrigin = right.id`. **Side effect: splits structs in the store, even if the item is never integrated.** (`:385-392`)

C3. Parent resolution (`:393-411`):
  - if left or right is a `GC` -> `parent = null` (item will be turned into GC at integrate).
  - else if parent unset: copy `parent`+`parentSub` from left if Item, else from right if Item.
  - else if parent is an ID: `getItem(store, parent)`; GC -> parent=null; otherwise `parent = content.type`. **[port]** note `(parentItem.content).type` assumes ContentType; malformed update pointing to a non-type item would throw TypeError/NullRef — error-path for TASK-1.12/1.13.
  Edge: left is Item but right is GC => parent null (GC wins, since condition checks either).
  Edge: parentSub is overwritten from left/right only when parent was null (info bits 7/8 set, no explicit parent).

C4. GC.getMissing and Skip.getMissing always return null. (`structs/GC.js:57`, `structs/Skip.js:56`)

## D. Item.integrate (YATA conflict resolution) — `structs/Item.js:419-533`

D1. **Offset handling** (`offset>0`, partially-known struct): `id.clock += offset`, `left = getItemCleanEnd(client, newclock-1)`, `origin = left.lastId`, `content = content.splice(offset)`, `length -= offset`. Note: mutates the id and does not reset `rightOrigin`. (:420-426) Test: apply an update overlapping known state; only the unseen suffix is integrated, with origin pointing to last known char.

D2. **Conflict loop is entered only if** `(!left && (!right || right.left != null)) || (left && left.right != right)` i.e. the natural spot is not already adjacent. Otherwise fast path: no scan. (:429)

D3. Scan start `o`: `left.right` if left; else for map items the leftmost item of the key chain (walk `.left` from `_map.get(parentSub)`); else `parent._start`. Scan until `o === this.right` or null. (:440-463)

D4. Per-`o` rules (:463-486), with `conflictingItems` (set C) and `itemsBeforeOrigin` (set B), both receiving `o` on each iteration:
  - **Case 1** `this.origin == o.origin` (compareIDs, null==null counts equal): if `o.id.client < this.id.client` -> `left = o; C.clear()`. Else if `this.rightOrigin == o.rightOrigin` -> `break` (this goes before o). Else continue (tie but different right origins).
  - **Case 2** `o.origin != null && B contains getItem(o.origin)` (no split): if `C` does not contain that origin item -> `left = o; C.clear()`; else keep scanning.
  - **Else** `break`.
  - `o = o.right`.
  Tests (each is a classic YATA scenario, drive via two docs and `applyUpdate` in both orders, assert identical results): same origin different clients (lower client id goes left); same origin same rightOrigin with greater client; interleaving case 2 with items inserted by third client in the conflict zone; null origin (insert at start) conflicts; map-key conflicts via leftmost walk.
  Determinism property: for any permutation of delivering a set of updates, final sequence is identical (convergence).

D5. **Linking** (:490-516): if `left != null`: `right = left.right; left.right = this`. Else: for map item, `right` = leftmost of key chain (does not update `_start`); for list item `right = parent._start; parent._start = this`. Then `right.left = this` if right non-null; else if parentSub != null: `parent._map.set(parentSub, this)` and `left.delete(tx)` if left non-null (last-writer-wins: previous value deleted).

D6. Parent `_length += length` only if `parentSub === null && countable && !deleted`. (:518-520)

D7. Order of effects after linking: `addStruct(store, this)` -> `content.integrate(tx,this)` -> `addChangedTypeToTransaction(tx, parent, parentSub)` -> delete self if parent item is deleted **or** (`parentSub != null && right != null`, i.e. not the current map value). (:521-528) Test: item integrated into deleted parent is deleted immediately and is in the tx delete set; map write that loses a concurrent race is stored deleted.

D8. **parent == null branch** (:529-532): item is replaced by `GC(id, length)` integrated at offset 0 (the offset was already applied above). Test: update whose origin was GC'd on receiver produces a GC struct covering the full length, state vector still advances.

D9. `integrate` order matters for ContentDeleted: `content.integrate` marks item deleted and adds to delete set **after** `_length` accounting (so a ContentDeleted item never contributes length: it is not countable, see E-ContentDeleted).

## E. Item.delete / gc / mergeWith / write

E1. `delete(tx)` idempotent when already deleted. Otherwise: if countable and parentSub==null, `parent._length -= length`; `markDeleted`; `addToDeleteSet(tx.deleteSet, client, clock, length)`; `addChangedTypeToTransaction`; `content.delete(tx)`. (:616-628) Test: delete twice -> single delete-set entry, length decremented once.

E2. `gc(store, parentGCd)`: throws `unexpectedCase` if not deleted. Calls `content.gc(store)`; if `parentGCd` replaces the struct in the store with `GC(id,length)` else `content = new ContentDeleted(length)` (item stays, `countable` bit stays as it was). (:634-644) Edge: after gc(false), `item.countable` is still true although ContentDeleted.isCountable()==false; length accounting already done at delete time.

E3. **mergeWith(right)** returns true only if ALL hold (:572-583): same class; `right.origin == this.lastId`; `this.right === right`; `rightOrigin` equal; same client; `this.clock+this.length == right.clock`; same `deleted`; both `redone === null`; same content class; and `content.mergeWith` returns true.
  Side effects on success: search-marker entries pointing to right are repointed to this (index -= this.length if this is countable & not deleted); `keep` OR-ed; `this.right = right.right` and back-link; `length` summed. (:585-606) Note: parentSub/parent are not compared (implied by origin chain). Test: each condition individually false prevents the merge; markers adjusted.

E4. **write(encoder, offset)** (:655-698): origin = `offset>0 ? (client, clock+offset-1) : this.origin`; info byte = `contentRef & 0x1F | (origin?0x80) | (rightOrigin?0x40) | (parentSub?0x20)`. Writes origin, rightOrigin if present. **Only when both origin and rightOrigin are null** writes parent info: 
  - parent is a type with `_item === null` (root): `writeParentInfo(true)` + `findRootTypeKey(parent)` (throws if not found in `doc.share`).
  - parent type with `_item`: `writeParentInfo(false)` + parentItem.id.
  - `parent` is a string (undefined `_item`): key written as-is; "edge case added by differential updates" (:684).
  - `parent` is an ID: written as ID. Otherwise `error.unexpectedCase()` throws.
  Then parentSub string if non-null (inside the same block, so parentSub is only written when both origins null), then `content.write(encoder, offset)`.
  Test: item with offset>0 encodes synthetic origin; rootless item with origin defined omits parent info; map-key item with null origins writes parentSub.

E5. `readItemContent`/`contentRefs` table: ref 0 and ref 10 throw `unexpectedCase`; 1 Deleted, 2 JSON, 3 Binary, 4 String, 5 Embed, 6 Format, 7 Type, 8 Any, 9 Doc. Index is `info & 0x1F`, so refs 11..31 index `undefined` -> TypeError. (`structs/Item.js:705-724`) Test (error path): info with ref 11..31 must fail deterministically (JS: "not a function"); port should raise a defined error (TASK-1.12).

## F. GC struct (`structs/GC.js`)

F1. ref number 0; `deleted` always true; `delete()` no-op. (:7-17)
F2. `mergeWith`: true iff same class; lengths add. (:23-29) GC+GC adjacent always merge (no id adjacency check — callers pass adjacent store neighbours).
F3. `integrate(tx, offset)`: if offset>0 `id.clock += offset; length -= offset`; `addStruct`. Adds nothing to the delete set (GC'd ranges are assumed already in delete set derived from store, see `createDeleteSetFromStructStore`). (:35-41)
F4. `write`: `writeInfo(0)`, `writeLen(length - offset)`. (:47-50)
F5. `getMissing` -> null. (:57)

## G. Skip struct (`structs/Skip.js`)

G1. ref number 10; `deleted` true; `delete()` no-op; `mergeWith` like GC. (:8-29)
G2. `integrate` always throws `unexpectedCase` ("skip structs cannot be integrated"). Skip never enters the StructStore. (:36-39)
G3. `write`: `writeInfo(10)` + **raw VarUint** length (not `writeLen`, which in V2 is RLE-encoded) minus offset. (:45-49) Test: byte equality V1/V2 differs for GC vs Skip length encoding.
G4. Skips only appear in updates (produced by `mergeUpdates`/`diffUpdate` when a client range is missing); decoder creates `Skip` and the integrator ignores it (see I3).

## H. Content types

For each: `getLength / getContent / isCountable / copy / splice / mergeWith / integrate / delete / gc / write / getRef / read`.

| Content | ref | length | countable | splice | mergeWith | notes |
|---|---|---|---|---|---|---|
| ContentDeleted | 1 | `len` | false | right=len-offset, left=offset | always true, lens add (no type check; Item.mergeWith ensures) | `integrate` adds `(client,clock,len)` to tx delete set and `item.markDeleted()` (`ContentDeleted.js:65-68`); write = `writeLen(len-offset)`; getContent `[]` |
| ContentJSON | 2 | arr.length | true | slice both sides | concat, true | write: per element `JSON.stringify`, `undefined` -> literal string `"undefined"`; read inverts (`ContentJSON.js:83-90, 106-118`). Legacy; **[port]** `undefined` sentinel and JSON.parse failure path |
| ContentBinary | 3 | 1 | true | **throws** `methodUnimplemented` | false | `writeBuf`; getContent `[content]` |
| ContentString | 4 | `str.length` (UTF-16 code units) | true | see H1 | string concat | `write` slices from offset; getContent = `str.split('')` (code units) |
| ContentEmbed | 5 | 1 | true | throws | false | `writeJSON` |
| ContentFormat | 6 | 1 | **false** | throws | false | `integrate` sets `parent._searchMarker = null` and `parent._hasFormatting = true` (`ContentFormat.js:68-72`); getContent `[]`; write key via `writeKey` + `writeJSON(value)` |
| ContentType | 7 | 1 | true | throws | false | see H2 |
| ContentAny | 8 | arr.length | true | slice both sides | concat, true | write len then `writeAny` each; dev mode deep-freezes array (`ContentAny.js:19`) **[port]** immutability |
| ContentDoc | 9 | 1 | true | throws | false | see H3 |

H0. `isCountable()==false` for Deleted and Format: they never contribute to `parent._length`, `yarray.length`, or text index. Test: format marks do not shift indices.

H1. **ContentString.splice surrogate handling** (`ContentString.js:51-66`): after splitting at `offset`, if the last code unit of the left part is a high surrogate (0xD800-0xDBFF), left's last unit is replaced by U+FFFD and right's first unit is replaced by U+FFFD (lengths unchanged). Tests: split inside an emoji ("a😀b" at 2); split at boundary leaves pair intact; offset 0/len edge. Note: the check reads `charCodeAt(offset-1)` on the already-truncated `this.str`. Edge: offset==0 reads `charCodeAt(-1)` = NaN -> no replacement. **[port]** C# `string` is also UTF-16, so identical semantic; Length/indices must stay in code units, not code points/text elements.

H2. **ContentType**:
  - `copy()` -> `new ContentType(type._copy())`.
  - `integrate` -> `type._integrate(doc, item)`.
  - `delete(tx)` (:105-128): for every child in the list chain `_start..right` and every `_map` value: not deleted -> `delete(tx)`; already deleted and `id.clock < beforeState[client]` -> push to `tx._mergeStructs` (so old tombstones get merged); then `tx.changed.delete(type)` (no events for a deleted type). Note `_map` iteration visits only the **latest** item per key, not older chain entries (older ones are already deleted).
  - `gc(store)` (:133-147): every list child `gc(store, true)` (-> GC structs in store); every map chain walked via `.left` `gc(store,true)`; `type._start=null; _map = new Map()`.
  - `write` -> `type._write(encoder)`; read -> `typeRefs[decoder.readTypeRef()](decoder)`; unknown type ref => `typeRefs[n]` undefined -> TypeError (error path).
  Tests: delete a parent containing nested types/map entries marks all descendants deleted in tx.deleteSet; no change events for the deleted type; after gc cycle children appear as GC structs and parent item holds ContentDeleted.

H3. **ContentDoc**:
  - constructor logs `console.error` if `doc._item` already set (not thrown). Builds `opts`: `gc:false` only if gc disabled; `autoLoad:true` if set; `meta` if non-null.
  - `copy()` creates a **new Doc** with the same guid and opts (`createDocFromOpts` -> `shouldLoad = opts.shouldLoad || opts.autoLoad || false`, :11).
  - `integrate`: `doc._item = item`; `tx.subdocsAdded.add(doc)`; if `doc.shouldLoad` also `subdocsLoaded`.
  - `delete`: if doc is in this tx's `subdocsAdded` remove it from there, else add to `subdocsRemoved`.
  - `write`: `writeString(guid)` + `writeAny(opts)`; read creates a new Doc.
  - `gc` no-op. Splice throws.
  Tests: add then delete in same tx emits neither added nor removed; delete in later tx -> removed; autoLoad round-trip through encode/decode.

H4. Throwing `splice` (Binary, Embed, Format, Type, Doc): items with length 1 never need to split; any attempt to split (clean start/end with offset into a len-1 item cannot occur; reachable only from malformed updates, e.g. delete range or origin that falls inside length>... ) — all such ranges lie on item boundaries. Test: crafted update with overlap offset>0 on a len-1 non-splittable item is unreachable (offset<length). Listed so port does not silently implement split for them.

H5. `ContentAny.mergeWith`/`ContentJSON.mergeWith`/`ContentDeleted.mergeWith`/`ContentString.mergeWith` do not type-check `right`; correctness depends on `Item.mergeWith` checking `content.constructor` equality first (E3).

## I. StructStore (`utils/StructStore.js`)

I1. `findIndexSS(structs, clock)`: pivoted binary search returning index of the struct whose range contains clock; fast path if last struct starts exactly at clock; throws `unexpectedCase` if absent or if structs empty (`structs[-1]` undefined -> TypeError). Callers must check state first. (:111-139) Tests: lookup at first, last, each boundary, middle-of-struct clocks; out-of-range -> error. Property: result consistent with linear scan on random layouts. Edge: pivot calc divides by `midclock + mid.length - 1`; when that is 0 (single struct of length 1 at clock 0) -> `0/0 = NaN`, floor NaN: handled only because the early return (clock===midclock) triggers when clock==0; for clock>0 state-check must preclude. **[port]** make the pivot integer-safe.

I2. `find`/`getItem(store,id)`: `structs[findIndexSS]`; unknown client => TypeError (`structs` undefined). (:151-165)

I3. `findIndexCleanStart(tx, structs, clock)`: if the containing struct starts before clock and is an `Item`, splits it and inserts the right half at index+1, returns that index; for GC/Skip it returns the containing index unsplit (GC can't split). (:172-180) Test: result struct starts exactly at clock for Items; for GC, struct.clock <= clock (callers must tolerate).

I4. `getItemCleanStart(tx, id)` -> struct starting at id (splitting). `getItemCleanEnd(tx, store, id)` -> struct ending at id (splitting at `id.clock - struct.clock + 1`); **does not split GC** (checks `constructor !== GC`) and **does not guard Item vs other**. (:192-220)

I5. `replaceStruct(store, old, new)` replaces array slot found via findIndexSS. (:231-234) Used by gc(parentGCd). Lengths must match (not asserted).

I6. `iterateStructs(tx, structs, clockStart, len, f)`: no-op for len 0; clean-starts at clockStart, calls `f` for each struct in range, and splits the final struct at clockEnd if it extends past. (:247-261) Test: callback sees exactly the covered ranges with splitting side effects; len 0 no splitting.

I7. StructStore fields: `clients: Map<number, Array<GC|Item>>`, `pendingStructs: {missing: Map, update: Uint8Array} | null`, `pendingDs: Uint8Array | null`. (:10-24)

## J. Transaction (`utils/Transaction.js`)

J1. Transaction init: `beforeState = getStateVector(store)` snapshot at creation; `afterState` empty until cleanup; `deleteSet` empty; `changed: Map<Type,Set<parentSub|null>>`; `changedParentTypes`; `_mergeStructs`; `origin`; `meta`; `local`; subdoc sets; `_needFormattingCleanup=false`. (:48-122)

J2. `transact(doc, f, origin=null, local=true)` (:415-451):
  - if no active tx, create it, push to `doc._transactionCleanups`; emit `beforeAllTransactions` iff it is the only pending cleanup; emit `beforeTransaction`.
  - nested call reuses the active transaction (origin/local of the nested call are ignored).
  - `f` result returned; in `finally`: only the initial call resets `doc._transaction = null` and, if its transaction is `cleanups[0]`, runs `cleanupTransactions(cleanups, 0)`. A transaction opened *inside* a cleanup (observer) is queued and processed after the current one, not nested.
  - exceptions in `f` still run cleanup (finally) and then propagate.
  Tests: nested transact shares one tx; events order; transact inside observer yields second transaction run after first's cleanup; throw in `f` still triggers cleanup/events and rethrows.

J3. `addChangedTypeToTransaction(tx, type, parentSub)`: records `(type -> parentSub)` only if `type._item === null` (root) **or** the type's item existed before this tx (`item.clock < beforeState[client]`) and is not deleted. Types created in this tx are not "changed". (:159-163)

J4. `nextID(tx)` = (doc.clientID, current state of own client). (:146-149)

J5. `writeUpdateMessageFromTransaction(enc, tx)`: returns false (writes nothing) iff delete set empty and no client's clock changed between beforeState and afterState; else `sortAndMergeDeleteSet(ds)`, `writeStructsFromTransaction`, `writeDeleteSet`; true. (:130-138) Test: empty transaction emits no `update` event; delete-only transaction emits.

J6. **cleanupTransactions** order (:260-400), per transaction:
  1. `sortAndMergeDeleteSet(ds)`; `afterState = getStateVector`; emit `beforeObserverCalls`.
  2. Observer queue `fs` (each called even if earlier throws — `callAll`): per changed type `_callObserver` if type not deleted; then deep observers: for each parent type with `_dEH` listeners and not deleted, filter out events whose target is deleted, set `currentTarget`, reset `_path`, sort by path length, call; then emit `afterTransaction`; then `cleanupYTextAfterTransaction` if `_needFormattingCleanup`.
  3. `finally` (always, even if observers throw): 
     a. if `doc.gc`: `tryGcDeleteSet(ds, store, doc.gcFilter)`;
     b. `tryMergeDeleteSet(ds, store)`;
     c. for each client whose clock advanced, merge-left from the end down to the first new struct (`firstChangePos = max(findIndexSS(beforeClock),1)`);
     d. process `_mergeStructs` from last to first: try merge the right neighbour then the struct itself;
     e. if `!tx.local` and own clientID's clock changed during the tx => **regenerate `doc.clientID`** (another peer is using this id) with a console message;
     f. emit `afterTransactionCleanup`;
     g. emit `update` (V1 bytes) if there are listeners and `hasContent`; then `updateV2` likewise (separate encoders);
     h. subdocs: set `subdoc.clientID = doc.clientID`, default `collectionid`, add to `doc.subdocs`; remove removed; emit `subdocs`; destroy removed subdocs;
     i. if last pending cleanup: reset `doc._transactionCleanups=[]` and emit `afterAllTransactions`; else recurse to the next tx.
  Tests: event order table; gc disabled keeps content; clientID regeneration (apply remote update containing structs for own clientID); update event bytes decode to the same changes; `update` listener absent => no encoding (observable via no emit); throw in observer does not skip GC/merge/update emission, error propagates after.

J7. **tryGcDeleteSet** (:199-220): for each delete range, find structs overlapping; for each `Item` that is deleted, not `keep`, and passes `gcFilter` -> `item.gc(store, false)` (content -> ContentDeleted; or content.gc for types makes children GC). Walks `ds` ranges right-to-left. (Inner `if (deleteItem.clock + len <= struct.id.clock) break` is redundant with loop condition.) Tests: keep flag prevents GC; gcFilter false prevents; doc.gc=false skips entirely; GC'd text item keeps its length/id but loses content (`getContent()==[]`).

J8. **tryMergeDeleteSet / tryToMergeWithLefts** (:171-244): merges adjacent structs right-to-left within ranges, starting at the neighbour right of the range. `tryToMergeWithLefts` only attempts when `deleted` equal and constructor equal, calls `left.mergeWith(right)`; if right was the current `_map` entry for its key, the map entry is repointed to `left`; returns number of structs merged and the array is spliced. Tests: after deleting and cleanup, adjacent deleted string items collapse into one ContentDeleted item (observable via `encodeStateAsUpdate` size / store struct count); live items merge when they are contiguous same-client with matching origin chain.

## K. Update application, pending structs and delete sets (`utils/encoding.js`, `utils/DeleteSet.js`)

K1. **readClientsStructRefs** (`encoding.js:112-205`): reads `numClients`; per client `numStructs`, client id, start clock; per struct `info`: low-5-bits 0 -> GC (`readLen`); 10 -> Skip (VarUint len, **not** `readLen`); otherwise Item. Item parent info is read only when `(info & (0x80|0x40)) == 0` (both origins absent): `readParentInfo()` true -> `doc.get(readString())` (root type created on demand as `AbstractType`), false -> `readLeftID()`; parentSub read only in that same branch if bit 0x20. Clock advances by struct length. (:157-205)
  Edge: bit 6 (parentSub) is ignored when an origin exists (parentSub inherited from left/right at getMissing). Test: crafted updates with each info-bit combination.

K2. **integrateStructs** (`encoding.js:227-359`): processes clients in **descending client id order** (stack popped from end of ascending-sorted list). Algorithm: for each struct (stackHead):
  - Skip: skipped (no state change) -> subsequent struct of that client sees `offset<0`.
  - `offset = localClock - clock`: **offset<0** (gap before struct) -> struct pushed to stack, `missingSV[client]=min(.., clock-1)`, whole stack moved to rest (K3).
  - else `getMissing`; missing client `m`: stack push; if client m has no remaining refs in this update -> `missingSV[m]=min(.., getState(m))` and stack to rest; else jump to that client's next struct (dependency DFS).
  - else if `offset == 0 || offset < length` -> `integrate(tx, offset)` and `state[client] = clock+length` (cached). **offset >= length (already known entirely) -> silently dropped** (idempotent re-apply).
  Tests: duplicate update application is a no-op (state vector unchanged, no events); partially overlapping update integrates suffix only; out-of-order (causally dependent) updates from 3 clients converge; GC struct in update advances state.

K3. **addStackToRestSS** (:279-298): for each stacked struct, the remaining refs of its client (from the failed index) are moved into `restStructs`, the client is dropped from the work list so already-processed prefix is not repeated. Rest is encoded as a V2 update with an empty delete set (`writeVarUint(0)`) and returned `{missing: Map<client, clock>, update}`. (:350-357)

K4. **readUpdateV2** (:382-449; always `transaction.local = false`):
  - integrate structs -> `restStructs`.
  - if `store.pendingStructs` already exists: `retry=true` if any `pending.missing[client] < getState(client)` (something became applicable); merge rest `missing` (min per client) and `update = mergeUpdatesV2([pending.update, rest.update])`. Else `pendingStructs = restStructs`.
  - delete set: `dsRest = readAndApplyDeleteSet(...)`; if `pendingDs` exists, also re-apply it (skipping 0 structs byte); merged leftovers via `mergeUpdatesV2` (cases 1-4 at :426-434). Else `pendingDs = dsRest`.
  - if `retry`: take `pendingStructs.update`, set `pendingStructs = null`, `applyUpdateV2` it (recursive/nested transaction => same tx).
  Tests: (a) update B depending on A applied first -> `pendingStructs != null`, state unchanged, then apply A -> B applied, pending cleared; (b) 3-level chain A<-B<-C delivered C,B,A and every permutation; (c) multiple independent pending updates merge into a single pending update; (d) `pending.missing` correctness (client -> min clock); (e) `encodeStateAsUpdate` while pending includes pending content (K8).
  Edge: retry happens at most once per readUpdate call at top-level but recursion handles chains.

K5. **readAndApplyDeleteSet** (`DeleteSet.js:278-332`): per client/range `[clock, clockEnd)`:
  - part beyond local `state` (unknown structs) goes to `unappliedDS` (range fully unknown -> entire range; partially -> tail `[state, clockEnd)`), later re-encoded as V2 update with 0 structs and stored as `store.pendingDs`.
  - known part: `findIndexSS`; split first struct if `!deleted && clock > struct.clock`; then for subsequent structs with `clock < clockEnd`: split at clockEnd if extends beyond, then `struct.delete(tx)` if not deleted. GC'd/already-deleted structs are skipped.
  - Casts structs as Item without checking: calling `.delete` on GC is a no-op (GC.delete), splitItem on a GC is avoided only because GC is always `deleted`.
  Tests: delete-set arriving before the inserts it refers to (pendingDs set, applied when structs arrive and pending reapplied); delete range covering partially-known clients; delete range inside the middle of an item splits it three ways; delete of already-GC'd range is a no-op; unknown client with `structs = []` and state 0 -> whole range pending.

K6. **writeClientsStructs / writeStructs** (`encoding.js:57-110`): only clients with `state > targetClock` (plus clients missing in target, clock 0) are written; clients sorted **descending**; for each client the start clock is clamped to `max(clock, structs[0].id.clock)`; first struct written with `offset = clock - struct.clock` (so a diff can start mid-item, see E4/GC.write/Skip.write offsets). Number of structs = `structs.length - startIndex`. Tests: diff against a state vector pointing mid-item; unknown client; fully-synced client omitted; client count header excludes omitted clients.

K7. `writeStateAsUpdate` = structs + delete set derived from the store (`createDeleteSetFromStructStore`). `encodeStateAsUpdate(V2)` additionally appends `store.pendingDs` and `diffUpdateV2(pendingStructs.update, targetSV)` merged with the main update (V1 path converts pending parts V2->V1 via `convertUpdateFormatV2ToV1`). (`encoding.js:504-541`) Test: a doc with pending state re-exports it so a downstream peer eventually converges.

K8. Applying an update that contains a struct for the receiver's own clientID (client id collision) -> J6.e regenerates `doc.clientID` after the tx.

## L. Error paths and malformed-input inventory (feeds TASK-1.12 / 1.13)

L1. `unexpectedCase` throw sites: `addStruct` clock gap (A3); `findIndexSS` miss (I1); `Item.gc` on non-deleted (E2); `Item.write` unknown parent type (E4); `contentRefs[0]` and `[10]` (E5); `Skip.integrate` (G2).
L2. `methodUnimplemented` throw sites: `AbstractStruct` abstract members; `AbstractContent` members; `splice` on Binary/Embed/Format/Type/Doc (H).
L3. Undefined-behaviour (JS TypeError) sites a port must turn into defined errors: `find` on unknown client (I2); `contentRefs` index 11-31 (E5); `typeRefs[unknown]` (H2); `getMissing` parent ID pointing to non-type Item (C3); `findIndexSS` on empty array (I1); `JSON.parse` failure in `readContentJSON` (H table); `findRootTypeKey` not found throws (E4).
L4. Silent-accept cases: first struct for an unknown client with clock>0 (A3 — blocked by K2 offset<0 check, which treats "state 0" as missing prefix); update structs with `offset >= length` dropped (K2); `Skip` structs dropped (K2); bit 6 ignored when origin present (K1); trailing bytes after the delete set are not checked here.
L5. Exceptions inside `transact` callbacks and observers: cleanup still runs (J2, J6). Errors thrown by observers propagate **after** GC/merge/update emission; only the first thrown error is observed from `callAll`.

## M. Suggested test-case seeds (derived, for TASK-1.x test design)

- Golden update bytes for: single insert, split-then-merge, concurrent same-position insert from clients with ids {1,2} and {2,1} (deterministic ordering), map overwrite race, delete + gc, nested type delete.
- Property tests: (1) convergence under all delivery permutations/duplication/partitioning of updates; (2) `integrityCheck` after each step; (3) `encodeStateAsUpdate` -> new doc -> equal content and equal state vector; (4) split/merge round trip yields identical encoded state (merge canonicalisation).
- Targeted white-box: B3 split bookkeeping, D4 case 1/2 branch coverage (both sub-branches of case 1, case 2 with and without conflicting-set hit, else-break), D8 GC replacement, K3/K4 pending merge, K5 pendingDs.
