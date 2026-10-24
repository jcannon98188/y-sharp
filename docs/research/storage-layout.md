# Yjs and Yrs struct layout and the slow-tier baseline workloads

Research for TASK-3.3 (wayfinder map: TASK-3). Feeds TASK-3.5 (M0 spike design) and TASK-3.8 (implementation plan).

## Sources and method

- **Yjs v13.6.33**: submodule `yjs/` at commit `9ea3a4f`. **lib0 v0.2.99**: submodule `lib0/` at `b895c1d`. Paths below are relative to `yjs/src/` unless prefixed.
- **Yrs**: the local checkout `C:\Users\Jason\source\repos\y-crdt-yffi-pendingstructs`, crate `yrs` 0.28.0, commit `c5c194c7d4ed0dfe55496e8c57ad046277f6d62d`. Paths are relative to `yrs/src/`. The same checkout is the basis of `docs/research/yrs-concurrency.md`. The GitHub `main` branch was also reachable (raw.githubusercontent.com returned 200 for `yrs/src/block.rs`), but it was not diffed against the pinned commit, so the pinned checkout is the reference.
- **Measurements** (read-only, run from the session scratchpad, nothing written into the repo):
  - `count.mjs` loads Yjs with the same lib0 loader as `bench/yjs-memory/measure.mjs`, runs the three workloads in one process, and counts structs, content classes and map and marker state. Node v24.21.0, `--expose-gc`.
  - `parts.mjs` measures the retained heap of 1,000,000 instances of each object kind that makes up a Yjs item.
  - A small Rust probe, built offline against the pinned Yrs checkout, prints `size_of` for Yrs types.
- Tags used below: **[read]** = stated directly in source at the cited line. **[measured]** = produced by one of the runs above. **[inferred]** = my reasoning from the cited code; not run. **[estimate]** = a size or cost I derived by hand from field types or V8/.NET object layout; not measured.

## TL;DR

- The three figures in `bench/yjs-memory/yjs-bytes-per-item.json` are **retained heap divided by operations**, not by structs. [read: `measure.mjs:61,67`] The "items" in the JSON are the number of workload operations. Measured structs per op are very different per workload: text 1 Item per op, map 1 Item per op, array 1 Item for all 1,000,000 ops.
- **Text (194.77 B/op)**: 1,000,000 `Item`s, each holding a one-character `ContentString`. No merges and no splits happened. Reconstructed composition: Item object 112 B + ID object 40 B + ContentString object 32 B + 8 B struct-array slot = 192 B, against 194.77 measured.
- **Array (8.31 B/op)**: the workload collapses into **one** `Item` whose `ContentAny.arr` is one JS array of 1,000,000 small integers. The 8.31 B/op is essentially one 8-byte array slot per element. It is a single 8 MB contiguous allocation, so Yjs itself does not satisfy the ADR 0018 chunking rule on this workload.
- **Map (312.26 B/op)**: 100,000 `Item`s, one per key, each with a `ContentAny([value])` and an entry in `parent._map`. Nothing is overwritten and nothing is deleted. Reconstructed composition is about 312 B per key, but the Map hash-table share is inferred, not measured.
- Yjs gets its position lookup from search markers (at most 80 per array or text, `AbstractType.js:27`). Yrs has no search markers and walks linearly from `Branch.start` [read: `branch.rs:368-392`, `text.rs:734-760`], which I infer makes the text workload quadratic for Yrs. Not run.
- A Yrs `Item` is 160 bytes inline and is always boxed [measured `size_of`; read `block.rs:215-219, 439`]. For the one-character text workload, Yrs is therefore **not obviously smaller** than Yjs. The benefit Yrs gets from arenas only shows up when items are not boxed. [estimate]
- The 8.31 B/op array ceiling is the hardest gate. Any representation that stores each element as a tagged 16-byte struct or a boxed object fails it. A homogeneous 8-byte column is needed. [estimate]
- ADR 0009 does not define "item". Structs per op and bytes per op give different answers, and for the array workload they diverge by 1,000,000 to 1. The spike should record both, and the ADR should name the unit.

