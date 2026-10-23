# Encoding and update behaviour inventory (TASK-1.2)

Scope: the wire formats and update-manipulation functions of Yjs 13.6.33 (`yjs/`), lib0 0.2.99 (`lib0/`), and the thin y-protocols sync framing (`y-protocols/sync.js`). Goal: give the test-suite design enough precision to write byte-exact oracle comparisons and malformed-input tests for the C# port.

Conventions
- All paths are relative to the repo root. `file:line` refs point at the submodule source as checked out here. A range means "the function starting here".
- "V1" and "V2" are the Yjs update encodings (`UpdateEncoderV1/V2`). "ds" is delete set. "sv" is state vector.
- Everything marked **[verified]** was also run against the actual source (copied to a scratch dir, Node v24.21.0; submodules untouched). Hex shown is lowercase, space-separated for readability only. Error message *text* is V8-specific; the *error class and the fact of failure* are what a port must match. Message text is not part of any contract.
- JS numbers are IEEE doubles. Every "number" in the protocol (client id, clock, length) is a double that is exact only up to 2^53-1. See section 9.
- Out of scope here: snapshot encoding (`yjs/src/utils/Snapshot.js:85-95`, `218-236`), awareness, UndoManager, relative positions. They reuse the same DS encoders; flag a follow-up if the suite needs them.

---

## 1. lib0 primitives actually used by Yjs

Yjs touches only a subset of lib0. Everything else (`writeUint16/32`, `IncUintOptRleEncoder`, `IntDiffEncoder`, `RleIntDiffEncoder`, terminated strings, `BigUint64`) is unused by `yjs/src` and `y-protocols/sync.js` (grep confirmed) and can be left out of the port's required surface.

### 1.1 Encoder buffer
`Encoder` is `{cpos, cbuf: Uint8Array(100), bufs: []}` (`lib0/encoding.js:38-46`). Growth strategy (doubling chunks) is an implementation detail with no wire effect. `toUint8Array` concatenates (`lib0/encoding.js:95-105`).

### 1.2 VarUint (`lib0/encoding.js:260-266`, `lib0/decoding.js:241-260`)
Little-endian base-128, 7 data bits per byte, bit 0x80 = continuation. **[verified]**

| value | bytes |
|---|---|
| 0 | `00` |
| 127 | `7f` |
| 128 | `80 01` |
| 300 | `ac 02` |
| 16383 | `ff 7f` |
| 16384 | `80 80 01` |
| 2^31 | `80 80 80 80 08` |
| 2^32-1 | `ff ff ff ff 0f` |
| 2^32 | `80 80 80 80 10` |
| 2^53-1 | `ff ff ff ff ff ff ff 0f` |

Encoder quirks (silently produce garbage, never throw): negative input writes one byte `BITS7 & num` (-1 -> `7f`), non-integer input is truncated by `&` on the loop's last byte (1.5 -> `01`), NaN -> `00`, >= 2^53 loses precision (2^53 -> `80 80 80 80 80 80 80 10`; 2^64 -> `80 .. 80 02`). **[verified]** Yjs only passes non-negative integers so these matter only for hostile *state* (e.g. unsorted delete sets under V2, section 4.4).

Decoder (`lib0/decoding.js:241-260`): accumulates `num += (b & 0x7f) * mult; mult *= 128` in doubles. Behaviour **[verified]**:
- EOF before the first byte, or EOF mid-number (continuation bit set, no more bytes): throws `Error('Unexpected end of array')` (`decoding.js:36`, thrown at 259).
- Non-canonical (overlong) encodings are accepted: `80 00` -> 0.
- Overflow guard `num > MAX_SAFE_INTEGER -> throw Error('Integer out of Range')` (`decoding.js:254-256`) runs only *after a byte with the continuation bit*, on the value accumulated so far. The terminating byte is never checked, so `80 80 80 80 80 80 80 80 80 01` returns 9223372036854776000 (2^63) without error. Ten `ff` then `00` throws "Integer out of Range".

### 1.3 VarInt (`lib0/encoding.js:277-291`, `lib0/decoding.js:273-298`)
Sign-magnitude, first byte: bit 0x80 = continue, bit 0x40 = negative, low 6 bits = magnitude; following bytes are 7-bit groups like VarUint. **Negative zero is representable and meaningful**: `-0` -> `40`. The encoder detects it via `math.isNegativeZero` (`lib0/math.js:58`: `n !== 0 ? n < 0 : 1/n < 0`). **[verified]**

| value | bytes |
|---|---|
| 0 | `00` |
| -0 | `40` |
| 1 / -1 | `01` / `41` |
| 63 / 64 | `3f` / `80 01` |
| -63 / -64 | `7f` / `c0 01` |
| 127 | `bf 01` |
| 8191 | `bf 7f` |
| -8192 | `c0 80 01` |
| 2^31-1 | `bf ff ff ff 0f` |
| -(2^31-1) | `ff ff ff ff 0f` |
| 2^32 | `80 80 80 80 20` |

Decoder quirks **[verified]**:
- `readVarInt` at EOF on the *first* byte does **not** throw: `decoder.arr[pos++]` is `undefined`, `undefined & x` is 0, so it returns `0` and advances `pos` past the end (`decoding.js:274-281`). Only EOF *after a continuation byte* throws "Unexpected end of array".
- Multi-byte negative zero (`c0 00`) decodes to `-0`.
- Overflow guard same as VarUint (`decoding.js:292-294`).

### 1.4 Fixed-width reads
`readUint8` is `arr[pos++]` with no bounds check: at EOF it returns `undefined` and still advances `pos` (`decoding.js:142`). `readUint16/32` at EOF return `NaN` / `0` respectively (arithmetic on `undefined`). **[verified]**. Used by Yjs only through `readInfo` (V1) and the V2 info `RleDecoder`.

### 1.5 VarUint8Array and VarString
- `writeVarUint8Array` = VarUint(byteLength) then raw bytes (`encoding.js:434-437`).
- `readVarUint8Array` = `readUint8Array(decoder, readVarUint(decoder))` (`decoding.js:118`). `readUint8Array` builds `new Uint8Array(decoder.arr.buffer, decoder.pos + decoder.arr.byteOffset, len)` (`decoding.js:102-106`): a **view, not a copy**, and it throws `RangeError('Invalid typed array length: N')` only if the range exceeds the *underlying ArrayBuffer*, not the view. **[verified]**: if the input is a `subarray` of a larger buffer, a length prefix larger than the view silently reads neighbouring bytes of the backing buffer. Same for `readFromDataView` (`decoding.js:432-436`, `RangeError: Invalid DataView length 4` when truly out of the buffer). Consequence for the port: a bounds check against the *logical* input length is stricter than JS; do not use "JS throws" as the oracle for views-into-larger-buffers cases. Test inputs should be standalone `Uint8Array`s.
- `UpdateDecoderV1.readBuf` copies (`UpdateDecoder.js:104-106`), `UpdateDecoderV2.readBuf` returns the view (`UpdateDecoder.js:253-255`). Not observable on the wire.
- VarString = VarUint(utf8 byte length) + UTF-8 bytes. Encode: `TextEncoder.encodeInto` into a 30000-byte scratch buffer when `str.length < 10000`, else `encodeUtf8` (`encoding.js:296-318`, selected at 344). **Lone surrogates are written as U+FFFD (`ef bf bd`)** because that is what `TextEncoder` does: `"\ud800"` -> `03 ef bf bd`; `"a\udc00b"` -> `05 61 ef bf bd 62`. **[verified]**
- Decode: `TextDecoder('utf-8', {fatal: true, ignoreBOM: true})` (`lib0/string.js:98`). So: invalid UTF-8, overlong forms (`c0 80`), encoded surrogates (`ed a0 80`), > U+10FFFF all throw `TypeError`; a UTF-8 BOM (`ef bb bf`) is **kept** as U+FEFF (length counts it); embedded NUL is allowed. Length prefix larger than remaining buffer -> `RangeError` (see above). **[verified]**
- The polyfill paths (`encoding.js:327`, `decoding.js:342`) are selected only if `TextEncoder/Decoder` are missing; treat the native behaviour as the oracle.

### 1.6 Float / BigInt
Big-endian (`setFloat32(0, n, false)` etc.): `writeFloat32` 4 bytes, `writeFloat64` 8, `writeBigInt64` 8 two's complement (`encoding.js:467-479`, `decoding.js:441-451`).

