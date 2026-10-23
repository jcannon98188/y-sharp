# JS quirk register for the y-sharp port

Task: TASK-1.7. Oracle: Yjs v13.6.33 (`yjs/`), lib0 v0.2.99 (`lib0/`), y-protocols (`y-protocols/`).

Scope: JS-specific behaviours that change wire bytes, observable semantics, or test design in a C# port. Every handling entry is a **proposal**, not a decision. Per ADR-0003 the default is "match"; a "diverge" proposal needs its own recorded decision and a tagged test showing the JS behaviour. ADR-0002 (YValue struct) is assumed.

Line numbers refer to the checked-out submodules. Entries marked **[verified]** were also run against the oracle's lib0 under Node v24 (throwaway script in the session scratchpad, not checked in). Bytes shown are lowercase hex.

Layout of each entry: **What** (behaviour), **Source**, **Repro idea** (minimal oracle-driven test), **Proposal**.

Index

| ID | Topic | Wire impact |
|---|---|---|
| Q01 | `writeAny` number tag selection | yes |
| Q02 | Integer range cutoff is 2^31-1, not int32 | yes |
| Q03 | Negative zero | yes |
| Q04 | NaN, Infinity, NaN bit pattern | yes |
| Q05 | BigInt (tag 122) | yes |
| Q06 | `undefined`, functions, symbols | yes |
| Q07 | Values `writeAny` silently flattens (Date, Map, ArrayBuffer, typed arrays) | yes |
| Q08 | Object key order (integer-like keys first) | yes |
| Q09 | `__proto__` and duplicate keys on decode | no (semantic) |
| Q10 | Which JS types the Yjs insert APIs accept | no (API) |
| Q11 | V1 JSON path (embeds, formats, ContentJSON) | yes |
| Q12 | Varint limits (2^53, doubles, no overflow check) | yes |
| Q13 | `-0` in UintOptRle / V2 RLE encoders | yes |
| Q14 | IntDiffOptRle 31-bit claim, doubles | yes |
| Q15 | Lenient decoding of truncated or hostile input | no (robustness) |
| Q16 | UTF-16 indexing in YText / ContentString | yes |
| Q17 | Surrogate splitting becomes U+FFFD | yes |
| Q18 | Lone surrogates and the UTF-8 encoder/decoder | yes |
| Q19 | StringEncoder (V2) concatenation | yes |
| Q20 | Client IDs: crypto uint32, not Math.random | yes |
| Q21 | Client ID arithmetic and sorting | yes |
| Q22 | Clocks are doubles | yes |
| Q23 | Observer and event ordering | no (semantic) |
| Q24 | Error aggregation in `callAll` | no (semantic) |
| Q25 | Stable `Array.prototype.sort` | yes |
| Q26 | Map iteration order, `for...in` order | yes |
| Q27 | Attribute equality in YText (`equalFlat`, NaN) | yes |
| Q28 | Number to string conversion (XML attrs, JSON) | yes |
| Q29 | Locale and `toLocaleLowerCase` | no |
| Q30 | `JSON.stringify` / `JSON.parse` fidelity (awareness, V1) | yes |
| Q31 | Buffer aliasing (views, not copies) | no (semantic) |
| Q32 | Time and timers | no |

---

## Number and value encoding (lib0 `writeAny` / `readAny`)

### Q01 Number tag selection is value-driven, not type-driven

**What.** A JS `number` has no int/float distinction. `writeAny` picks the tag from the value: if `Number.isInteger(n)` and `|n| <= 0x7FFFFFFF` then tag 125 (`writeVarInt`); else if the value survives a round trip through float32 then tag 124; else tag 123 (float64). The decoder returns a plain `number` for all three, so the original tag is lost on decode. Re-encoding therefore canonicalises: a float64-tagged `1.0` from a foreign encoder comes back out as tag 125 (`7d01`). This matters wherever Yjs decodes and re-encodes content (update merging, diff, `encodeStateAsUpdate` after apply).

**Source.** `lib0/encoding.js` `writeAny` (l.536 onward), `isFloat32` (l.494); `lib0/decoding.js` `readAnyLookupTable` (l.461), `readAny` (l.497). `yjs/src/structs/ContentAny.js` `write` / `readContentAny`.

**Repro idea.** Table test over `[0, 1, 1.5, 1.1, 16777217, 2**31, 2**31+1, 2**53, -2**31, 1e21, 0.1]`, capture `writeAny` bytes. **[verified]** samples: `1.5 -> 7c3fc00000`, `1.1 -> 7b3ff199999999999a`, `16777217 -> 7d81808010` (an integer within the int range is varint, not float), `2**31 -> 7c4f000000` (float32), `2**31+1 -> 7b41e0000000200000` (float64), `2**53 -> 7c5a000000` (float32, exactly representable). Also feed foreign-encoded input (float64 `1.0`, float32 `5.0`) through decode then re-encode and assert the canonical tag.