---

## 1. The three slow-tier workloads

Source: `bench/yjs-memory/measure.mjs`. Each workload runs in a fresh child process, and `heap()` forces two GCs before measuring `heapUsed` [read: `measure.mjs:26, 58-61`]. The result is `(after - before) / ops` [read: `measure.mjs:61`]. Batches are 1,000 operations per `doc.transact` [read: `measure.mjs:27, 31-33`].

| Workload | Ops | Operation per op | Item structs (measured) | Content (measured) | Retained bytes | Bytes per op |
|---|---|---|---|---|---|---|
| text-insert | 1,000,000 | `t.insert(max(0, length - back), 1 char)`, `back` in 0..15 (`measure.mjs:34-35`) | 1,000,000 | 1,000,000 x `ContentString`, all length 1 | 194,770,504 | 194.77 |
| array-push | 1,000,000 | `a.push([j])` (`measure.mjs:43`) | 1 | 1 x `ContentAny`, length 1,000,000 | 8,316,304 | 8.32 |
| map-set | 100,000 | `m.set('k'+j, j)` (`measure.mjs:48`) | 100,000 | 100,000 x `ContentAny` of length 1 | 31,232,336 | 312.32 |

The `count.mjs` column is a single-process rerun. The committed JSON is 194.77 / 8.31 / 312.26 (child processes). The 0.06 difference on the map figure is run-to-run noise in `heapUsed`. [measured]

### 1.1 Text (`text-insert`)

- Each insert is a single character. `insertText` builds `new ContentString(text)` [read: `types/YText.js:265`] and integrates one `Item`.
- Index `length - back`: 62,500 of the 1,000,000 ops land at the end (`back == 0`, `rightOrigin === null`), and 937,500 insert before an existing character. [measured: `nonNullRightOrigin` = 937,500]
- Items: 1,000,000, all with `length === 1`, 0 GC structs, 0 deleted items. [measured]
- **Splits: none.** Every insert lands on an existing item boundary because every item is one character long, so `splitItem` (`Item.js:85-121`) never runs. [measured by consequence; inferred mechanism]
- **Merges: none.** `Item.mergeWith` (`Item.js:571-586`) requires `this.right === right` and matching `rightOrigin`, among other conditions. Structs are appended in clock order by `addStruct` (`StructStore.js:88-101`), so a new item's clock predecessor is usually not its linked-list left neighbour. I infer that is why all 15/16 middle inserts fail to merge. [inferred]
- Search markers: 80 `ArraySearchMarker` objects, the cap (`AbstractType.js:27`). Marker maintenance runs on every insert (`updateMarkerChanges`, `AbstractType.js:184`). [measured: `searchMarkers` = 80]
- Node sizes: origin and rightOrigin for length-1 items are the neighbour's `ID` object, not copies, because `lastId` returns `this.id` when `length === 1` [read: `Item.js:560-563`] and integration assigns `left.lastId` / `right.id` [read: `Item.js:387, 391`]. [inferred from code; consistent with the measured total below]

### 1.2 Array (`array-push`)

- Each push produces a `ContentAny([j])`. Cleanup merges consecutive same-client, linked, adjacent structs [read: `Transaction.js:330-357`, `Item.js:571-586`]. `ContentAny.mergeWith` is `this.arr = this.arr.concat(right.arr)` [read: `ContentAny.js:64-67`].
- Result: **one** item with a 1,000,000-element array. [measured]
- The 8.32 B/op is the element slot in that array. Node 24 without pointer compression stores packed small integers at 8 bytes each. [inferred from the number; not separately measured]
- Yjs merges through a concat, so the array is one contiguous allocation of 8 MB, which V8 places in the large-object space. The chunking rule of ADR 0018 is violated by Yjs itself here. [inferred]
- Time, not memory: each `concat` copies the accumulated array, so merging a 1,000-op batch is O(batch squared) copying. I did not time it. [inferred]
- No search markers were created for this workload. [measured: `searchMarkers` = 0]