### 1.7 Any encoding (`lib0/encoding.js:536-598`, `lib0/decoding.js:461-497`)

One tag byte, `127 - k`, then payload. **[verified]** hex for each:

| tag | dec | type | payload | example |
|---|---|---|---|---|
| `7f` | 127 | undefined | none | `undefined` -> `7f` |
| `7e` | 126 | null | none | |
| `7d` | 125 | integer | VarInt | `0`->`7d 00`, `-0`->`7d 40`, `1`->`7d 01`, `-1`->`7d 41` |
| `7c` | 124 | float32 | 4 bytes BE | `1.5` -> `7c 3f c0 00 00` |
| `7b` | 123 | float64 | 8 bytes BE | `0.1` -> `7b 3f b9 99 99 99 99 99 9a` |
| `7a` | 122 | bigint | 8 bytes BE signed | `5n` -> `7a 00 00 00 00 00 00 00 05` |
| `79` | 121 | false | none | |
| `78` | 120 | true | none | |
| `77` | 119 | string | VarString | `"x"` -> `77 01 78` |
| `76` | 118 | object | VarUint(nKeys), then (VarString key, Any value)* | `{a:1,b:[]}` -> `76 02 01 61 7d 01 01 62 75 00` |
| `75` | 117 | array | VarUint(len), Any* | `[1,"a"]` -> `75 02 7d 01 77 01 61` |
| `74` | 116 | Uint8Array | VarUint8Array | `[1,2]` -> `74 02 01 02` |

Number selection is the part most likely to diverge in a port (`encoding.js:543-557`):
1. If `Number.isInteger(n) && Math.abs(n) <= 0x7FFFFFFF` -> tag 125 (VarInt). Includes `-0` (becomes `7d 40`, decodes back to `-0`).
2. Else if the value survives a float32 round trip (`isFloat32`, `encoding.js:494-497`) -> tag 124. This includes `2^31` (`7c 4f 00 00 00`), `-2^31` (`7c cf 00 00 00`), `2^53` (`7c 5a 00 00 00`), `+/-Infinity` (`7c 7f 80 00 00`, `7c ff 80 00 00`), and any integer with <= 24 significant bits.
3. Else tag 123 (float64): `0.1`, `-2^31-1`, `1e300`, and **NaN** (`7b 7f f8 00 00 00 00 00 00`; NaN fails the `===` round-trip test).
So the C# encoder needs: int test with the 2^31-1 bound by absolute value, then exact float32 round trip, else double. `NaN` canonical bytes are `7f f8 00 00 00 00 00 00`.

Other encoder behaviours **[verified]**:
- Objects: `Object.keys(data)` order, i.e. **integer-like keys first in ascending numeric order, then string keys in insertion order**: `{2:1, 1:2, b:3, a:4}` -> keys written `1,2,b,a`. A C# `Dictionary` or `IDictionary` input must replicate this ordering to be byte-exact against JS. Values that are `undefined` are *kept* (`{u: undefined}` -> `76 01 01 75 7f`).
- `Date`, `Map`, `Set`, class instances: `typeof === 'object'`, not array, not `Uint8Array` -> tag 118 with `Object.keys` (a `Date` is `76 00`, an empty object; a `Map` is `76 00`). Functions and symbols -> `7f` (undefined). Typed arrays other than `Uint8Array` (e.g. `Float32Array`) are objects with index keys (`76 02 01 30 7d 00 01 31 7d 00`). Node `Buffer` is a `Uint8Array` subclass -> tag 116.
- Array holes: `data.length` is used and each `data[i]` is read, holes become `undefined` (`7f`).
- `-0` inside arrays/objects behaves as above.

Decoder behaviours **[verified]**:
- Tag lookup is `readAnyLookupTable[127 - readUint8()]` (`decoding.js:497`). Any byte outside 116..127, and EOF in the tag position (`127 - undefined = NaN`), throws `TypeError: ... is not a function`. A port must map this to a decode error.
- Payload truncation: VarInt at EOF yields `0` silently (see 1.3) - `7d` at EOF decodes to integer 0 with no error; float read past buffer -> `RangeError`; string/bytes length past buffer -> `RangeError`.
- Float32 NaN decodes to NaN (JSON-serialises as null in the test script; irrelevant to the wire).
- Objects: duplicate keys -> last wins (`{a:1,a:2}` -> `{a:2}`); key `__proto__` assigned with a non-object value is silently dropped (`obj[key] = v` hits the prototype setter) **[verified]**. These are JS-specific artefacts; the port will likely not replicate them, which is a deliberate divergence to record.
- No depth limit: 100000 nested one-element arrays throws `RangeError: Maximum call stack size exceeded` **[verified]**. In C# an unbounded recursion is a process-fatal `StackOverflowException`, so this is a hard requirement for the hostile-input ticket (TASK-1.13): the port needs an explicit depth cap or iterative decoder, and the suite needs a deeply nested test input.
- Array length prefix is not sanity-checked against remaining bytes (`readAny` loops `len` times, failing on the first missing tag), so a huge length costs only time until EOF.

### 1.8 RLE-family encoders (the V2 column encoders)

All four are in `lib0/encoding.js`; Yjs uses exactly these in `UpdateEncoderV2` (`UpdateEncoder.js:176-184`). Bytes below are **[verified]**.

**`RleEncoder(writeUint8)`** (`encoding.js:615-650`), used for `info` and `parentInfo`. Writes a value, then (when the *next different* value arrives) writes `VarUint(count - 1)` for the run just finished. **The final run's count is never written** (`toUint8Array` is the plain buffer; there is no flush). `[1]`->`01`, `[1,1,1]`->`01`, `[1,2]`->`01 00 02`, `[0,0,5]`->`00 01 05`, `[]`->empty. The decoder (`decoding.js:504-535`) compensates: after reading a value, if no bytes remain (`hasContent` false) it sets `count = -1` and repeats that value forever. The decoder treats the stream as ended based on `pos !== arr.length`.

**`UintOptRleEncoder`** (`encoding.js:742-774`, flush at 722-732), used for client, typeRef, len. Each run is `VarInt(count===1 ? s : -s)`, and if `count > 1` additionally `VarUint(count - 2)`. Unlike `RleEncoder`, **the final run is flushed** in `toUint8Array`. Note `-s` for `s = 0` is `-0`, giving `40`. `[1]`->`01`, `[1,1,1]`->`41 01`, `[0]`->`00`, `[0,0]`->`40 00`, `[0,0,0]`->`40 01`, `[1,2,2]`->`01 42 00`, `[]`->empty. Initial state `s=0,count=0`, so a leading `0` joins the (empty) run correctly. Decoder (`decoding.js:592-619`): reads a VarInt; **any negative value (including -0) means a count follows** (`isNegativeZero(n)` is `n < 0` for non-zero n, `math.js:58`), `s = -s`, `count = VarUint + 2`.

**`IntDiffOptRleEncoder`** (`encoding.js:853-888`, flush at 821-834), used for keyClock, leftClock, rightClock. State `s` (last value, init 0), `diff` (init 0), `count`. A value `v` extends the run iff `v - s === diff`. Flush writes `VarInt(diff*2 + (count===1 ? 0 : 1))` and, if `count > 1`, `VarUint(count - 2)`. Final run is flushed. `[0]`->`00`; `[5]`->`0a`; `[1,2,3]`->`03 01` (diff 1, count 3); `[5,5]`->`0a 00` (diff 5 then diff 0); `[0,0,0]`->`01 01`; `[3,1]`->`06 44` (diff 3, diff -2 -> -4 -> `44`); `[]`->empty. Decoder (`decoding.js:650-682`): `diff = readVarInt`, `hasCount = diff & 1`, `diff = floor(diff/2)`, `count = hasCount ? VarUint + 2 : 1`, then `s += diff` on every read. (Negative encoded values: `(2d+1) & 1 == 1` and `floor((2d+1)/2) == d`, so negative diffs round-trip.)