**Proposal (match).** `YValue` stores a `double` for the number case and chooses the tag exactly as above (this is ADR-0002's premise). Do not remember the wire tag on decode. Implement `IsFloat32` as `(double)(float)x == x` and treat NaN as "not float32" (NaN != NaN falls out of the comparison). Add the foreign-tag re-encode test as the canonicalisation guard.

### Q02 The integer cutoff is 2^31-1, so int32 minimum is a float32

**What.** The guard is `Math.abs(data) <= BITS31` (0x7FFFFFFF). `-2147483648` has abs 2^31, so it fails the guard and is written as float32 (`7ccf000000`), not as a varint. The comment table says "32 bit signed integers" but the code is effectively "|n| < 2^31". Integers between 2^31 and 2^53 go to float32 only when exactly representable, else float64.

**Source.** `lib0/encoding.js` `writeAny`, the `case 'number'` branch. **[verified]** `-2^31 -> 7ccf000000`, `2^31-1 -> 7dbfffffff0f`.

**Repro idea.** Boundary table: `2^31-1`, `-(2^31-1)`, `-2^31`, `2^31`, `-2^31-1`.

**Proposal (match).** Use `Math.Abs(x) <= int.MaxValue` on the double, not a `long`/`int` range check. A `(int)x` cast check would wrongly put `int.MinValue` on the varint path.

### Q03 Negative zero

**What.** `-0` is an integer (`Number.isInteger(-0)` is true), so `writeAny(-0)` takes tag 125. `writeVarInt` detects the sign with `isNegativeZero` (`n !== 0 ? n < 0 : 1/n < 0`) and sets the sign bit with zero magnitude: `7d40`. `readVarInt` returns `sign * 0 = -0`, so `-0` round-trips. A C# `long`/`int` cannot represent it. Positive `0` is `7d00`.

**Source.** `lib0/encoding.js` `writeVarInt` (l.277), `lib0/math.js` l.58, `lib0/decoding.js` `readVarInt` (l.273). **[verified]** `-0 -> 7d40`, and `readAny(7d40)` yields `-0`.

**Repro idea.** `writeAny(-0)`, `writeAny(0)`; decode `7d40` and assert `Object.is(v, -0)`.

**Proposal (match).** Decode `7d40` to `double -0.0`, and encode `-0.0` as `7d40` (check `double.IsNegative`/`BitConverter.DoubleToInt64Bits < 0` before using integer paths). Public API consequence for TASK-1.9: `YValue` equality must distinguish `+0` and `-0` at the wire level even though `==` does not. See also Q13 where the same trick is load-bearing for V2 RLE.

### Q04 NaN, Infinity and NaN bit patterns

**What.** `NaN` is not an integer and `isFloat32(NaN)` is false (NaN !== NaN), so it is float64: `7b7ff8000000000000` **[verified]**. `Infinity` is float32 `7c7f800000` **[verified]**. The NaN payload is whatever `DataView.setFloat64` canonicalises to in V8 (observed `7ff8000000000000`, positive quiet NaN). In .NET `double.NaN` has bits `0xFFF8000000000000` (sign bit set on x86/x64), so a naive `BinaryPrimitives.WriteDoubleBigEndian(double.NaN)` would emit `fff8...`, a different byte string. Decoders accept any NaN payload, so only encode is affected.

**Source.** `lib0/encoding.js` `isFloat32`, `writeFloat64` (l.473). The V8 canonical NaN is engine behaviour, so it is flagged for oracle confirmation on the target Node version rather than derivable from the source.

**Repro idea.** Encode NaN, +Inf, -Inf, and a decoded non-canonical NaN (`7bfff8000000000001`) in the oracle; assert bytes.

**Proposal (match).** Normalise any NaN to bits `0x7FF8000000000000` before writing. Treat decode of non-canonical NaN as accepted; re-encode canonicalises (consistent with Q01).

### Q05 BigInt (tag 122)

**What.** `bigint` is written with `setBigInt64`, which wraps modulo 2^64 for out-of-range values (no error). Decoding returns a JS `bigint`. A `bigint` is never produced from tag 125/124/123, so a JS number and a JS bigint of equal value are distinct on the wire. Note an API asymmetry: `YMap.set` accepts `BigInt` (and `Date`) as a value, but `YArray.insert` / `YText.insertEmbed` paths do not (Q10); BigInt can still appear inside arrays/objects nested in a map value.

**Source.** `lib0/encoding.js` `writeBigInt64` (l.479), `writeAny` `case 'bigint'`; `lib0/decoding.js` `readBigInt64` (l.451); `yjs/src/types/AbstractType.js` `typeMapSet` l.846 (`case BigInt`).

**Repro idea.** `map.set('k', 2n**63n)` and `2n**64n+1n` (wrap), compare bytes; decode a tag 122 and confirm a bigint is returned.

**Proposal (match).** `YValue` has a distinct `Int64` case (signed 64-bit). Wrapping for out-of-range input is a JS artefact; **diverge** by throwing on values outside int64 in the public constructor (a C# `BigInteger` source cannot silently wrap). Record as a Divergence if adopted. Decoded tag 122 always yields the `Int64` case, never `Double`.

### Q06 `undefined`, functions, symbols

**What.** Tag 127 is `undefined`; anything with an unrecognised `typeof` (function, symbol, undefined) falls into `default` and is written as 127. In objects the key is kept with value `undefined`; in arrays the slot is kept. Decoded `undefined` values are real members (so `Object.keys` includes the key). `null` is tag 126. At the Yjs level `YMap.set(k, undefined)` stores `ContentAny([undefined])`; `get` then returns `undefined`, indistinguishable from a missing key even though the entry is live in `keys()`/`has()`.

**Source.** `lib0/encoding.js` `writeAny` `default` branch; `lib0/decoding.js` table slot 127; `yjs/src/types/AbstractType.js` `typeMapSet` (`value == null`), `typeMapGet` l.~892.

**Repro idea.** `map.set('u', undefined)`; assert `map.has('u')`, `map.get('u') === undefined`, `map.size`, `toJSON()` contains key with `undefined`, update bytes contain `7f`.

**Proposal (match).** `YValue` has an `Undefined` case separate from `Null`. C# functions/symbols have no analogue, so there is nothing to coerce. `YMap.Get` should return a result distinguishing "absent" from "present-undefined" (an explicit proposal for TASK-1.14; JS cannot distinguish them via `get` alone).

### Q07 Values that `writeAny` flattens silently

**What.** `writeAny` only recognises string, number, bigint, null, array, `Uint8Array`, boolean; every other `object` goes to the plain-object branch using `Object.keys`. Consequences **[verified]**: `Date` -> empty object `7600`; `Map` -> `7600`; `ArrayBuffer` -> `7600` (not binary!); `Float32Array([...])` -> an object keyed `"0"`, `"1"` (`760201307d0001317d00`). `Date` is therefore accepted by `YMap.set` but arrives at the peer as `{}`. A nested `ArrayBuffer` or non-`Uint8Array` typed array has the same fate. Only a top-level `ArrayBuffer`/`Uint8Array` passed to `YArray.insert`/`YMap.set` goes to `ContentBinary` (Q10).

**Source.** `lib0/encoding.js` `writeAny` `case 'object'` branches; `yjs/src/types/AbstractType.js` l.~660-680 and `typeMapSet` l.854-870.

**Repro idea.** `map.set('d', new Date(0))`, `map.set('o', {d: new ArrayBuffer(2)})`, decode in a second doc, assert `{}`.

**Proposal (diverge, narrow).** Do not model Date/Map/ArrayBuffer in `YValue` at all; the public surface only offers the eleven Any cases (ADR-0002). Wire decode of `7600` yields an empty object as in JS. A fixture that exercises the flattening is useful only to document that C# deliberately has no way to produce it.

### Q08 Object key order on encode

**What.** `writeAny` iterates `Object.keys(data)`, which for plain objects yields array-index keys (canonical non-negative integer strings below 2^32-1) in ascending numeric order first, then other string keys in insertion order. **[verified]** an object built as `{b, 2, a, 1}` encodes keys in order `1, 2, b, a`. Consequence: a decode then re-encode through Yjs re-sorts integer-like keys, and the same logical object yields different bytes from an implementation that preserves insertion order. Yjs's own `YMap.toJSON`, `getAttributes()` and `YXmlElement.toString` use plain objects and inherit this order. By contrast `YMap._map` itself is a `Map` and keeps pure insertion order (Q26).

**Source.** `lib0/encoding.js` `writeAny` object branch (`Object.keys`); `lib0/object.js` `forEach`/`map` use `for...in` (same order); `yjs/src/types/YMap.js` `toJSON` (l.~125).

**Repro idea.** Wire-level: `map.set('o', {b:1, 2:1, a:1, 1:1})`, assert key order `1,2,b,a`. Cross-impl: feed the C# decoder bytes with order `b,2,a,1`, re-encode, and compare with the oracle's re-encode of the same bytes.

**Proposal (match).** `YValue` object case: store keys in insertion order, but sort on encode using the JS rule: keys that are canonical decimal array indices (no leading zeros except "0", value <= 4294967294) first ascending numerically, then the rest in insertion order. Specify the array-index test precisely in the fixture spec (TASK-1.10). Also determines `toJSON` output ordering for the public API.

### Q09 `__proto__` and duplicate keys on decode

**What.** `readAny` builds objects with `obj[key] = value`. A wire key `"__proto__"` therefore sets the prototype (when the value is an object) instead of creating an own property **[verified]**: decoded keys `[]`, and inherited properties appear on the result. A foreign encoder could send this; a C# dictionary would keep it as an ordinary key. Duplicate keys: the last value wins and the position stays at the first occurrence **[verified]** (`{"a":1,"b":2}` from keys a, b, with duplicates behaving analogously).

**Source.** `lib0/decoding.js` l.~476-482.

**Repro idea.** Hand-built tag 118 payloads with `__proto__` and duplicate keys; compare oracle decode+re-encode with C#.

**Proposal (diverge).** Treat `__proto__` as an ordinary key (prototype pollution has no meaning in C#). This only matters for adversarial interop fixtures; record as a Divergence with a tagged test. For duplicates, match (last wins, first position).

### Q10 Which JS types Yjs accepts for insertion

**What.** Dispatch is on `value.constructor`, not `typeof`. `YArray.insert`/`typeListInsertGenericsAfter` accepts `Number, Object, Boolean, Array, String` plus `null` into ContentAny, `Uint8Array`/`ArrayBuffer` into ContentBinary, `Doc`, and `AbstractType` subclasses; anything else throws `Unexpected content type in insert operation`. This means: `undefined` throws a `TypeError` (no `.constructor`), `bigint`, `Date` throw in arrays, `Object.create(null)` throws, class instances throw. `typeMapSet` (YMap) is looser: it is `value == null` (so `undefined` is allowed) and additionally allows `Date` and `BigInt`. Boxed `new Number(1)` passes (constructor Number) and is then encoded by `typeof` = `object` as `{}`.

**Source.** `yjs/src/types/AbstractType.js` l.645-695 (`typeListInsertGenericsAfter`), l.846-875 (`typeMapSet`); `yjs/src/types/YText.js` `insertText` (l.265: String -> ContentString, AbstractType -> ContentType, else ContentEmbed).

**Repro idea.** Matrix of values x (YArray.insert, YMap.set, YText.insertEmbed) recording success/throw.

**Proposal (diverge, by construction).** The C# API is statically typed via `YValue`; the "throws on unsupported type" behaviour disappears. Keep the matrix as documentation only. One behaviour to keep consciously: YArray cannot hold `undefined` or `Int64`, but YMap can. Decide in TASK-1.14 whether to mirror that restriction (proposal: **match**, to keep fixtures portable, i.e. `YArray.Insert` rejects `Undefined`/`Int64`, `YMap.Set` allows them).

### Q11 V1 uses `JSON.stringify` for embeds/formats and legacy ContentJSON

**What.** In V1, `writeJSON` is `writeVarString(JSON.stringify(embed))`; in V2 it is `writeAny`. `ContentEmbed` and `ContentFormat` values go through it, as does legacy `ContentJSON` (per element, with the literal string `"undefined"` for `undefined`). JSON semantics then apply: `NaN`/`Infinity` become `null`, `-0` becomes `0`, `undefined` as a top-level embed makes `JSON.stringify` return `undefined` (then `writeVarString` throws), `Date` becomes an ISO string, `BigInt` throws `TypeError`, object key order follows Q08, and number text uses JS `Number#toString` (Q28). Reading uses `JSON.parse`, which makes integers beyond 2^53 lossy and turns `1e0` into `1`. V1 and V2 therefore round-trip different value sets for the same format/embed.

**Source.** `yjs/src/utils/UpdateEncoder.js` l.115 (V1), l.285 (V2); `yjs/src/utils/UpdateDecoder.js` l.113 (V1), l.264 (V2); `yjs/src/structs/ContentEmbed.js` l.79-97, `ContentFormat.js` l.87-104, `ContentJSON.js` `write`/`readContentJSON`.

**Repro idea.** For each of `NaN, -0, Infinity, undefined, 1e21, 0.1+0.2, 2**53+2, {b:1,2:1}, "\ud800"` do `insertEmbed` and `format`, encode with V1 and V2, compare bytes and the decoded value.

**Proposal (match).** Embeds/format values are `YValue`. V1 path: implement a JS-compatible JSON serialiser/parser (not `System.Text.Json` defaults): shortest round-trip number formatting with JS exponent rules, `NaN/±Inf -> null`, `-0 -> 0`, key order Q08, JS string escaping (see Q30). Throw for `Undefined`/`Int64` at top level in V1 (JS throws or errors too).

---

## lib0 varint and RLE encoders

### Q12 Varint limits and number representation

**What.** `writeVarUint` is documented as "max encodable integer is 2^53" and works on doubles: `num = floor(num/128)` loops, so any value up to 2^53 is exact. Values above 2^53 lose precision silently **[verified]** (`2^53+2 -> 8280808080808010`, one extra continuation group). The decoder accumulates in a double with `mult *= 128`, and its overflow check (`num > MAX_SAFE_INTEGER`) only runs after a continuation byte, so a final byte can push the value past 2^53 without an error, and the result is a rounded double. `writeVarInt` carries 6 bits in the first byte plus sign (bit 7) and continuation (bit 8). It is sign-magnitude, not zigzag, hence Q03. Maximum useful length is 8-9 bytes for uint53. Non-integer or negative inputs to `writeVarUint` are not validated and produce garbage (negative `num > 127` is false, so `write(BITS7 & num)` emits low bits only).

**Source.** `lib0/encoding.js` l.260-290; `lib0/decoding.js` l.241-300 (`readVarUint`, `readVarInt`); `lib0/number.js` `MAX_SAFE_INTEGER`.

**Repro idea.** Boundary table for `readVarUint`: `2^32-1, 2^32, 2^53-1, 2^53, 2^53+1`; hand-built overlong sequences (`80 80 ... 00`, redundant zero groups) and a 10-byte sequence; confirm accept/throw and decoded value. Overlong encodings are accepted (`80 00` decodes to 0).

**Proposal (diverge, narrow).** Use `ulong`/`long` internally. Match: accept overlong encodings, produce identical bytes for all values <= 2^53-1. Diverge: reject decoded values > 2^53-1 with a typed error (JS silently rounds and, for client IDs/clocks, would corrupt state; C# `ulong` could accept up to 2^64 and diverge differently). Record the 2^53 bound as the public limit for client, clock, length. Any wire field that is semantically `uint32` in other implementations (client IDs) must still be accepted up to 2^53.

### Q13 `-0` is load-bearing in `UintOptRleEncoder` (V2 only)

**What.** `UintOptRleEncoder` flags "a count follows" by writing the value negated via `writeVarInt(-s)`. For `s = 0` that is `-0`, which `writeVarInt` encodes with the sign bit and zero magnitude. Writing `[0, 0, 5]` gives `400005` **[verified]**: `40` (= -0), `00` (count-2), `05`. The decoder detects the flag with `isNegativeZero`. Affects the V2 client, typeRef, len and string-length streams. A C# port using `long` negation cannot tell "single 0" from "run of 0".

**Source.** `lib0/encoding.js` `flushUintOptRleEncoder` (l.722), `UintOptRleEncoder` (l.737); `lib0/decoding.js` `UintOptRleDecoder` (~l.570); `yjs/src/utils/UpdateEncoder.js` l.125 onwards.

**Repro idea.** Encode client/length streams containing runs of 0 (e.g. YText items whose first-struct length stream is `[0,0,...]` via ContentDeleted of length... practically `typeRef` stream of 0 = YArray repeated). Directly test `UintOptRleEncoder` with `[0]`, `[0,0]`, `[0,0,0]`, `[1,1]`, `[0,0,1]`.

**Proposal (match).** Implement the sign-magnitude varint with an explicit `isNegative` flag separate from the magnitude (a `readVarInt` returning `(sign, magnitude)` internally). Apply the same for `IncUintOptRleDecoder`.

### Q14 IntDiffOptRle: "31-bit" arithmetic is on doubles

**What.** `IntDiffOptRleEncoder` computes `diff * 2 + flag` in double arithmetic; the doc comment says only 31-bit integers are supported but nothing enforces it. Clock diffs larger than 2^30 still encode (via a longer varint) and decode with `floor(diff/2)` and `diff & 1`, but `diff & 1` uses ToInt32, which is only correct below 2^31 (and for negatives relies on two's complement). Larger diffs would decode wrongly. Parity: for negative odd values `diff & 1` is 1, and `floor(diff/2)` rounds toward -infinity, which is consistent for the encode side `diff*2+flag`.

**Source.** `lib0/encoding.js` `flushIntDiffOptRleEncoder` (l.821), `lib0/decoding.js` `IntDiffOptRleDecoder`.

**Repro idea.** Clock jumps (V2 `leftClockEncoder`) with diffs `+-1, +-2^30, +-2^31, +-2^32`: encode and decode in the oracle and see where round-trip breaks.

**Proposal (match up to the safe range, diverge beyond).** Use `long` and arithmetic shifts; replicate JS output for diffs within int32. For |diff| >= 2^31 where JS is corrupt, fail loudly (Divergence); note that a doc with a client clock >= 2^31 is unrealistic but a hostile fixture could contain it.

### Q15 Lenient decoding of truncated or hostile input

**What.** Several readers do not bounds-check. **[verified]**: `readUint8` past the end returns `undefined` (then bitwise ops coerce it to 0); `readVarInt` on empty input returns `0` and advances `pos` past the end; `readUint8Array` builds a view on the underlying `ArrayBuffer`, so it throws `RangeError` only when exceeding the buffer, not the view: reading 5 bytes from a `subarray(0, 2)` view returned 5 bytes of neighbouring data. `readVarUint` does throw at end of array. `readAny` with a tag outside 116-127 throws a `TypeError` (not a lib0 error). In RLE decoders, running out of data means "repeat the last value forever" (`hasContent` false -> count = -1). Yjs applies updates in a `try`/pending model: malformed updates may partially apply.

**Source.** `lib0/decoding.js` `readUint8` (l.142), `readUint8Array` (~l.108), `readVarInt`, `RleDecoder.read`, `readAny`.

**Repro idea.** Truncate every valid fixture update at each byte offset and record per-offset outcome in the oracle (throw type, partial doc state). Use as negative-test oracle.

**Proposal (diverge, narrow).** Never read beyond the span; throw a typed `MalformedUpdateException`. Match only the "RLE repeats forever when stream exhausted" behaviour, because valid foreign encoders rely on omitted trailing counts. Do not try to mirror garbage-in garbage-out; the fixtures should assert "oracle throws, y-sharp throws" and "oracle partially applies" only where the partial result is itself well defined (pending structs retained, see `readUpdate` pending/missing logic).

---

## Strings and UTF-16

### Q16 YText indexing and lengths are UTF-16 code units

**What.** `ContentString.getLength()` is `str.length` (UTF-16 code units); `YText.insert/delete/format/length`, relative positions, delta `retain/delete` counts and the `clock` space all count code units. `getContent()` is `str.split('')`, so each element is a single code unit, which can be half an emoji. An emoji (surrogate pair) takes 2 clock ticks, a CJK BMP character 1. Wire strings are UTF-8 (`writeVarString` writes the UTF-8 byte length), so wire lengths in bytes and CRDT lengths in code units differ (important: `lenEncoder` and V1 `ContentString` store the string, V2 `StringEncoder` stores UTF-16 lengths, Q19).

**Source.** `yjs/src/structs/ContentString.js` l.19-30, 91-93; `yjs/src/types/YText.js` position logic (`findPosition`, `insertText`, `deleteText`); `lib0/encoding.js` `writeVarString`.

**Repro idea.** Insert `"a\u{1F600}b"`; assert `text.length === 4`, delete(1, 1) behaviour (Q17), clock advance, `encodeStateAsUpdate` bytes. Also combining marks and ZWJ sequences to show no grapheme logic.

**Proposal (match).** C# `string` is UTF-16 as well, so use `string.Length`/`Substring` directly and never `StringInfo`/`Rune` counts for CRDT indices. Test with surrogate pairs, combining marks, ZWJ emoji. Make sure any span/rune based optimisation preserves code-unit indices.

### Q17 Splitting a surrogate pair inserts U+FFFD on both sides

**What.** `ContentString.splice(offset)` slices at a code-unit offset. If the left piece ends with a high surrogate, both halves are corrupted on purpose: left ends with `�` and right starts with `�`. This happens when a remote or local operation lands between the two halves of an emoji (delete/insert/format at an odd index). The replacement is a single code unit on each side so lengths stay consistent, but the original character data is destroyed (and the wire bytes are 3-byte `efbfbd` sequences for each half).

**Source.** `yjs/src/structs/ContentString.js` `splice` l.51-67 (references yjs issue #248). Note the check uses `charCodeAt(offset - 1)` on the already truncated string.

**Repro idea.** `ytext.insert(0, '\u{1F600}')`, then `insert(1, 'x')`; read `toString()` and the update bytes. Also remote: apply an update that references clock+1 inside the emoji.

**Proposal (match).** Port `splice` literally, including the U+FFFD replacement on both sides. Tagged test `quirk:surrogate-split`. This is the kind of behaviour where the fixture generator must include odd-index operations on astral characters, otherwise the divergence never shows.

### Q18 Lone surrogates and UTF-8 conversion

**What.** `TextEncoder.encodeInto`/`encode` convert lone surrogates to U+FFFD (`efbfbd`) **[verified]**: `"a\ud800b"` -> `0561efbfbd62` (byte length 5, three bytes for the replacement). Within Yjs the in-memory string still holds the lone surrogate until it is read back from the wire, where it appears as `�`. So a local doc and a peer disagree on the character while agreeing on length (both 1 unit). The decoder is `TextDecoder('utf-8', {fatal: true, ignoreBOM: true})`: invalid UTF-8 on the wire throws; a leading BOM bytes (`efbbbf`) are kept as U+FEFF rather than stripped. Other strings paths: `_writeVarStringNative` uses a 30000-byte scratch buffer when `str.length < 10000`, otherwise the allocating path; output is the same.

**Source.** `lib0/string.js` l.46-123 (`utf8TextEncoder`, `utf8TextDecoder` with `fatal: true`), `lib0/encoding.js` l.324-345.

**Repro idea.** Strings: lone high, lone low, reversed pair, BOM prefix, `U+0000`, 10000-unit and 30000-unit strings (buffer-size boundaries), invalid UTF-8 bytes on decode.

**Proposal (match, with one pin).** C# `Encoding.UTF8` also replaces lone surrogates by `EF BF BD` with `UTF8Encoding(false, false)` but a throwing decoder needs `new UTF8Encoding(false, true)`; construct both explicitly and never rely on defaults. Ensure the decoder neither strips BOM nor accepts overlongs/surrogates-in-UTF-8 (CESU), matching WHATWG `fatal` behaviour (.NET's strict decoder rejects the same set; confirm with a byte-pattern table).

### Q19 V2 `StringEncoder` concatenates and relies on code-unit lengths

**What.** The V2 string stream stores all strings as one concatenated UTF-8 string plus a `UintOptRle` stream of **UTF-16 lengths**. The decoder slices the decoded JS string by code units. Consequences: (a) a lone surrogate in an earlier string pairs up with a following string's low surrogate during concatenation and encoding, silently altering both lengths' meaning; (b) lone surrogates become U+FFFD one-for-one, so lengths survive; (c) an astral char split across two items (Q17 prevents local creation, but a foreign encoder can emit it) would re-pair. Strings of length 0 are legal (length stream carries 0, see Q13).

**Source.** `lib0/encoding.js` `StringEncoder` (~l.940), `lib0/decoding.js` `StringDecoder`.

**Repro idea.** V2-encode docs with consecutive items of `"\ud83d"` and `"\ude00"` (constructed via remote ops), empty-string embeds, and key strings.

**Proposal (match).** Implement `StringEncoder` over UTF-16 lengths exactly. Slice the decoded string by code units. No extra validation; fixtures decide.

---

## Client IDs, clocks and ordering

### Q20 Client IDs come from `crypto.getRandomValues`, not `Math.random`

**What.** The brief anticipated `Math.random` client IDs. In this oracle `generateNewClientId = random.uint32`, which is `getRandomValues(new Uint32Array(1))[0]` (via `lib0/webcrypto`), so IDs are uniform in `[0, 2^32-1]`. `Math.random` is only re-exported as `random.rand`; the doc comment in `random.js` saying it falls back to `Math.random` is stale. `uuidv4()` (default `Doc.guid`) also uses `uint32`. A client that observes another client writing with its own ID after a remote transaction picks a new ID (`doc.clientID = generateNewClientId()`), with a console message. `uint53` also exists but Yjs does not use it for the doc client ID. Foreign implementations (e.g. Rust/Python ports) may use 53-bit or 64-bit-truncated IDs; JS decodes IDs up to 2^53 exactly (Q12).

**Source.** `yjs/src/utils/Doc.js` l.23, 64; `lib0/random.js` l.10-37; `yjs/src/utils/Transaction.js` l.360-363 (collision reaction).

**Repro idea.** (a) Assert `clientID` is within uint32 over many docs; (b) `new Doc()` then set `clientID` via a test hook, create the collision scenario (apply a remote update that has the local clientID and a different afterState) and assert the ID is regenerated.

**Proposal (match behaviour, diverge source).** Use `RandomNumberGenerator.GetBytes` -> `uint`. For determinism, allow injection (`IClientIdGenerator`) so tests and fixtures can pin IDs; the fixtures' IDs must be fixed explicitly in the generator (`doc.clientID = n`). Keep the collision-regeneration behaviour. Doc.guid generation: `Guid`-style v4 formatting must match the JS template (lower-case hex, layout `10000000-1000-4000-8000-100000000000`) only if tests compare guids; otherwise unobservable.

### Q21 Client ID arithmetic and comparisons assume doubles

**What.** Client IDs are JS numbers in `[0, 2^32)` and are compared/sorted with `b[0] - a[0]` (descending) and `a - b` (ascending) in several places: state-vector and struct-set encoding order (higher client first), delete-set order (higher client first), pending-update processing (`clientsStructRefsIds` ascending, with a stack that processes the head), `mergeUpdates` ordering, and conflict resolution in `Item.integrate` (lower client ID wins ties). With `uint` or `int` in C#, `b - a` wraps or overflows. IDs above 2^31 would become negative as `int`.

**Source.** `yjs/src/utils/encoding.js` l.99, 233, 603; `yjs/src/utils/DeleteSet.js` l.227; `yjs/src/utils/updates.js` l.357-370; `yjs/src/structs/Item.js` `integrate`.

**Repro idea.** Doc with clients `1`, `2^31`, `2^32-1` and one IDs above 2^32 injected from the wire; assert write order and conflict winners.

**Proposal (match).** Client ID type is `uint` for generated IDs but decode into `ulong` (or `long`) to accept up to 2^53-1 (Q12), and compare with `CompareTo`, never subtraction. Sort descending for encode paths exactly as listed.

### Q22 Clocks and lengths are doubles

**What.** `clock` and `length` fields are JS numbers. Arithmetic like `clock + length`, pivoted binary search (`math.floor((clock / (midclock + mid.length - 1)) * right)`), and diff encoders operate in floating point, exact below 2^53. `findIndexSS` uses a floating-point pivot so its probe sequence is not a plain binary search; results are the same but a port that "simplifies" it should not change observable behaviour (only performance, relevant for TASK-1.15 hot paths). Division by zero paths: `midclock + mid.length - 1` can be 0 giving `NaN`/`Infinity` pivots that JS then clamps by `floor` and index arithmetic (JS `structs[NaN]` is `undefined` and would throw); the code guards via the first `midclock === clock` check. A C# port converting to `int` indices must replicate the guard, not rely on JS coercion.

**Source.** `yjs/src/utils/StructStore.js` l.111-145.

**Repro idea.** Property test: random struct arrays, assert `findIndexSS` equals a plain binary search; edge cases with first struct at clock 0, length 1.

**Proposal (diverge internally, match observably).** Use `long` clocks/lengths; implement `FindIndexSS` as a plain binary search if desired (internal), asserting equivalence by property test.

### Q23 Observer and event ordering

**What.** After a transaction, `cleanupTransactions` runs in this order: sort/merge delete set, compute `afterState`, `beforeObserverCalls`; then a list of callbacks `fs`: (1) for each entry of `transaction.changed` (a `Map`, so in order of first change), if the type is alive call `_callObserver`, which adds the event to `changedParentTypes` for the type **and every ancestor**, then calls the type's own `_eH` listeners; (2) one callback that, for each entry of `changedParentTypes`, queues a deep-observer callback (only if that type has `observeDeep` listeners and is alive), which filters out events whose target is deleted, sets `currentTarget`, resets `_path`, **sorts events by path length (stable)**, and calls the `_dEH` listeners with the whole event array; then `afterTransaction`, then YText formatting cleanup (which may run its own nested transaction). Afterwards (in `finally`): GC, merge, client-id collision check, `afterTransactionCleanup`, `update` (V1) then `updateV2` events (only built if observers are registered), subdocs events, `afterAllTransactions`. Nested `transact` calls from inside observers are queued in `_transactionCleanups` and processed after the current cleanup, not interleaved. Listener lists: `Observable.emit` copies the listener array first (listeners added during emit are not called); `EventHandler` listeners are called in registration order by `callAll`. `Observable` uses a `Map<string, Set>`, so adding the same function twice is a no-op and re-adding does not change position.

**Source.** `yjs/src/utils/Transaction.js` l.260-400; `yjs/src/types/AbstractType.js` `callTypeObservers` l.243-255; `yjs/src/utils/EventHandler.js` l.86; `lib0/observable.js` `emit` (l.80, l.152); `lib0/function.js` `callAll`.

**Repro idea.** A doc with nested types (map -> array -> text), observers and deep observers at each level plus doc-level `update`, `afterTransaction`; a transaction touching several types in non-structural order; record the global call log. Add a nested-transact-in-observer case and an observer that registers another observer.

**Proposal (match).** Specify the order as a table in the behaviour inventory and port `cleanupTransactions` structurally (list of closures run by `CallAll`). Keep `changed` as an insertion-ordered dictionary (`Dictionary` in .NET Core happens to preserve insertion order only without removals; use an explicit ordered structure to be safe, Q26). C# events (`event EventHandler`) have their own ordering/exception semantics; expose Yjs-style `Observe(callback)` with explicit list and copy-before-emit semantics rather than raw multicast delegates. The deep-observer path sorting must be stable (Q25).

### Q24 Error aggregation: which exception surfaces

**What.** `callAll` calls every function even if one throws, by recursing in a `finally`. If several functions throw, the exception from the **last** failing call wins (a throw in `finally` replaces the pending one), and earlier errors are lost. The transaction `finally` block still performs GC/merge/update emission even if an observer threw, and `afterAllTransactions` fires. Also, an exception thrown in an observer does not abort the transaction: the document state is already mutated.

**Source.** `lib0/function.js` l.16-26; `yjs/src/utils/Transaction.js` l.323-400.

**Repro idea.** Two observers that both throw distinct errors; assert which error escapes `transact`, and that both observers ran, and that an `update` event still fires.

**Proposal (diverge deliberately, narrow).** Match "all observers run, cleanup always completes, doc state stays mutated". Diverge on which error surfaces: throw an `AggregateException` containing all errors in order (strictly more informative; tagged test showing JS surfaces only the last). Record as a Divergence.

### Q25 `Array.prototype.sort` is stable; `List<T>.Sort` is not

**What.** ES2019 requires stable sorting. Yjs relies on it implicitly: events sorted by `path.length` keep transaction order within equal lengths; `DeleteSet` sorted by `clock`; the `mergeUpdates` decoder ordering (comparison returns 0 for ties). .NET `List<T>.Sort` / `Array.Sort` is introsort and unstable.

**Source.** `yjs/src/utils/Transaction.js` l.308; `yjs/src/utils/DeleteSet.js` l.118, 227; `yjs/src/utils/updates.js` l.357.

**Repro idea.** Deep observer with several events at equal depths; compare order with the oracle for 10+ equal-length paths (unstable sort tends to reorder only on larger arrays, so use at least ~20 elements).

**Proposal (match).** Use `OrderBy` (stable) or an index-tiebreak comparer everywhere Yjs sorts. Add a lint/test: no direct `Sort(` on lists whose ties are observable.

### Q26 Map iteration and `for...in` order

**What.** `Map` iterates in insertion order and survives deletes/re-inserts (re-insert moves to end). Yjs relies on this: `transaction.changed`, `changedParentTypes`, `doc.share`, `YMap._map` iteration (so `YMap.keys()/entries()/forEach` order is first insertion order per key), `ds.clients` iteration, state vector `Map`s. `for...in` / `Object.keys` over plain objects follow the integer-first rule (Q08). `findRootTypeKey` and state-vector writing depend on those iteration orders in some paths (the explicit descending-client sorts neutralise most of it).

**Source.** See Q23; `yjs/src/types/YMap.js` l.130-190; `yjs/src/utils/StructStore.js` `getStateVector` l.37-44.

**Repro idea.** YMap: set `b`, `a`, delete `b`, set `b` again; compare `keys()` order. `doc.share` with types created in different order; `transaction.changed` order for multi-type transactions.

**Proposal (match).** Do not use `Dictionary<K,V>` iteration order as the contract: .NET does not guarantee insertion order once removals occur (freed slots are reused). Introduce a small `OrderedMap` (dictionary + linked list or list + tombstones) used wherever Yjs depends on `Map` order; `YMap.keys()` returns first-insertion order, with re-insertion after delete moving to the end.

### Q27 Attribute equality in YText

**What.** `equalAttrs(a, b)` is `a === b` or both non-null objects and `object.equalFlat(a, b)`. `equalFlat` compares own keys one level deep with `===`. So: `NaN` never equals `NaN` (a NaN-valued format is always "changed"); nested objects/arrays compare by **reference** (two structurally equal arrays are unequal, unless the same instance, and note arrays are compared as objects by index keys with `===`); `null` vs `undefined` differ (`attributes[key] ?? null` is used in places to coerce). This decides whether `format` produces new `ContentFormat` items or is a no-op, and thus wire bytes. After decode, equal-looking attribute objects are always distinct instances (so remote formatting is treated as always different).

**Source.** `yjs/src/types/YText.js` l.42 and uses at l.156, 205, 232, 312, 774-800; `lib0/object.js` `equalFlat` l.115.

**Repro idea.** `text.format(0, 3, {link: {href:'x'}})` twice with distinct but equal objects; same with arrays; with `NaN`; with `{k: undefined}`.

**Proposal (match).** `YValue` equality for attribute purposes is NOT structural: define `YValue.AttributeEquals` = same case with identical scalar value (double `==`, so NaN unequal, but `+0 == -0`), for objects shallow compare of members by *reference identity* (`ReferenceEquals` for object/array/binary payloads), and flag decoded values as never reference-equal to prior ones. Document this in the behaviour inventory; do not use `record` structural equality here by accident.

### Q28 Number to string conversion

**What.** Places where a number or other value becomes text: `YXmlElement.toString()` uses `key + '="' + attrs[key] + '"'` (JS string concatenation: numbers use `Number#toString`, objects become `[object Object]`, arrays join with commas, `undefined` prints `undefined`; no XML escaping); JSON for V1 embeds/awareness (Q11, Q30). `Number#toString` rules: shortest round-trip digits, exponent for `>= 1e21` or `< 1e-6` (`1e+21`, `1e-7`), `-0` prints `0`, `NaN`, `Infinity`.

**Source.** `yjs/src/types/YXmlElement.js` l.113-129; `yjs/src/types/YXmlFragment.js` `toString`; `yjs/src/types/YXmlText.js` `toString` (also uses attribute ordering).

**Repro idea.** Attributes with values `0.1+0.2`, `1e21`, `1e-7`, `-0`, `[1,2]`, `{}`, `null`.

**Proposal (match, shared helper).** One `JsNumberFormatter.ToString(double)` helper (use `double.ToString("R")` as a starting point, then fix exponent formatting to JS rules) shared by XML `ToString` and the V1 JSON serialiser. Add golden tests against the oracle for ~50 numbers.

### Q29 Locale and default sort

**What.** `YXmlElement.toString` sorts attribute names with default `Array#sort` (UTF-16 code unit order, not locale) and lowers the node name with `toLocaleLowerCase()`, which depends on the host locale (Turkish dotless i). Yjs also calls `nodeName.toLocaleLowerCase()` only for display; wire `nodeName` is unchanged.

**Source.** `yjs/src/types/YXmlElement.js` l.113-128.

**Repro idea.** Node names `DIV`, `İ`, `I`; attribute names `b`, `B`, `a`, `é`, `\u{1F600}`.

**Proposal (match, pinned).** Use `string.CompareOrdinal` for sorting (ordinal compares UTF-16 code units, matching JS) and `ToLowerInvariant()` for the lowercase (oracle tests under a neutral locale; document that JS's result is host-locale-dependent and the fixtures assume the neutral locale). **Divergence** note for non-neutral locales.

---

## Other JS-specific behaviours that affect interop

### Q30 `JSON.stringify` / `JSON.parse` fidelity (awareness and V1)

**What.** y-protocols awareness serialises per-client state with `JSON.stringify(state)` and reads with `JSON.parse`. `JSON.stringify`: omits `undefined`/function properties in objects, turns them into `null` in arrays, converts `NaN/Infinity` to `null`, uses `toJSON` (Dates), escapes lone surrogates as `\udXXX` (well-formed JSON.stringify), does not escape non-ASCII or `<>&`, emits no whitespace, and orders keys per Q08. .NET's `System.Text.Json` defaults differ on every one of those (non-ASCII/HTML escaping, `NaN` throwing, no `undefined`). `null` state (client removed) is the string `null`. Also: the clock is a `varUint`, `JSON.parse` of huge integers loses precision, and `outdatedTimeout = 30000` with a check interval of `outdatedTimeout/10` uses `Date.now()` and `setInterval` (Q32).

**Source.** `y-protocols/awareness.js` l.201-204, 226-231, 251-256; `y-protocols/awareness.js` l.13 (timeouts).

**Repro idea.** Awareness updates with states containing `{name:"é", n: NaN, u: undefined, a:[undefined], s:"\ud800"}`, then compare bytes of `encodeAwarenessUpdate`.

**Proposal (match, own serialiser).** The same JS-compatible JSON writer as Q11 (shared component). Parsing may use a lenient standard parser, but numbers must be parsed to `double`. Decide in TASK-1.9 whether awareness states are `YValue` trees (proposal: yes, serialised via the JSON writer).

### Q31 Buffers are views and decoded content aliases input

**What.** `readUint8Array` returns a view over the input buffer (no copy). `ContentBinary` created from a decoded update keeps a reference into the received buffer; `new ContentBinary(new Uint8Array(c))` copies on local insert (`typeListInsertGenericsAfter`) but `typeMapSet` passes `value` directly (not copied), so mutating the caller's array after `YMap.set(k, bytes)` mutates the doc contents. Encoded `Uint8Array`s returned by `toUint8Array()` are fresh copies.

**Source.** `lib0/decoding.js` `readUint8Array`; `yjs/src/types/AbstractType.js` l.671 vs l.864; `yjs/src/structs/ContentBinary.js`.

**Repro idea.** `const b = new Uint8Array([1]); map.set('k', b); b[0] = 9; map.get('k')` and the same through `array.insert`.

**Proposal (diverge).** Copy bytes on insert and on decode (C# `ReadOnlyMemory<byte>` over a pooled buffer would otherwise alias reused memory). Record as a Divergence; the JS aliasing is a bug-like behaviour, not interop. Hot-path allocation tradeoffs belong to TASK-1.15.

### Q32 Time and timers

**What.** Behaviour tied to wall-clock: `UndoManager` capture timeout (`Date.now()`), `PermanentUserData` `setTimeout`, awareness `setInterval` renewals and timeouts, and `Transaction` timestamps if any. None affect update bytes, but they affect observable state in tests.

**Source.** `yjs/src/utils/UndoManager.js`, `yjs/src/utils/PermanentUserData.js` l.87, 109, `y-protocols/awareness.js` l.59-77.

**Repro idea.** Fake-timer driven tests in the oracle (Node `--experimental` fake timers or injected clocks) versus a C# `TimeProvider`.

**Proposal (diverge for ergonomics).** Inject `TimeProvider` (.NET 8) and `PeriodicTimer` equivalents so tests control time; the observable semantics (timeout values 30000 ms and interval 3000 ms) are matched.

---

## Suggested follow-ups for dependent tickets

- **TASK-1.9 (YValue mapping):** Q01-Q09, Q11 define the case list (Undefined, Null, Bool, Double with canonical NaN/-0, Int64, String, Bytes, Array, Object with JS key ordering). Number-tag selection and key-order sorting are encode-side concerns, so the `YValue` struct should not carry a wire tag.
- **TASK-1.10 (fixture format / Node generator):** the generator must be able to express `-0`, `NaN`, `Infinity`, `undefined`, `bigint`, lone surrogates, integer-like object keys, and fixed client IDs. Plain JSON fixtures cannot carry those, so use a tagged encoding. It must include odd-index operations on astral text (Q17) and foreign non-canonical encodings (Q01, Q09).
- **Test design:** keep one tagged test per Q entry whose proposal says "diverge"; per ADR-0003 each also needs its decision record.

## Not covered / open

- NaN canonicalisation (Q04) depends on V8 behaviour at the pinned Node version; confirm the bit pattern from a checked-in fixture, not from memory.
- Promise/async ordering is not an issue in the core library (all synchronous), but `PermanentUserData` and providers are not examined here.
- `y-protocols/sync.js` and `auth.js` use only `writeVarUint`/`writeVarUint8Array`/`writeVarString` and inherit Q12/Q15/Q18; no additional quirks found in this pass.
- A full audit of `Item.integrate` conflict resolution for number-comparison subtleties was not done beyond Q21; TASK on algorithm port should recheck.