### 1.3 Map (`map-set`)

- `typeMapSet` creates `new ContentAny([value])` [read: `types/AbstractType.js:846-862`], then integrates an `Item` with `parentSub = key` [read: `AbstractType.js:846+`, `Item.js:419+`].
- Each new key has no left item, so the item becomes the `_map` entry for that key [read: `Item.js` integrate, the `parentSub` branch at `Item.js:496-516`; `AbstractType.js:270` for `_map`].
- Items: 100,000, all `parentSub`, `_map` entries: 100,000, no overwrites, no deletes. [measured]
- Per-key cost: the Item, its ID, its ContentAny, its one-element JS array and backing store, the key string, the `_map` entry and the struct-array slot (see section 2.3).

### 1.4 What "bytes per item" means

- It is retained heap after two forced GCs, minus heap before the document was built, divided by the number of operations [read: `measure.mjs:26, 58-61`]. Garbage from transactions is excluded, and anything the document keeps (structs, payloads, `_map`, markers) is included.
- The figure is per **operation** (per inserted character, per pushed element, per map key). For text and map, one operation is also one struct. For the array, one operation is one array slot and there is one struct for the whole workload.
- Consequence for the gate in ADR 0009 ("Bytes per item must not exceed the figure Yjs produced"): a per-struct reading would make the array figure meaningless (about 8 MB per struct) and would let an implementation with one struct per op pass trivially. The per-op reading is the one the JSON records. The ADR should say so. [inferred from the figures]

---

## 2. Yjs layout

### 2.1 Item and content

- `Item extends AbstractStruct` [read: `Item.js:250`]. `AbstractStruct` holds `id` and `length` [read: `structs/AbstractStruct.js:12-14`].
- Own fields, in constructor order [read: `Item.js:267-312`]: `origin`, `left`, `right`, `rightOrigin`, `parent`, `parentSub`, `redone`, `content`, `info` (bit flags keep/countable/deleted/mark, `Item.js:300-312`). With `id` and `length` that is **11 properties** [measured: `Object.keys` in `parts.mjs`].
- `origin` / `rightOrigin` are `ID` objects, or `null`. `ID` is `{client, clock}` [read: `utils/ID.js:7-22`]. `createID` allocates a new `ID` [read: `utils/ID.js:42`].
- Content classes [read: `structs/*.js`]: `ContentString` (`str`, `ContentString.js:8-16`; `mergeWith` is `this.str += right.str`, `ContentString.js:72-75`; `splice` uses `slice`, `ContentString.js:51-57`), `ContentAny` (`arr`, `ContentAny.js:10-18`), `ContentJSON`, `ContentBinary`, `ContentDeleted` (`len` only, `ContentDeleted.js:6-11`), `ContentType`, `ContentEmbed`, `ContentFormat`, `ContentDoc`. Content objects carry no references to the item.
- Deleted content: `Item.gc` replaces `content` with `new ContentDeleted(length)` or, when the parent is also collected, with a `GC` struct [read: `Item.js:634-643`]. `ContentString.delete`, `ContentAny.delete` are no-ops [read: `ContentString.js:85`, `ContentAny.js:77`]. Overwritten map values stay as deleted items until GC.

### 2.2 StructStore, delete set and merging