**`StringEncoder`** (`encoding.js:900-930`), used for all V2 strings. Maintains a joined string and a `UintOptRleEncoder` of per-string **UTF-16 `string.length`** values. Output = `VarString(all strings concatenated)` + `lensEncoder` bytes (no outer length prefix; the V2 container wraps it in a VarUint8Array). `["a","bc"]`->`03 61 62 63 | 01 02`; `["€","😀"]`->`07 e2 82 ac f0 9f 98 80 | 01 02` (lengths 1 and 2 code units); `[]`->`00`; `[""]`->`00 00`. Decoder (`decoding.js:684-706`): reads the joined string as VarString (UTF-8 -> UTF-16), then each `read()` slices `spos .. spos + lens.read()` **in UTF-16 code units**. Consequences **[verified]**: lengths running past the string yield `""` rather than an error; a length that lands inside a surrogate pair returns a lone surrogate (`"\ud83d"` then `"\ude00"`). The internal 19-char `sarr` flush at `encoding.js:915-918` is a perf detail; output is identical to joining all.

---

## 2. State vector

Layout (V1 and V2 identical; `encodeStateVectorV2` just uses a `DSEncoderV2` whose `restEncoder` is the same plain buffer; **[verified]** bytes equal):

```
VarUint n
n * { VarUint client, VarUint clock }      // sorted by client DESCENDING
```
Refs: `encoding.js:601-608` (`writeStateVector`: sort `b[0]-a[0]`), `627-634`, `644`. Decode: `readStateVector` (`encoding.js:565-574`) via `decodeStateVector` which always uses the V1 DS decoder (`594`) - there is no V2 state-vector decoder (the V2 one is commented out, `584`). `encodeStateVectorV2` exists but is **not exported from `yjs/src/index.js`** (only `encodeStateVector` is, `index.js:66`); it is reachable from `utils/encoding.js` only. So the V1 and V2 state vector are the same bytes and only one decoder is public API.