- `StructStore.clients` is `Map<clientId, Array<GC|Item>>` [read: `utils/StructStore.js:10-18`]. Each client's array is sorted by clock and contiguous; `addStruct` pushes at the end and throws if the clock is not contiguous [read: `StructStore.js:88-101`].
- `findIndexSS` is a pivoted binary search over that array [read: `StructStore.js:111-150`].
- `getItemCleanStart` / `getItemCleanEnd` split an item to create a boundary [read: `StructStore.js:172-220`; `splitItem` at `Item.js:85-121`].
- `DeleteSet.clients` is `Map<clientId, Array<DeleteItem{clock, len}>>` [read: `utils/DeleteSet.js:16-46`]. `addToDeleteSet` pushes `new DeleteItem` [read: `DeleteSet.js:175-177`]. `isDeleted` and `findIndexDS` are binary searches [read: `DeleteSet.js:78-110`]. `sortAndMergeDeleteSet` at `DeleteSet.js:116`.
- Merging happens at commit in `cleanupTransactions` [read: `utils/Transaction.js:260-360`]: `sortAndMergeDeleteSet`, `tryGcDeleteSet` (`Transaction.js:199-221`), `tryMergeDeleteSet` (`Transaction.js:226-`), then `tryToMergeWithLefts` over the changed ranges and over `_mergeStructs` (`Transaction.js:171-190`, `330-357`). `Item.mergeWith` (`Item.js:571-586`) checks: same constructor, `right.origin == left.lastId`, `left.right === right`, equal `rightOrigin`, same client, clock adjacency, same deleted flag, no `redone`, same content constructor, and `content.mergeWith` returning true.
- `mergeWith` on an item that has search markers repoints any marker from the right item to the left item and adjusts its index [read: `Item.js:581-596`].

### 2.3 Search markers and maps

- `maxSearchMarker = 80` [read: `types/AbstractType.js:27`]. `ArraySearchMarker` is `{p: Item, index, timestamp}` [read: `AbstractType.js:38-`].
- `markPosition` creates a marker up to the cap, then overwrites the oldest [read: `AbstractType.js:76-88`]. `findMarker` takes the nearest marker by index, walks right or left through the linked list, and re-marks when the marker is far enough away [read: `AbstractType.js:102-180`]. Insertions and deletions adjust marker indices with `updateMarkerChanges` [read: `AbstractType.js:184-`].
- `_map` is a `Map<string, Item>` holding the current item per key [read: `AbstractType.js:270`; `Item.js` integrate sets `_map.set(parentSub, this)`].

### 2.4 Per-object costs

Measured with `parts.mjs` (1,000,000 live instances each, retained heap per instance including its 8-byte array slot, Node v24.21.0, V8 without pointer compression [inferred from the 8-byte slot]):

| Object | Measured (with slot) | Own bytes (slot removed) | Notes |
|---|---|---|---|
| `ID` | 48.01 | 40 | 24-byte JS object header + 2 fields x 8 |
| `ContentString` (1 char) | 40.01 | 32 | 24 header + 1 field; the 1-char string is V8's single-character table, not an allocation |
| `Item` (with its own `ID` and `ContentString`) | 192.02 | 112 | 24 header + 11 fields x 8 |

Reconstruction for text: 112 + 40 + 32 + 8 = 192, against 194.77 measured. The residual (about 2.8 B/op) is struct-array growth slack, which is [inferred]. The match holds only if origin and rightOrigin do not allocate, which is what `lastId` (`Item.js:560-563`) implies.

Reconstruction for map: 112 (Item) + 40 (ID) + 32 (ContentAny) + 32 (JS array header for `[j]`) + 24 (backing store, one element) + about 24 (key string `'k'+j`) + 8 (struct slot) = 272, leaving about 40 B per key for the `_map` hash table entries and buckets. The 40 B is inferred from the 312.26 total, not measured on its own. [inferred]

Estimated .NET equivalents (not measured; object header 16 bytes on x64, reference 8 bytes, struct fields inline):

- Object-per-item `Item` class with inline `ID` structs for origin and rightOrigin: about 104-112 field bytes plus 16 header, about 120-128 B. Plus `ContentString` object (24 B) and a one-character `string` (24 B) if text is held as a string object, plus an 8-byte array slot. Total about 176-184 B per character. [estimate]
- The same item with content inline (no `ContentString` object, a chunk slice instead of a string) drops to about 128-136 B. [estimate]

---

## 3. Yrs layout

### 3.1 Block, Item and content

- `Block` is an enum: `Item(Box<Item>)`, `GC(BlockRange)`, `Skip(BlockRange)` [read: `block.rs:215-219`]. The item is boxed.
- `Item` [read: `block.rs:1302-1345`]: `id: ID`, `len: u32`, `left`/`right: Option<ItemPtr>`, `origin`/`right_origin: Option<ID>`, `content: ItemContent`, `parent: TypePtr`, `redone: Option<ID>`, `parent_sub: Option<Arc<str>>`, `info: ItemFlags(u16)` [read: `block.rs:1204`].
- `ItemPtr(NonNull<Item>)` is a raw pointer into the box; it is `Send`/`Sync` by `unsafe impl` [read: `block.rs:439-442`]. The box keeps the address stable when the containing `Vec` reallocates.
- `ItemContent` variants [read: `block.rs:1692-`]: `Any(Vec<Any>)`, `Binary(Vec<u8>)`, `Deleted(u32)`, `Doc(Option<Doc>, Doc)`, `JSON(Vec<String>)`, `Embed(Any)`, `Format(Arc<str>, Box<Any>)`, `String(SplittableString)`, and branch references.
- `SplittableString { content: SmallString<[u8; 8]> }` [read: `block.rs:1570-1573`]. Strings up to 8 bytes are stored inline with no heap allocation. Longer strings are heap-allocated and owned by the one item.
- Text is therefore **owned per item**, not a slice of a shared buffer. ADR 0018 asks for shared chunked buffers. This is a design departure from Yrs, not something Yrs provides.

### 3.2 Block store

- `ClientBlockList { inner: Vec<UnsafeCell<Block>> }` [read: `block_store.rs:17-20`], one per client. `Send`/`Sync` by `unsafe impl` [read: `block_store.rs:23-27`].
- `BlockStore { clients: HashMap<ClientID, ClientBlockList, ClientHasher>, skips: IdSet }` [read: `block_store.rs:281-285`]. `push` appends or replaces a skip [read: `block_store.rs:308-`]. `get_item` / `get_block` [read: `block_store.rs:404-418`].
- `find_index` is a pivoted binary search that starts with an append fast path [read: `block_store.rs:49-`].
- Splitting: `split_block` / `split_block_inner` [read: `block_store.rs:457-473`] and `Item::splice` [read: `block.rs:639-665`] allocate a **new** `Box<Item>` and move part of the content into it. `ItemContent::splice` copies: `Any` is `to_vec` on both sides [read: `block.rs:2006-2014`], and `String` goes through `split_str` into new `SplittableString`s [read: `block.rs:2015-2030`].
- Merging: `ItemPtr::try_squash` [read: `block.rs:688-711`] has the same conditions as Yjs `mergeWith`, plus an `info.is_linked()` exclusion (`block.rs:696`) for items that weak links point at [read: `block.rs:901`, `types/weak.rs:562-580`]. Content squash [read: `block.rs:2053-2074`]: `Any` does `v1.append(&mut v2.clone())`, `String` does `push_str`.
- Commit cleanup [read: `transaction.rs:1111-1145`]: GC, delete-set squash, `squash_left` over the inserted clock ranges, then over `merge_blocks`. Same shape as Yjs.
- GC: `Item::gc` sets `content = ItemContent::Deleted(len)` and clears countable, or marks the id for parent GC [read: `block.rs:1556-1566`]. `Block::GC(BlockRange)` replaces the block [read: `block.rs:355-361`].

### 3.3 Position lookup

- Yrs has **no search markers**. A grep for `search_marker` and `SearchMarker` in `yrs/src` returns nothing.
- `index_to_ptr` walks `item.right` from the given pointer, subtracting content lengths until the index is reached, then splits [read: `branch.rs:368-392`]. Text uses `find_position`, which starts at `this.start` and walks the list [read: `types/text.rs:734-760`]. `BlockIter` holds a per-iteration cursor only [read: `block_iter.rs:10-16`].
- Inferred consequence: for the text workload, every insert near the end walks from the start of the list, so the total work grows as the square of the item count (about 5 x 10^11 pointer steps for 1M items with no merges). This is an inference from the code and was not run. Yrs is not a usable baseline for the 1M-text workload as written.