Semantics: `clock` is the *next expected* clock for that client (= last struct's `id.clock + length`), `getStateVector` at `StructStore.js:37-44`. A client appears only if it has at least one struct. Empty doc: `00`. **[verified]**

Examples **[verified]**: client 1 with 3 clock units -> `01 01 03`. Clients 20 (clock 1) and 10 (clock 5): `02 14 01 0a 05`. Client 2^32-1 sorts first: `04 ff ff ff ff 0f 01 84 07 01 11 01 03 01` (clients 4294967295, 900, 17, 3 each at clock 1).

Decoder behaviour **[verified]**:
- Empty input -> `Error: Unexpected end of array`. `00` -> empty map. Truncated pair -> same error. Trailing bytes ignored. Duplicate client: later wins. Unsorted input accepted. A `clock` of 0 entry is stored as-is.
- Wrong `n` larger than entries -> throws at EOF; smaller than entries -> trailing ignored.
- `n` is a VarUint with no sanity cap; the loop fails at the first missing byte, so a huge `n` is cheap.

`encodeStateVectorFromUpdate(V2)` (`updates.js:194-246`): derives a state vector from an update without applying it. Rules, from the code: iterate structs (Skips included, `filterSkips=false`); per client track `currClock` only while the client's run starts at clock 0 and no Skip/gap has been seen (`stopCounting`); a client is emitted only if its final `currClock !== 0`. So a client whose first struct is not at clock 0, or a client reached only through a diff update, is **omitted**, and a Skip stops counting for the rest of that client. Order is the order of clients **in the update** (already descending for well-formed updates), not re-sorted. **[verified]** (`E svFromUpdate gap`: merge of clock 0 and clock 2 structs with a Skip between -> `01 1e 01`, only clock 1). Empty update (`00 00`) -> `00`. The V2 variant emits the same bytes as V1 (the writer only uses `restEncoder`), **[verified]**.

`parseUpdateMeta(V2)` (`updates.js:253-291`) returns `{from, to}` maps (first clock seen / end clock per client), in update order, ignoring gaps (Skips count as structs). **[verified]** `{from:{20:0,10:0}, to:{20:1,10:5}}` for the multi-client sample.

---

## 3. Update layout

### 3.1 Common skeleton (both versions)

```
VarUint numClients                                   // restEncoder
repeat numClients, clients in DESCENDING client-id order:
    VarUint numStructs                               // restEncoder (both versions)
    client                                           // V1: VarUint; V2: clientEncoder column
    VarUint startClock                               // restEncoder (both versions)
    numStructs * struct
DeleteSet                                            // section 4
```
Source: `writeClientsStructs` (`encoding.js:81-102`), `writeStructs` (`57-71`), reader `readClientsStructRefs` (`112-198`). Client ordering is `sort((a,b) => b[0]-a[0])` (line 99); numeric descending, so 4294967295 sorts before 900. Which clients are written: those with `state(client) > sv.get(client)`, plus every client missing from `sv` with clock 0 (`encoding.js:83-94`). `startClock = max(svClock, firstStruct.id.clock)` - the **sv clock, not the struct boundary** (line 59, 64). `numStructs = structs.length - findIndexSS(clock)`. If the sv clock lands inside a struct, that first struct is written with `offset = clock - struct.id.clock` (line 67): content sliced from `offset`, and its origin synthesised as `ID(client, clock+offset-1)` (`Item.js:656`), see 3.4.

An update with no clients and no deletes: V1 `00 00` (**[verified]** `encodeStateAsUpdate(emptyDoc)` and `mergeUpdates([])`). V2 empty doc: `00 00 00 00 00 00 01 00 00 00 00 00 00` (13 bytes: flag, 5 empty columns each `00`, string column `01 00`, three more empty columns, then rest `00 00`). **[verified]**

### 3.2 Struct (block) layout

First byte `info`:
- bits 1-5 (`& 0x1f`): content ref. `0` = GC, `10` = Skip, `1..9` item content kinds (table in 3.5). Other values 11..31 are invalid.
- bit 6 (0x20): item has `parentSub` (a map key).
- bit 7 (0x40): item has `rightOrigin`.
- bit 8 (0x80): item has `origin`.
Source: write `Item.js:655-700` (info at 659-662), read `encoding.js:130-193`.

Dispatch on read (`encoding.js:130`): `switch (BITS5 & info)`: `0` -> GC, `10` -> Skip, anything else -> Item path. In `updates.js` `lazyStructReaderGenerator` the order is different: `info === 10` (exact byte equality, line 52), then `(BITS5 & info) !== 0` -> Item, else GC (lines 52-79). Edge case: an `info` byte like `0x8a` or `0x2a` (ref bits = 10 plus flag bits) is a **Skip** in `readClientsStructRefs` but is an **Item** (with content ref 10, which then throws) in the lazy reader used by merge/diff/convert. An `info` of `0x80`/`0x40`/`0x20` (ref 0 plus flag bits) is **GC** in both. Test inputs should include these.

**GC** (`GC.js:47-50`): `info=0x00`, then `len`: V1 VarUint; V2 `lenEncoder`.

**Skip** (`Skip.js:45-48`): `info=0x0a`, then `VarUint len` **in `restEncoder` in both versions** (comment: Skips can't use predictable length encoding). Reader side: `encoding.js:137-142` and `updates.js:52-55` also read it as a plain VarUint from `restDecoder`.

**Item** (`Item.js:655-700`), in order:
1. `info` byte.
2. If bit 8: `origin` = ID (client, clock).
3. If bit 7: `rightOrigin` = ID.
4. **Only if neither bit 7 nor bit 8 is set**: `parentInfo` (1 = root type, then `string key`; 0 = parent is an item, then ID of parent item), and then, if bit 6, `parentSub` string. **If origin or rightOrigin is present, neither parent nor parentSub is written even when bit 6 is set**; they are inherited from the neighbour at integrate time (`Item.js:372-416`). Read side: `cantCopyParentInfo = (info & (BIT7|BIT8)) === 0` (`encoding.js:150`), lines 161-162.
5. Content, per 3.5.

ID write for left/right: V1 `VarUint client, VarUint clock`; V2: `clientEncoder.write(client)` + `leftClockEncoder`/`rightClockEncoder.write(clock)`. Note origin and right origin use **different** clock columns but the **same** client column, interleaved in struct order (`UpdateEncoder.js:207-218`). Parent-item ID uses `writeLeftID` (V1 `UpdateEncoder.js:40`, V2 line 207, so left clock column).

Root parent key: `findRootTypeKey(parent)` string through `writeString` (V2: string column, **not** the key-dedupe path). Item with `parentSub` writes the sub key through `writeString` too.

### 3.3 V1 content layouts (`UpdateEncoderV1`, `UpdateEncoder.js:36-125`)

| ref | kind | V1 payload | source |
|---|---|---|---|
| 1 | Deleted | VarUint len | `ContentDeleted.js:82`, read `:100` |
| 2 | JSON | VarUint len, then len * VarString(`JSON.stringify(v)`, or the literal `undefined` for undefined) | `ContentJSON.js:83-90`, read `:106-118` |
| 3 | Binary | VarUint8Array | `ContentBinary.js:76`, read `:92` |
| 4 | String | VarString | `ContentString.js:94`, read `:112` |
| 5 | Embed | VarString(`JSON.stringify(embed)`) | `ContentEmbed.js:79`, read `:97` |
| 6 | Format | VarString key, VarString(`JSON.stringify(value)`) | `ContentFormat.js:87`, read `:104` |
| 7 | Type | VarUint typeRef; for XmlElement(3)/XmlHook(5) also VarString name | `ContentType.js:153`, types' `_write` |
| 8 | Any | VarUint len, then len * Any | `ContentAny.js:86-93`, read `:107-114` |
| 9 | Doc | VarString guid, Any(opts) | `ContentDoc.js:121-124`, read `:140` |

Notes:
- Item length (clock units): Deleted = len; JSON/Any = element count; String = **UTF-16 code units** (`ContentString.js:22-24`, `this.str.length`); Binary, Embed, Format, Type, Doc = 1.
- Type refs (`ContentType.js:28-34`): Array 0, Map 1, Text 2, XmlElement 3, XmlFragment 4, XmlHook 5, XmlText 6. Reader table `ContentType.js:18-26`; XmlElement/XmlHook read a name (`YXmlElement.js:260`, `YXmlHook.js:97`) via `readKey`; written via `writeKey` (`YXmlElement.js:248-251`, `YXmlHook.js:84-87`). V1 `writeTypeRef` is a VarUint (so refs > 127 would multi-byte; none exist).
- ContentDoc opts (`ContentDoc.js:33-43`): object built as `{gc:false}` iff `!doc.gc`, `{autoLoad:true}` iff autoLoad, `{meta: ...}` iff meta non-null; key order gc, autoLoad, meta. **[verified]** sample: `... 02 67 31 76 02 08 61 75 74 6f 4c 6f 61 64 78 04 6d 65 74 61 76 01 01 61 7d 01 00`. Reader `createDocFromOpts` (`ContentDoc.js:11`) spreads opts into `new Doc({guid, ...opts, shouldLoad: opts.shouldLoad || opts.autoLoad || false})`; unknown keys in opts are passed through to the Doc constructor.
- V1 parentInfo is a **VarUint**; the reader tests `=== 1` (`UpdateDecoder.js:74-76`), so any other value (0, 2, 5...) is treated as "parent is an ID" and the next two VarUints are read as (client, clock) **[verified]**.
- V1 ContentString at an offset (`ContentString.js:94-96`) uses `str.slice(offset)`; slicing in the middle of a surrogate pair yields a lone surrogate which `TextEncoder` writes as `ef bf bd` (but clock accounting still counts one code unit). **[verified]**: doc `a😀b` (`61 f0 9f 98 80 62`, len 4); diff from sv clock 2 -> `01 01 01 02 84 01 01 04 ef bf bd 62 00` (4-byte content for 2 code units: U+FFFD + `b`). Likewise `ContentString.splice` (`ContentString.js:51-66`) replaces a split surrogate with U+FFFD on both halves permanently in the doc. So after inserting `X` inside a pair, the doc holds `a � X � b` and the update is `61 ef bf bd ... ef bf bd 62`. **[verified]**

### 3.4 Offsets, diff and split semantics for writing

Writing a struct with `offset > 0` (`Item.js:655-657`, `GC.js:47-50`, `Skip.js:45-48`): GC/Skip simply write `length - offset`; Item uses `origin = ID(client, clock + offset - 1)` (overriding the stored origin), keeps `rightOrigin`, `parentSub`, and `content.write(encoder, offset)` (which slices: String `slice(offset)`, Any/JSON from index `offset`, Deleted `len - offset`). Single-unit contents (Binary, Embed, Format, Type, Doc) can never be at `offset > 0` (`splice` throws `methodUnimplemented`, and sv clocks can't point inside a length-1 struct).

### 3.5 V2 container (`UpdateEncoderV2`, `UpdateEncoder.js:162-320`)

`toUint8Array` (lines 187-202) produces:

```
VarUint 0                          // feature flag, written 0; decoder reads and ignores (UpdateDecoder.js:172)
VarUint8Array keyClockEncoder      // IntDiffOptRle   - key clocks
VarUint8Array clientEncoder        // UintOptRle      - clients (item clients, origin/rightOrigin clients, struct clients)
VarUint8Array leftClockEncoder     // IntDiffOptRle   - origin clocks and parent-item-id clocks
VarUint8Array rightClockEncoder    // IntDiffOptRle   - rightOrigin clocks
VarUint8Array infoEncoder          // Rle(uint8)      - info bytes
VarUint8Array stringEncoder        // StringEncoder   - all strings
VarUint8Array parentInfoEncoder    // Rle(uint8)      - parentInfo 0/1
VarUint8Array typeRefEncoder       // UintOptRle      - typeRef
VarUint8Array lenEncoder           // UintOptRle      - GC len, ContentDeleted len, ContentAny len, ContentJSON len
<rest, raw, NOT length-prefixed>   // restEncoder
```
The rest section runs to the end of the buffer (`encoding.writeUint8Array`, comment at line 199). It holds, in order of writing: `numClients`, per client `numStructs` and `startClock`, Skip lengths, `Any`/binary/embed payloads, and finally the delete set. Reader: `UpdateDecoderV2` constructor reads the flag then 9 VarUint8Arrays in this exact order and leaves `restDecoder` positioned at the remaining bytes (`UpdateDecoder.js:163-182`).

Per-field routing in V2 (`UpdateEncoder.js:207-319`):

| field | V2 route |
|---|---|
| client (struct header), item `writeClient` | `clientEncoder` |
| origin / rightOrigin / parent-item ID | client -> `clientEncoder`; clock -> `leftClockEncoder` (origin, parent id) or `rightClockEncoder` (rightOrigin) |
| info | `infoEncoder` |
| string (root key, parentSub, ContentString, guid) | `stringEncoder` |
| parentInfo | `parentInfoEncoder` (1 = root key) |
| typeRef | `typeRefEncoder` |
| len (GC, Deleted, Any count, JSON count) | `lenEncoder` |
| Any payloads (ContentAny elements, ContentDoc opts) | `restEncoder` via `writeAny` |
| ContentEmbed / ContentFormat value | `writeJSON` = `writeAny` on `restEncoder` (not JSON text), `UpdateEncoder.js:285-287` |
| ContentJSON elements | `writeString(JSON.stringify(c))` -> `stringEncoder` (still JSON text) |
| ContentBinary | `writeBuf` = VarUint8Array on `restEncoder` |
| keys: ContentFormat key, XmlElement name, XmlHook name | `writeKey` (below) |
| numClients, numStructs, startClock, Skip len | raw VarUint on `restEncoder` |
| delete set | section 4 (V2 variant), on `restEncoder` |

`writeKey` (`UpdateEncoder.js:297-319`): looks up `keyMap` but the `keyMap.set` is commented out (line 313), so a key is **never found**: every call does `keyClockEncoder.write(this.keyClock++)` then `stringEncoder.write(key)`. The decoder (`readKey`, `UpdateDecoder.js:271-280`) reads a key clock; if `< keys.length` it returns the cached key, else it reads a new string and pushes it. Net: V2 keys are always fresh strings with a monotonically increasing clock 0,1,2,... (and the IntDiffOptRle encodes that as `diff=1, count=n`). A decoder must still implement the cache lookup branch for compatibility with other encoders, though 13.6.33 never produces repeated clocks. **[verified]** by `testKeyEncoding` (`yjs/tests/updates.tests.js:119-132`).

V2 differences that matter for equality of bytes:
- The info column is an RLE without trailing count; the last run's length is implicit (1.8).
- Everything in the columns is flushed when `toUint8Array` runs; an unused column is an empty array encoded as VarUint8Array length `00` (the string column is `01 00` for "empty joined string, no lens").
- V1 -> V2 conversion of the same logical content is exactly `convertUpdateFormatV1ToV2` and equals the native V2 encoding **[verified]** (sample multi-client doc, both directions equal native bytes).

### 3.6 Worked examples **[verified]**

Text `"hi"` by client 1 (root key `t`):

V1 `01 01 01 00 04 01 01 74 02 68 69 00`
= numClients 1 | numStructs 1 | client 1 | clock 0 | info `04` (String, no origins) | parentInfo 1 | key `01 74` ("t") | content `02 68 69` | ds numClients 0.

Map (client 1): `m.set('k',{a:1}); m.set('s','v'); m.set('k',2)`

V1 `01 03 01 00 | 21 01 01 6d 01 6b 01 | 28 01 01 6d 01 73 01 77 01 76 | a8 01 00 01 7d 02 | 01 01 01 00 01`
- Header: numClients 1, numStructs 3, client 1, startClock 0.
- Struct @0: info `21` = ContentDeleted (1) + parentSub bit. parentInfo `01`, key `01 6d` ("m"), parentSub `01 6b` ("k"), len `01`. This is the overwritten `{a:1}`, whose content was turned into ContentDeleted at transaction cleanup (gc on).
- Struct @1: info `28` = Any (8) + parentSub. parentInfo `01`, key "m", parentSub `01 73` ("s"), Any count `01`, Any `77 01 76` ("v").
- Struct @2: info `a8` = Any + parentSub + origin bit. origin ID `01 00` = (client 1, clock 0); no parent fields (origin present); Any count `01`, Any `7d 02` (int 2).
- Delete set: 1 client (`01`), client 1, 1 range, clock 0, len 1.

Full hex for further samples (including all V2 forms) is in section 12. Section 12 lists hex only; decode it with the rules above or by running the generator.

---

## 4. Delete set

### 4.1 Model
`DeleteSet {clients: Map<client, DeleteItem{clock,len}[]>}` (`DeleteSet.js:16-47`). In update output it is always sorted ascending by clock and merged (adjacent *or* overlapping ranges coalesced: `left.clock + left.len >= right.clock`, `sortAndMergeDeleteSet`, `DeleteSet.js:116-138`). `readDeleteSet` does **not** sort or merge what it reads (`248-263`); `mergeDeleteSets` does (`144-164`).

### 4.2 V1 layout (`writeDeleteSet`, `DeleteSet.js:222-239`; `DSEncoderV1`, `UpdateEncoder.js:8-34`)
```
VarUint numClients
per client, DESCENDING client id:
    VarUint client
    VarUint numDeletes
    numDeletes * { VarUint clock, VarUint len }
```
The decoder skips clients whose `numDeletes == 0` (`DeleteSet.js:255`), so `00 01 05 00` parses as an empty ds.

### 4.3 V2 layout (`DSEncoderV2`, `UpdateEncoder.js:127-160`; `DSDecoderV2`, `UpdateDecoder.js:125-157`)
Same outer structure, all in the rest buffer, but the pair is delta-coded relative to a running `dsCurrVal` that is **reset to 0 for each client** (`resetDsCurVal`):
- `clock` is written as `VarUint(clock - dsCurrVal)` and then `dsCurrVal = clock`.
- `len` is written as `VarUint(len - 1)` and then `dsCurrVal += len`.
So a client with ranges `{2,len2}`, `{8,len1}` is `01 (client) 02 (count) 02 01 04 00`: first clock diff 2, len-1 = 1 (dsCurrVal=4), second clock diff 4, len-1 = 0. **[verified]** (client 1, document `abcdefghij`, `delete(2,2)` and `delete(6,1)`): V1 ds `01 01 02 02 02 08 01` (1 client, client 1, 2 ranges, (2,2), (8,1)); V2 ds `01 01 02 02 01 04 00`.

Encoder hard errors: `writeDsLen(0)` throws `Error('Unexpected case')` (`UpdateEncoder.js:154-156`); V1 would write `00`. Consequence: a *received* V1 delete range with `len = 0` that is parked as pending (clock >= known state, see 4.5) makes `applyUpdate` throw `Error('Unexpected case')`, because the pending ds is re-encoded through the V2 writer (`readAndApplyDeleteSet` -> `writeDeleteSet(UpdateEncoderV2)`, `DeleteSet.js:325-330`). **[verified]** with `00 01 05 01 00 00` (client 5, clock 0, len 0).
Encoder on unsorted input: `clock - dsCurrVal` can be negative and VarUint writes garbage silently (1.2). The model never produces this.

### 4.4 Which deletes an update carries
- `encodeStateAsUpdate` / `writeStateAsUpdate`: the **whole** delete set of the doc, derived from the struct store (`createDeleteSetFromStructStore`, `DeleteSet.js:188-213`; runs of consecutive `deleted` structs are merged into one range, regardless of sv). Never diffed against the target sv (`encoding.js:504-507`). **[verified]**: a diff update with all structs known still carries the full ds (`E diff sv{30:99}` -> `00 01 1e 01 02 01`).
- Transaction `update` event: only the transaction's own delete set, sorted and merged first (`Transaction.js:130-138`, emitted 366-379); `update` is emitted only if there are struct changes or deletes. **[verified]**: three deletes within one transaction on adjacent ranges produced one range `{0,3}`.
- GC'd content: when a deleted item is collected its struct is rewritten as `ContentDeleted` (or `GC` if its parent was collected, `Item.js:gc`), so after GC the same doc encodes **different struct bytes** (info `01` + len instead of info `04` + string) but the same ds. **[verified]**: client 40, `insert(0,'hello'); delete(0,5)` encodes as `01 01 28 00 | 01 01 01 74 05 | 01 28 01 00 05` (struct: info `01` ContentDeleted, parentInfo 1, key "t", len 5; ds: client 40, 1 range, (0,5)). With `new Doc({gc:false})` (client 41) the struct keeps its string: `01 01 29 00 | 04 01 01 74 05 68 65 6c 6c 6f | 01 29 01 00 05`.

### 4.5 Applying a delete set (`readAndApplyDeleteSet`, `DeleteSet.js:278-332`)
For each range: if `clock < state(client)`, splits the first struct at `clock` if not already deleted and the last at `clockEnd`, then `struct.delete(transaction)` each in range; any part `>= state` goes to an `unappliedDS` (lines 290-293), and ranges wholly at or beyond `state` go there too (321). The unapplied ranges are re-encoded as a **V2 update with 0 structs + ds** (`00` for numClients then the V2 DS; lines 325-330) and stored in `store.pendingDs`. On the next `applyUpdate`, pendingDs is re-read (`encoding.js:421-434`) and merged. **[verified]**: `00 01 05 01 03 02` (delete clock 3 len 2 of unknown client 5) -> accepted, `pendingDs` set, doc state vector unchanged. Duplicate client entries in one ds are accepted and each applied independently.

---

## 5. Applying updates (`applyUpdate`, `applyUpdateV2`)

`applyUpdate(doc, update, origin)` -> `readUpdateV2(decoder, doc, origin, new UpdateDecoderV1(decoder))` (`encoding.js:476-492`, `382-449`). Sequence inside one transaction with `local=false`:
1. `readClientsStructRefs` decodes **all** structs into memory first (`encoding.js:112-198`). Decode errors here abort before any struct is integrated; no partial state change **[verified]** (`struct then truncated mid-struct` -> doc empty).
2. `integrateStructs` (`227-359`): processes clients in descending order via a stack-based dependency resolver. Per struct: `offset = localState - struct.id.clock`; `offset < 0` -> there is a gap: the struct and its successors go to `restStructs` and the client gets a `missing` entry (`clock - 1`); else `getMissing` (origin/rightOrigin/parent in another client with `clock >= state` -> that client is missing); else if `offset == 0 || offset < length` -> `integrate(transaction, offset)` (partial overlap is trimmed via split, `Item.js:419-425`); otherwise (`offset >= length`) already known, skipped. Skip structs are skipped entirely (line 302).
3. Leftovers are re-encoded as a V2 update (`writeClientsStructs(encoder, restStructs, new Map())` then `00` for an empty ds) into `store.pendingStructs = {missing, update}` (`encoding.js:350-357`, 396-417). Retry logic at 397-404 and 444-448.
4. `readAndApplyDeleteSet` (section 4.5), then pendingDs handling.
5. Transaction cleanup runs in `finally` (`Transaction.js:415-440`): observers and `update` events fire even when decoding threw midway.

Observed behaviours **[verified]**:
- Idempotent: re-applying the same update changes nothing (text and sv unchanged).
- Out-of-order: applying update 2 (depends on 1) first -> `pendingStructs` set with `missing = {30:0}`, text unchanged, sv unchanged; the pending update is **included** in the next `encodeStateAsUpdate` (`encoding.js:527-540`: `pendingDs` appended and `diffUpdateV2(pendingStructs.update, sv)` appended, then merged via `mergeUpdates`/`mergeUpdatesV2`, so with pending state the output bytes are the *merge* output, not the plain writer output). Applying update 1 later flushes pending automatically.
- Trailing bytes after a complete update are ignored (`01 01 01 00 04 01 01 74 02 68 69 00 ff ff` OK). Empty-update `00 00` is OK.
- Clock gap in the first struct of a client (`clock 5` with state 0) is accepted and parked as pending; sv unchanged.
- Parent given as an ID of an item that is not a type (e.g. a string item): `getMissing` evaluates `parentItem.content.type`, which is `undefined` for non-type content (`Item.js:404-409`), so `parent` is falsy and `integrate` takes the else branch that creates a `GC` of the same id/length instead (`Item.js:529-531`). No error; the doc's clock advances. **[verified]** (`01 02 01 00 04 01 01 74 01 61 04 00 01 00 01 62 00` -> text `a`, sv `01 01 02`).
- Zero-length struct (String `""`): accepted, advances nothing, yields an item of length 0 in the store (`empty string item (len 0)` -> OK, sv `01 01 00` i.e. client with clock 0). A follow-up struct at the same clock then integrates normally. Do not assume lengths are >= 1 when porting; the JS code does not check.
- Same client twice in one update's client list: the second `clientRefs.set(client, ...)` replaces the first (map keyed by client, `encoding.js:127`), so the first run is silently discarded **[verified]** (result: nothing integrated, pending). Port must keep "last wins" if it wants identical behaviour; this is a hostile-input behaviour, not a valid-input one.
- `parentInfo` other than 0/1 -> read as ID, so no root-key string is consumed; leads to downstream misparse/pending.
- A root type referenced for an unknown name is created as an abstract placeholder: `doc.get(name)` with no constructor (`encoding.js:161`, `Doc.js:215-247`); a later `getText(name)` converts it.

### 5.1 Unknown / invalid content refs and type refs **[verified]**
- Content ref 11..31 in `readItemContent`: `contentRefs[n]` is `undefined` -> `TypeError: contentRefs[...] is not a function`; refs 0 and 10 present in the array as functions that throw `Error('Unexpected case')` (`Item.js:712-724`) - not reachable via `readClientsStructRefs` since 0 and 10 are dispatched earlier, but reachable through `updates.js` for `info` bytes like `0x2a` (ref 10 + flags) which go the Item path in the lazy reader.
- Type ref 7+ in ContentType: `typeRefs[n]` undefined -> `TypeError` (`ContentType.js:171`).
- Both errors occur *after* earlier bytes of the struct are consumed; error class can differ from "unknown ref" if bytes run out first (e.g. `info=0x1f` with too few bytes throws `Unexpected end of array` before reaching `contentRefs`). Ordering of consumption defines which error you see; only "it throws" is stable.

---

## 6. mergeUpdates / mergeUpdatesV2

`mergeUpdates(updates)` = `mergeUpdatesV2(updates, UpdateDecoderV1, UpdateEncoderV1)` (`updates.js:186`, `333-459`). V2 default decoders/encoders.

Behaviour:
- **`updates.length === 1` returns the same array instance unparsed** (`updates.js:334-336`). No validation, no re-encode: `mergeUpdates([garbage])` returns `garbage` **[verified]**. `[]` -> not special-cased, yields the empty update (`00 00` V1) **[verified]**.
- Otherwise each input is read lazily with `filterSkips = true` (`LazyStructReader`, `updates.js:84-110`): Skip structs in inputs are dropped during reading.
- Loop: filter out exhausted decoders; sort decoders by (client DESC, then clock ASC, ties broken so a non-Skip sorts before a Skip) (`356-373`); take the first. Writes keep client-descending order, clock-ascending within client.
- Overlap handling: structs entirely covered by what has already been written are dropped (`388-391`); a struct partially overlapping is **sliced** with `sliceStruct` (`updates.js:301-322`) so only the new tail is written. For an Item the slice has `origin = ID(client, clock+diff-1)`, `rightOrigin` preserved, `parent`/`parentSub` preserved, content `splice(diff)`. **[verified]** `merge([abc, diffFrom2])` yields `Item(0,3)` then `Item(3,4)` with the second written as `84 1e 02 04 "defg"` (origin (30,2), no parent info because origin is set).
- Gap between consecutive structs of one client is filled with a **Skip** whose length is the gap; consecutive Skips are extended (`405-418`). **[verified]** `[0,2]` of single-character inserts -> `01 03 1e 00 <item> 0a 01 <skip len 1> 44 1e 00 01 63 00 ...` (3 structs: Item(0,1), Skip(1,1), Item(2,1)).
- Adjacent structs are coalesced with `mergeWith` (`429`). For **GC and Skip**, `mergeWith` extends length (adjacent GC+GC with lengths 2+3 -> one GC of 5; overlap 4 and 6 at offset 2 -> one GC of 8 **[verified]**). For **Item**, `Item.mergeWith` (`Item.js:571-`) requires `this.right === right`, which is never true for items produced by the lazy reader (constructed with `right = null`), so **items are never coalesced by mergeUpdates**: adjacent items remain separate structs even if they would merge in a live doc (`items adjacent` -> `Item(30,0,3)`, `Item(30,3,3)`). **[verified]** The byte output of `mergeUpdates` therefore differs from `encodeStateAsUpdate` of the doc built from the same updates whenever the live doc would merge items (the live doc: single `Item(0,7)`). A port that "optimises" by merging items would break byte equality.
- Delete sets: each input's ds is read (`updates.js:455`: `readDeleteSet` on each decoder *after* the struct loop consumed it), merged with `mergeDeleteSets` (sort + coalesce adjacent/overlapping), and written once (`456-457`). **[verified]** multi-client example: result ds `2:{0,1}, 1:{0,1}`.
- Order independence: `mergeUpdates([2,0,1])` equals `[0,1,2]` bytes **[verified]** for the sampled inputs.
- Duplicate input: `merge([a,a])` equals `a` **[verified]** for a single-struct update.
- Both inputs must be the same format; V1 + V2 mixed bytes with `mergeUpdates` (V1 decoder on V2 bytes) did not throw on the sample (`merge([u1,u2])` returned `01 01 01 00 04 01 01 74 02 68 69 00`, i.e. it silently decoded V2 bytes as V1 and merged the interpretation) **[verified]**. This is "garbage in, plausible out"; the suite should not rely on it.
- Errors **[verified]**: `[emptyBytes, valid]` -> `Error: Unexpected end of array`; `[garbage 'ffff', valid]` -> same; truncated or missing ds -> same. The error is thrown before any output exists (pure function, no state).
- Output of the writer path: `LazyStructWriter` + `writeStructToLazyStructWriter` (`updates.js:525-539`) + `finishLazyStructWriting` (`547-571`): per client, `numStructs` and the client's buffered payload are emitted, with `numClients` first. For V2 the columns inside `updateEncoder` are shared across clients, but the per-client `restEncoder` fragments are swapped out each flush (`512-518`). Net layout equals `writeClientsStructs`.

### 6.1 `diffUpdate` / `diffUpdateV2` (`updates.js:467-507`)
`diffUpdate(update, sv)`: decode sv with the **V1** state-vector decoder always (`decodeStateVector`, line 468, even for `diffUpdateV2`). For each client in update order: skip a leading Skip (`477-481`); if `clock + length > svClock`, write the first struct with `offset = max(svClock - clock, 0)` and all following structs of that client with offset 0 (`482-488`); else drop structs while `clock + length <= svClock` (`489-494`). **Skips after the first written struct are written through** (no Skip filtering after the first struct of a client, `485-487`). Then the **entire** ds is re-read and written (`497-499`), not filtered.
**[verified]** sv `{30:1}` on `full` -> `01 02 1e 01 84 1e 00 01 62 41 1e 00 01 01 1e 01 02 01`: first struct is rewritten with offset 1 (it had length 2: 'ab' -> 'b', origin (30,0)). sv beyond update -> zero structs (`00` numClients) plus full ds. sv `{}` returns the whole update re-encoded (bytes equal to `full`). Empty sv *bytes* (zero-length) -> `Error: Unexpected end of array`; `00` is the valid empty sv.
`diffUpdate` re-encodes everything (does not preserve input bytes), and for a mid-struct start the item header changes (explicit origin).

### 6.2 `convertUpdateFormat*` (`updates.js:579-591`, `717-722`)
Read all structs lazily (**filterSkips = false**, so Skips are preserved), pass each through a block transformer (identity for convert, obfuscator for `obfuscateUpdate*`), write with the target encoder, then re-read and rewrite the ds in the target format. Properties **[verified]**: V1->V2 equals native V2 of the same doc and V2->V1 equals native V1 (sample multi-client doc); V1->V2->V1 returns identical bytes for a doc with text, format, map and array content. Truncated/empty input -> `Error: Unexpected end of array`. `convertUpdateFormatV2ToV1(<V1 bytes>)` -> `RangeError: Invalid typed array length: 104` (V1's first bytes parsed as V2 column lengths). Since format confusion yields RangeError/Error/TypeError or occasionally success depending on the bytes, the port must treat all of them as one "decode error" category.
Structure preserved: converting does **not** merge items, so unlike the live doc, a converted update keeps the exact struct boundaries of its input.

### 6.3 `obfuscateUpdate*` (`updates.js:603-712`)
Out of scope for wire parity (it is a debugging utility) but it is a deterministic transform and could serve as a fixture, e.g. `testObfuscateUpdates` (`yjs/tests/updates.tests.js:311`).

---

## 7. Update emission sources (what produces bytes)

| API | bytes | ref |
|---|---|---|
| `Y.encodeStateAsUpdate(doc, sv?)` | V1 full or diff update + pending (merged if pending exists) | `encoding.js:555`, `522-541` |
| `Y.encodeStateAsUpdateV2(doc, sv?)` | V2 equivalent (sv is always V1-encoded bytes, `decodeStateVector`, line 523) | `encoding.js:522` |
| `doc.on('update')` / `'updateV2'` | transaction delta, V1 / V2 | `Transaction.js:366-379` |
| `Y.encodeStateVector(doc|Map)` | state vector | `encoding.js:644` |
| `y-protocols` sync step 1/2/update | VarUint msgType + VarUint8Array payload | `y-protocols/sync.js:48-62,96-99,118-133` |

y-protocols framing **[source only]**: step1 = `00` + VarUint8Array(V1 state vector); step2 = `01` + VarUint8Array(V1 update); update = `02` + VarUint8Array(V1 update). `readSyncMessage` throws `Error('Unknown message type')` for anything else (line 131). `readSyncStep2/readUpdate` swallow all apply errors with `console.error` and an optional errorHandler (`sync.js:82-90`), so malformed updates delivered through y-protocols **never throw to the caller**, while the same bytes passed to `Y.applyUpdate` do. This matters for the error-model ticket (TASK-1.12): the two layers differ.

When pending data exists, `encodeStateAsUpdate` returns `mergeUpdates([base, convertV2ToV1(pendingDs), convertV2ToV1(diff(pendingStructs))])` for V1 (`encoding.js:533-536`), so the result goes through the lazy merge writer and has Skip/GC/non-merged-items characteristics from section 6 rather than from the live-store writer.

---

## 8. JS behaviour on malformed or truncated input (summary table)

All **[verified]** by running the real source. "Throw class" is V8's class; the port should map to its own error type. "Side effects" matters: applyUpdate is not atomic with respect to *integration*, only with respect to *struct decoding*.

### 8.1 Truncation sweeps
Every proper prefix of a ~70-byte V1 update and ~80-byte V2 update (multi-client, text, map with nested object, array with binary and float) was applied to a fresh doc:
- **No prefix succeeded** for either version (every cut threw). V1 outcomes by class: `Error('Unexpected end of array')` (45 of 71 cuts), `RangeError` (invalid typed array / DataView length: cut inside a length-prefixed string, buffer, or float), `TypeError` (cut inside an Any: the tag byte read as `undefined`). V2: same classes, plus the cut inside the column header region always `RangeError` (a column length prefix exceeding the buffer) or `Error`.
- Implication: "truncated => failure" holds for these fixtures, but is **not a universal law**: any prefix that happens to be a complete valid update succeeds (trailing bytes are ignored, so e.g. an update followed by junk is still valid, and a valid update that is itself a prefix of a longer one is fine). A generator should assert failure only for cuts it knows land strictly inside the structure.
- **Partial application on failure**: if the truncation falls in the delete-set region (struct section complete), decoding of structs succeeded and `integrateStructs` already ran before the ds read threw; the doc keeps the integrated structs (e.g. text became `"hi"`, sv `01 01 02`). **[verified]** ("truncated before DS": `Error: Unexpected end of array`, but text `"hi"`.) Failure inside the struct section applies nothing from that update.
- Observer `update` events fire after a failed `applyUpdate` as well, because the transaction cleanup is in `finally` (`Transaction.js:432`).

### 8.2 Targeted malformed cases (V1 unless stated)

| input | JS result |
|---|---|
| empty bytes | `Error: Unexpected end of array` |
| `00 00` | OK, no-op |
| valid + trailing junk | OK (junk ignored) |
| `numStructs` = 2^32 | `RangeError: Invalid array length` (`new Array(n)`, `encoding.js:123`) |
| `numStructs` = 2^32-1, body short | `Error: Unexpected end of array` (sparse array, fast) |
| `numClients` = 2^32-1 | `Error: Unexpected end of array` |
| 10x `80` then `00` | `Error: Unexpected end of array` (unterminated) |
| clock = 2^53-1 | accepted, pending |
| client id >= 2^53 (e.g. 2^63 as `80*9 01`) | accepted; value is the nearest double (precision loss), re-encoded sv shows `80 .. 01` |
| info with content ref 11 | `TypeError` ("not a function") |
| unknown typeRef 9 | `TypeError` |
| invalid UTF-8 in string or key | `TypeError` (fatal decoder) |
| `ed a0 80` (CESU surrogate) | `TypeError` |
| string `ef bb bf 61` | OK; text `"﻿a"`, length 2 |
| JSON content `{{` | `SyntaxError` from `JSON.parse` |
| JSON content text `undefined` | OK; element is `undefined` (special-cased, `ContentJSON.js:111-113`) |
| Any tag 0x10, 0x73, 0x80 | `TypeError` |
| Any tag `7d` as the last byte | the VarInt payload reads as 0 silently (1.3), so the struct *is integrated* (sv advanced), and only the following missing ds read throws `Error: Unexpected end of array`. Result: error AND partial application |
| Any float32 truncated | `RangeError: Invalid DataView length 4` |
| Binary length beyond buffer | `RangeError` |
| `parentInfo` = 2 or 5 | treated as "parent ID", misparse, then pending/RangeError/OK depending on following bytes |
| ds with 0 deletes for a client | accepted, ignored |
| ds delete len 0 | `Error: Unexpected case` (only on V2 re-encode path, see 4.3) |
| ds range beyond known state | accepted, parked in `pendingDs` |
| ds truncated (count says 2, 1 present) | `Error: Unexpected end of array` |
| V2 bytes through V1 applier | accepted on a tiny sample (the leading `00` flag and column bytes parsed as an empty update); V1 bytes through V2 applier -> `RangeError` |
| V2 feature flag != 0 | ignored; OK |
| V2 column length larger than remaining | `RangeError` |
| `applyUpdate(doc, undefined)` | `TypeError: Cannot read properties of undefined` |
| plain `Array` `[0,0]` instead of Uint8Array | accepted (decoder only indexes `arr[i]`; but `readUint8Array` would use `arr.buffer`, so any content needing a slice throws) |
| Node `Buffer` | accepted, fine |

Rows that say "OK" for a hostile input are the dangerous ones for a safe port: the port must decide whether to replicate (compat) or reject (strict). That decision belongs to TASK-1.12; this file just records what JS does.

### 8.3 Reader primitives at EOF (so a port can reproduce or knowingly diverge)

| primitive | at EOF |
|---|---|
| `readUint8` | `undefined`, advances |
| `readVarUint` | throws |
| `readVarInt` (first byte) | returns 0, advances (**silent**) |
| `readVarInt` (after continuation) | throws |
| `UintOptRleDecoder.read` on empty buffer | returns 0 forever (**silent**) |
| `IntDiffOptRleDecoder.read` on empty buffer | returns 0 forever, `s` stays 0 (**silent**) |
| `RleDecoder.read` on empty buffer | throws `Unexpected end of array`: `readUint8` returns `undefined` but advances `pos` to 1, so `hasContent` (`pos !== length`) is true and a count VarUint read throws. **[verified]** |
| `RleDecoder` after final run | repeats last value forever |
| `StringDecoder.read` beyond joined string | `""` |
| `readVarUint8Array` / `readFloat*` | `RangeError` if beyond backing buffer |

Consequence for V2: a V2 update whose `info` column is shorter than the number of structs does not necessarily fail: the info column repeats its last run forever; whereas truncated clients/len/typeRef/clock columns produce `0`s. A C# port that throws on exhausted columns is stricter than JS. This is the single largest source of "JS accepts, strict port rejects" cases in V2.

---

## 9. Numeric range facts a C# port must decide on

- Client ids: `Doc.js:23` `generateNewClientId = random.uint32` (so < 2^32 for generated ids), but the wire accepts any double up to ~2^53. `readVarUint` result is a `number`; JS code does `Map` lookups and `b[0] - a[0]` sorts on it, which is exact up to 2^53-1. Wire values >= 2^53 are accepted by the decoder with silent precision loss (1.2). Recommend `ulong` for client and clock with explicit rejection above 2^53-1, or document the divergence.
- Clocks and lengths: same domain. Sums (`clock + length`) can exceed 2^53 and silently lose precision in JS; a C# checked-arithmetic port will throw instead. Fixture clock = 2^53-1 with length 1 is a good boundary test.
- Any integers: exactly the 32-bit signed range magnitude `<= 2^31-1` goes to VarInt (note: `-2^31` does **not**, it becomes float32 `7c cf 00 00 00`).
- Strings: lengths are UTF-16 code units in the model (clocks), UTF-8 bytes on the wire (prefix). C# `string` is UTF-16, so clock arithmetic matches; the UTF-8 side must use a **non-throwing replacing encoder** for write (JS replaces lone surrogates with U+FFFD) and a **throwing decoder** for read (JS is fatal). .NET's `UTF8Encoding(false, true)` throws on encode; use `new UTF8Encoding(false, false)` to encode, and `new UTF8Encoding(false, true)` to decode (BOM kept: do not use `StreamReader`/`GetString` helpers that strip BOM).

---

## 10. Behaviours that are version-specific traps (checklist for test design)

1. V1 and V2 state vectors are byte-identical; only one decoder exists; sv argument to V2 APIs is V1-format bytes.
2. V2 `ContentEmbed`/`ContentFormat`-value use Any; V1 uses JSON text. V2 `ContentJSON` still uses JSON text (in the string column).
3. V2 keys never reuse a clock (cache branch in decoder is unreachable from this encoder).
4. V2 info/parentInfo RLE omits the final count; other columns flush it.
5. Skip lengths are always raw VarUint; GC lengths are `lenEncoder` in V2.
6. `startClock` and `numStructs` are raw VarUint in V2 (rest buffer), not in columns.
7. Delete set in V2 is delta coded with per-client reset; `len-1` stored.
8. Merge never coalesces Items but coalesces GC/Skip; `encodeStateAsUpdate` reflects live-doc merging. Byte-exact fixtures must record which API produced them.
9. `mergeUpdates` with one argument returns the input instance untouched.
10. Diff updates (sv mid-struct) rewrite the first Item's origin and may contain U+FFFD from surrogate splitting.
11. Encode of a doc with pending structs/ds goes through merge, not the direct writer.
12. `info` bytes with ref 10 plus flag bits: Skip in `applyUpdate` path, Item (then error) in merge/diff/convert path.

---

## 11. Existing oracle material in the submodules (reusable as fixtures)

- Yjs tests: `yjs/tests/updates.tests.js` (merge, diff, key encoding V2, obfuscate), `yjs/tests/encoding.tests.js:26-44` (content ref table: `contentRefs.length === 11`), `:72-100` (state vector from update, skip handling), `yjs/tests/compatibility.tests.js` (base64 V1 documents produced by older Yjs: array, map, text).
- lib0 tests: `lib0/encoding.test.js` (Any generator `genAny` at line 15-39; unknown types `283-296`; RLE/OptRLE round trips `641-765`).
- These are *behavioural* (round trip) tests, not golden byte tests. No golden V1/V2 bytes for entire updates exist in the repo except the base64 blobs in `compatibility.tests.js`. The generator in TASK-1.10 must produce byte goldens.

---

## 12. Appendix: reproducible hex samples (all **[verified]**, Yjs 13.6.33 / lib0 0.2.99)

Docs are built with fixed `clientID`s; each line is `label: hex`. Hex is produced by `Buffer.from(update).toString('hex')`.

```
text "hi" client 1                   V1  010101000401017402686900
                                     V2  0000010100000104060374686901020101000001010000
                                         = flag 00 | keyClock 00 | client 01 01 | left 00 | right 00 | info 01 04 | string 06 [03 74 68 69 = "thi"] [01 02 = lens 1,2] | parentInfo 01 01 | typeRef 00 | len 00 | rest: 01 01 00 00 (numClients, numStructs, startClock, ds count)
empty doc                            V1  0000
                                     V2  00000000000001000000000000
empty state vector                   V1/V2 00
map c1: set k={a:1}, s='v', k=2      V1  010301002101016d016b012801016d017301770176a80100017d020101010001
                                     V2  00000241000100000521002800a807046d6b6d7341020101000241010103007701767d020101010000
                                     sv  010103
array c2: push [1,'x',null]; insert(1,[Uint8Array[9,8]]); push [new Map]
                                     V1  0104020008010161017d01880200027701787ec3020002010209088702020100
                                     V2  00000242030301000401020708008800c3008703016101010101010201020104007d017701787e02090800
text c3: insert 'abc' {bold:true}; insertEmbed(1,{img:'u'}); format(0,1,{bold:null})
                                     V1  010703000101017401840300016184030102626386030304626f6c64046e756c6cc10301030201c5030503020b7b22696d67223a2275227dc60306030204626f6c64047472756501030200010501
                                     V2  0002000202430806000204440802030401000b010084018600c100c500c6120c74616263626f6c64626f6c6441000244000101000241000107007e760103696d677701757801030200000400
two clients (10, 20) 'hello'; c20 deletes (1,2) and inserts 'Z' at 0; merged into c10's doc
                                     V1  02011400440a00015a030a00040101740168810a0002840a02026c6f010a010102
                                     V2  000003144a020200040100074400040081008409055a74686c6f41010201010001020201000300010a010101
                                     sv  0214010a05
incremental updates, client 30, text t: insert(0,'a'), insert(1,'b'), insert(0,'c'), delete(0,1)
   u0  01011e0004010174016100
   u1  01011e01841e00016200
   u2  01011e02441e00016300
   u3  00011e010201                        // no structs; ds: client 30, one range clock 2 len 1
   merge all                01031e00040101740161841e000162441e000163011e010201
   merge [u0,u2] (gap)      01031e000401017401610a01441e00016300
   merge [u2,u0]            01031e000401017401610a01441e00016300      // order independent
   full doc                 01021e0004010174026162411e0001011e010201
   diff sv{30:1}            01021e01841e000162411e0001011e010201
   diff sv{30:99}           00011e010201
   svFromUpdate(merge gap)  011e01
text 'hello' fully deleted, gc on    V1  0101280001010174050128010005
text 'hello' fully deleted, gc off   V1  01012900040101740568656c6c6f0129010005
subdoc guid g1 autoLoad meta {a:1}   V1  010132002901016d01730267317602086175746f4c6f616478046d657461760101617d0100
xml: fragment x, el 'p', attr a='b'  V1  01023c000701017803017028003c0001610177016200
                                     V2  000100027c000100000307002806037870614101030100000103010101020077016200
```
Reading the hex: the first struct header is `numClients numStructs client startClock`, so `28` in the gc-on sample is **client 40** (not an info byte), `29` is client 41, `2a` 42, `32` 50, `3c` 60, `1e` 30. The map sample is decoded byte by byte in 3.6.

### Reproducing
Harness used (not committed): copy `yjs/src` and `lib0` into a scratch dir (as `node_modules/lib0`), import `./src/index.js`. `encodeStateVectorV2` and `writeDeleteSet` must be imported from `utils/encoding.js` / `internals.js` since they are not exported by `index.js`. The scripts probe: lib0 primitives (varint, varuint, varstring, Any table, RLE family), the update scenarios above, merge/diff/convert behaviours, and the malformed-input tables of section 8.