### 3.4 Identifiers and delete set

- `ClientID` is a 53-bit id stored in `NonZeroU64` (or `u32` with the `small-client` feature) [read: `block.rs:72-81`]. The doc comment states the 53-bit range.
- `ID { client: ClientID, clock: u32 }` [read: `block.rs:160-170`].
- `IdSet(IdMapInner<()>)` [read: `id_set.rs:142`]. `IdMapInner` is `BTreeMap<ClientID, IdRanges<T>>` [read: `ids.rs:595`]. `IdRanges<T>` is `SmallVec<[(Range<u32>, T); 1]>` [read: `ids.rs:29`].
- `StateVector` is `HashMap<ClientID, u32, ClientHasher>` [read: `state_vector.rs:21`].

### 3.5 Measured Yrs sizes

Built with a probe crate against the pinned checkout (`size_of`, x64, rustc from the session toolchain). [measured]

| Type | Bytes |
|---|---|
| `ID` | 16 |
| `ClientID` | 8 |
| `ItemPtr` | 8 |
| `Option<ItemPtr>` | 8 |
| `Option<ID>` | 16 |
| `ItemContent` | 32 |
| `SplittableString` | 24 |
| `Item` | 160 |
| `ItemFlags` | 2 |
| `BlockRange` | 16 |
| `Any` | 24 |

`Block` and `TypePtr` are `pub(crate)` and could not be measured from outside the crate. Estimate: `Block` is 16 to 24 bytes, depending on whether Rust uses a niche in the `ClientID` of `BlockRange`. [estimate]

Estimated Yrs cost per one-character text item (not measured): `Box<Item>` 160 B + allocator overhead (about 16 B on the Windows CRT or glibc, depending on the platform) + one `Block` slot (16 to 24 B). That is roughly 190 to 200 B per character, so Yrs is **not obviously below Yjs's 194.77 on this workload**. The one-character text benefit does not carry over to Yrs' boxed item. [estimate]

Estimated Yrs cost per map key: the boxed item (160 + overhead) + an `Arc<str>` key (16 + bytes) + a `Vec<Any>` of one element (24 B + 24 B heap) + a `HashMap` entry (16 + 8 + control bytes) + the block slot. Roughly 260 to 290 B. [estimate]

Estimated Yrs cost per array element: each push is a boxed item with a one-element `Vec<Any>` (24 B per element, before squash). After squash, one `Vec<Any>` holds all elements at 24 B each, plus `Vec` growth slack (up to about 2x with doubling). That is about 24 to 48 B per element against Yjs's 8.31. [estimate]

---

## 4. What a minimal spike store must do

The spike must run the three workloads fairly under both candidate models (object-per-item and index-addressed arenas). Operations only. No code.

1. **Client table.** Map from 53-bit client id to a per-client ordered sequence of blocks (items, skips, GC ranges). Keys are unsigned 64-bit; the table grows by chunks.
2. **Per-client sequence.** Append at the end; binary search by clock (chunk-level first-clock table, then within a chunk); split a block at an offset (a new block at the end of the storage, not in place); replace a block by a GC range; remove a merged-away block. Needed by the commit merge and by every local insert that starts at a clock boundary.
3. **Sequence links per parent.** Left and right neighbour, origin id, right-origin id, parent, parent sub-key, and countable and deleted flags. Needed for position lookup and for the Yjs merge rules.
4. **Local insert.** Compute left and right neighbours from the index, set origin and right-origin, integrate into the sequence, append to the client sequence, and update the parent length. The remote conflict-resolution loop (`Item.integrate`, `Item.js:419-530`) is not needed for these three local-only workloads, but the spike must not make it impossible to add.
5. **Merge on insert.** After a transaction, merge adjacent blocks that meet the Yjs `mergeWith` conditions (`Item.js:571-586`): same client, clock-adjacent, linked-adjacent, equal right-origin, same deleted flag, same content kind, and a content merge that succeeds. Content merge means: extend a text slice when it is contiguous in the same buffer, or append to a typed run. A failed merge leaves two blocks, as Yjs does.
6. **Position lookup.** Index to block. Yjs uses at most 80 search markers (`AbstractType.js:27`), updated on every insert and delete (`AbstractType.js:184`), and walks linearly from the nearest marker. The spike needs an equivalent: either the same marker scheme, or another index that is O(markers) per op. Without it the text workload is quadratic, as the Yrs code is.
7. **Delete.** Mark a block deleted, add its range to the delete set, and clear the countable length of the parent. The three workloads do not delete, but ADR 0009's steady-state check ("100k steady-state edits") probably does, and the spike must include it.
8. **Delete set.** Per-client sorted list of ranges, with a binary search for `isDeleted`. Chunked.
9. **Map key table.** Per parent, a table from key to the current block for that key, and removal of the previous block when a key is set again (the previous block is marked deleted, as `Item.js` integrate does via `this.left.delete`, `Item.js:514`).
10. **Content payloads.** Text: slices of shared character buffers, so that a split copies nothing (ADR 0018). Typed values for the array workload: a homogeneous 8-byte column per chunk. Map values: one payload per key, chunked, with no boxing for small integers.
11. **Chunked growth.** Every growing structure (per-client sequence, delete set, payload buffers, key table) grows by adding chunks no larger than 64 KB. Chunk size is fixed per structure.
12. **Retained-memory measurement.** Build the same retained-heap figure as `measure.mjs`: force two collections, measure, build, force two collections, measure, divide by operations. Report structs per op alongside bytes per op.
13. **Steady-state check.** Run 100,000 edits (inserts and deletes, not just appends) and count gen2 collections and large-object-heap growth.

---

## 5. Risks against ADR 0018 and ADR 0009

Notation: "A" is object-per-item (an `Item` class per item with references between items). "B" is structs in chunked arenas, addressed by index.

### 5.1 Model A (object per item)

| Constraint | Risk | Evidence |
|---|---|---|
| Text bytes/op <= 194.77 | Likely passes but with a thin margin. Estimated 176-184 B per character if content is a string object, about 128-136 B if content is inline. | Section 2.4 estimates [estimate] |
| Map bytes/key <= 312.26 | Estimated 280-300 B per key. Passes with a thin margin. | Section 2.4 [estimate] |
| Array bytes/op <= 8.31 | **Fails** unless elements are stored in typed chunks, not one object per element. A boxed or referenced element costs 16 to 24 B. | Yjs 8.31 is the 8-byte slot [measured]; section 3.5 Yrs `Any` 24 B [measured] |
| Zero gen2 collections in 100k edits | **At risk.** Every surviving item is a graph node that promotes gen0 to gen1 to gen2. The heap grows with the document, so gen2 collections are likely even when steady-state edits do not grow it. Not measured. | [inferred] |
| No LOH growth | Passes if per-client lists and key tables are chunked (the item class does not change this). | [inferred] |
| No per-item machinery (no finalizer, lock, event, delegate, closure) | Passes as long as the item class carries none. A class with reference fields is allowed by ADR 0018's wording. | ADR 0018 text |
| Neighbour links without separate objects | Passes: `Left`/`Right` are direct references. | ADR 0018 text |
| 53-bit client ids | Passes with `ulong`. | ADR 0018 text |
| Text as slices of shared chunked buffers | Passes: an item holds a reference to a chunk and an offset/length pair. The reference pins the chunk for as long as any item uses it. | [inferred] |
| Deleted content shrinks and payload is released | Passes with `Deleted(len)` replacement and dropping the content reference. | [inferred] |
| Chunk size <= 64 KB | Passes if the chunk holds references or small structs, not one array of all items. | [inferred] |

### 5.2 Model B (structs in chunked arenas)

| Constraint | Risk | Evidence |
|---|---|---|
| Text bytes/op <= 194.77 | Passes with a wide margin. Estimated about 64-80 B per character for a struct of about 56-64 B plus its 8-byte slot. | [estimate] |
| Map bytes/key <= 312.26 | Passes with a wide margin if the key is stored in a key-bytes arena rather than as a string object. A `Dictionary<string,int>` key adds a string object per key (about 24 B) and an entry (about 24 B). | [estimate] |
| Array bytes/op <= 8.31 | **Passes only with a homogeneous 8-byte column.** A tagged struct (for example a 1-byte tag plus an 8-byte payload) is 9 to 16 bytes per element and fails. Mixed-type runs need a separate kind column or a fallback that costs more. | Yjs 8.31 [measured]; tagged-struct arithmetic [estimate] |
| Zero gen2 collections in 100k edits | Better: the arena holds value-only structs with no references, so the GC does not scan them. Surviving structs cost nothing to trace. Residual risk is any reference field in the struct (payload objects, buffer references). | [inferred] |
| No LOH growth | Passes if every arena chunk is under 64 KB. The top-level chunk table grows at about 1 entry per 1,000 structs, which is small. | [inferred] |
| No per-item machinery | Passes: no per-item heap object at all. | ADR 0018 text |
| Neighbour links without separate objects | Passes: links are integer indices in the struct. | [inferred] |
| 53-bit client ids | Passes if the client table keys are `ulong` and the per-client index is separate from the id. | ADR 0018 text |
| Text as slices of shared chunked buffers | Passes: a block stores (buffer index, offset, length). A contiguous append extends the length. A merge across a buffer boundary cannot extend, so either it is refused (the merge fails, as in Yjs for mid-document inserts) or the block holds a segment chain (more complexity). | [inferred] |
| Deleted content shrinks and payload is released | **Risk.** Releasing a buffer requires a per-buffer live count or a reclaim rule. ADR 0018 says compaction is not required, but releasing the payload storage is. A partly deleted buffer holds memory until all its slices are deleted. | ADR 0018 text; [inferred] |
| Index stability | **Risk.** Reusing a freed index can alias a stale marker or link. The spike needs either a generation check or a no-reuse policy. | [inferred] |
| Marker maintenance on split and merge | Same work as Yjs (`Item.js:581-596` repoints markers on merge). | [read] |

### 5.3 Yrs as a reference (not a model to copy)

- Yrs does not meet ADR 0018 on two points. Its `Any` and text content grow a single `Vec` or `SmallString` in one allocation (`block.rs:2056-2070`), which is a single allocation that can exceed 64 KB. Its items are boxed. Its position lookup is linear. Yrs is a reference for semantics (squash rules, commit order, delete set shape) and not for layout.

---

## Could not verify

- **Yrs runtime behaviour.** No Yrs workload was run. The quadratic text-insert cost (section 3.3), the Yrs per-item and per-element byte costs (section 3.5) and the Yrs squash costs are inferences from source.
- **`Block` and `TypePtr` sizes in Yrs.** Both are `pub(crate)`. The `Block` size range is an estimate.
- **Node heap composition.** The per-object costs were measured for a fresh process with the same object kinds. The attribution of the map residual (about 40 B per key) to `_map` is inferred. I did not run a heap snapshot.
- **Allocator overhead** for boxed Yrs items and for .NET objects. Estimates only.
- **.NET costs.** Nothing was measured in .NET. The object sizes in sections 2.4, 5.1 and 5.2 are arithmetic on the CLR object layout (16-byte header, 8-byte references), not measurements.
- **The steady-state workload.** ADR 0009 does not define the 100k edits (insert/delete mix, positions, sizes). Section 4 item 13 assumes a mix; the spike gate must pin it down.
- **Timing.** Neither the Yjs array concat cost nor any Yrs cost was timed. ADR 0009 does not gate on time for these workloads.
- **Yrs main branch.** The GitHub `main` branch was reachable but not compared against the pinned checkout (commit `c5c194c`).
