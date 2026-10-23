# JS-compatible JSON writer and reader

Task: TASK-1.21. Oracle: Yjs v13.6.33 (`yjs/`), lib0 v0.2.99 (`lib0/`), y-protocols (`y-protocols/`), run under Node v24.21.0 (V8 `JSON`). .NET comparison runs used .NET 11.0.0 (`dotnet run file.cs`, System.Text.Json and `double.ToString`).

Scope: byte-exact `JSON.stringify` text and `JSON.parse` acceptance, because V1 embeds and formats, legacy ContentJSON, awareness states and XML attribute text depend on it. This file extends `docs/research/quirks.md` Q11, Q28, Q30 (which state the rules in one line each) and `docs/adr/0007-yvalue-contract.md` (one `YValue` tree, one JS-compatible JSON writer, top-level Undefined/BigInt is a V1 error). It does not repeat Q08 (key order) beyond the JSON-specific details. Every handling entry is a **proposal**; ADR-0003 default is "match".

Conventions: **[oracle]** = run against the real engine/submodule (throwaway scripts in the session scratchpad, not checked in). Bytes are lowercase hex. "STJ" = System.Text.Json.

Sources: ECMA-262 `JSON.stringify` (SerializeJSONProperty, SerializeJSONObject/Array, QuoteJSONString incl. the ES2019 well-formed-JSON.stringify change, UnicodeEscape), `JSON.parse` + ECMA-404 grammar, InternalizeJSONProperty, and `Number::toString` (ECMA-262 section 6.1.6.1.20, https://tc39.es/ecma262/#sec-numeric-types-number-tostring). Spec section numbers other than that one are given by algorithm name only; the spec page was too large to page through here, so the exact rules below are pinned by oracle runs rather than quoted from the spec text.

---

## 1. Where Yjs, lib0 and y-protocols call JSON

| # | Call site | Direction | What flows through |
|---|---|---|---|
| 1 | `yjs/src/utils/UpdateEncoder.js` l.115-117 `UpdateEncoderV1.writeJSON` = `writeVarString(JSON.stringify(embed))` | write | `ContentEmbed.write` (`structs/ContentEmbed.js` l.80), `ContentFormat.write` (`structs/ContentFormat.js` l.89) |
| 2 | `yjs/src/utils/UpdateDecoder.js` l.113-115 `UpdateDecoderV1.readJSON` = `JSON.parse(readVarString())` | read | `readContentEmbed` (l.97), `readContentFormat` (l.104) |
| 3 | `yjs/src/structs/ContentJSON.js` l.88 `write`: `c === undefined ? 'undefined' : JSON.stringify(c)` per element | write | legacy ContentJSON (ref 2). Not produced by any current Yjs insert API; reachable only from old updates or hand-built Items |
| 4 | `ContentJSON.js` l.108-114 `readContentJSON`: string `'undefined'` -> `undefined`, else `JSON.parse(c)` | read | same |
| 5 | `y-protocols/awareness.js` l.204 `encodeAwarenessUpdate`: `JSON.stringify(state)` where `state = states.get(id) \|\| null` | write | awareness |
| 6 | `awareness.js` l.227/231 `modifyAwarenessUpdate`: parse, user callback, stringify | both | awareness relays |
| 7 | `awareness.js` l.252 `applyAwarenessUpdate`: `JSON.parse(readVarString)` | read | awareness |
| 8 | `lib0/json.js` l.13/21: `export const stringify = JSON.stringify`, `parse = JSON.parse` (pure aliases, no options) | n/a | used by lib0 websocket helpers (`lib0/websocket.js` l.40, 131), not by Yjs or y-protocols |
| 9 | `yjs/src/types/YXmlElement.js` l.113-129 `toString`: `key + '="' + attrs[key] + '"'` | write (text, **not JSON**) | uses `ToString`, not `JSON.stringify`: see section 7 |

V2 does **not** use JSON: `UpdateEncoderV2.writeJSON` = `writeAny` (l.285), `UpdateDecoderV2.readJSON` = `readAny` (l.264). So the JSON writer/reader is needed for exactly: V1 embed, V1 format value, ContentJSON (read, plus write if the library is to re-encode old updates), awareness states, and (via number conversion only) XML attribute text. There are no `reviver`, `replacer` or `space` arguments anywhere in these three codebases (grep: all calls are single-argument). That removes `toJSON`-with-key, replacer arrays and indentation from scope; `toJSON` (Date) is irrelevant because `YValue` cannot hold a Date (ADR-0007), but note a Date can still reach the *oracle* through `insertEmbed({d: new Date(0)})`, giving `"d":"1970-01-01T00:00:00.000Z"` in V1 and `{}` in V2 **[oracle]**. That is a fixture to exclude, not support.

Also relevant: all JSON text travels through `writeVarString`/`readVarString` (UTF-8, Q18), so the JSON text itself is converted to UTF-8; with `JSON.stringify`'s well-formed output this is lossless (section 3.3).

---

## 2. Number formatting (`Number::toString` radix 10)

### 2.1 The rule

Spec algorithm (ECMA-262 6.1.6.1.20), for finite non-zero `x`: let `n, k, s` be integers with `k >= 1`, `10^(k-1) <= s < 10^k`, `s * 10^(n-k)` exactly the value of `x` when read back (round-trips), and **`k` as small as possible**; if several `s` satisfy this for the minimal `k`, choose the `s` closest to the true value (spec note: if two are equally close, the even one is acceptable). With `digits` the decimal digits of `s`:

| Condition | Output |
|---|---|
| `k <= n <= 21` | `digits` followed by `n-k` zeros (integer, no point, no exponent) |
| `0 < n <= 21` | `digits[0..n]` `.` `digits[n..k]` |
| `-6 < n <= 0` | `0.` + `-n` zeros + `digits` |
| otherwise | `d[0]` (`.` `d[1..k]` if `k>1`) `e` sign `abs(n-1)`; sign is always written, `+` or `-` |

Translated to magnitude: plain decimal for `1e-6 <= |x| < 1e21`; exponent form outside that, with `e+` / `e-`, **no zero padding of the exponent**, lowercase `e`, and a single digit before the point. Special cases: `+0` and `-0` both give `"0"`; `NaN` gives `"NaN"`; `Infinity` gives `"Infinity"` (but see 2.3 for JSON).

Oracle table (`JSON.stringify(n)` equals `String(n)` for every finite number) **[oracle]**:

| value | JS text | note |
|---|---|---|
| `100000000000000000000` (1e20) | `100000000000000000000` | n=21 still integer form |
| `1e21` | `1e+21` | first exponent form |
| `1.5e21` | `1.5e+21` | |
| `123456789012345680000` | `123456789012345680000` | shortest digits then zero padding, not the exact value |
| `999999999999999900000` | `999999999999999900000` | |
| `1e-6` / `0.000001` | `0.000001` | n=-5 still plain |
| `1.5e-6` | `0.0000015` | |
| `1e-7` | `1e-7` | first exponent form |
| `1.234e-7` | `1.234e-7` | |
| `5e-7` | `5e-7` | |
| `5e-324` | `5e-324` | min subnormal |
| `1.7976931348623157e308` | `1.7976931348623157e+308` | |
| `2^53` | `9007199254740992` | |
| `2^53+2` | `9007199254740994` | |
| `0.1+0.2` | `0.30000000000000004` | |
| `1/3` | `0.3333333333333333` | 16 digits, shortest |
| `4.35` | `4.35` | |
| `1e23` | `1e+23` | |
| `8.41e21` | `8.41e+21` | |

### 2.2 -0, NaN, Infinity in JSON

`JSON.stringify(-0)` is `"0"` (SerializeJSONProperty calls `ToString(-0)`), `NaN`, `Infinity`, `-Infinity` all become `null` (not finite), including inside arrays/objects **[oracle]**: `JSON.stringify([NaN,-0,Infinity])` = `[null,0,null]`. Yjs V1 consequences **[oracle]**: `insertEmbed(1, {n:NaN, i:-Infinity, z:-0})` -> V1 payload text `{"n":null,"i":null,"z":0}` (`7b226e223a6e756c6c2c2269223a6e756c6c2c227a223a307d`); the same embed in V2 keeps NaN/-Infinity/-0 via `writeAny` (Q03/Q04). So a V1 round trip permanently loses `-0` and NaN. Top-level `NaN` as a format value gives `null` text, which is also the "remove attribute" marker: `t.format(0,1,{k:NaN})` would write `null` (same as `format(..., {k:null})`).

### 2.3 Shortest-digit choice vs .NET

`double.ToString()` and `"R"` in .NET Core 3.0+ both return the shortest round-trippable digit string (IEEE-correct, 17 digit fallback only when needed). **[oracle, differential]** I ran 499,834 doubles (300k random 64-bit patterns excluding NaN/Inf, 100k decimal-ish `k/10^j`, 100k `random * 10^e` for e in [-25, 24]) through Node `String(x)` and a C# formatter that takes `ToString("R", InvariantCulture)`, extracts digits/exponent and re-lays them out by the table above: **0 mismatches**. The same set also parses back to the identical double via `double.Parse` (0 mismatches). So the shortest digits agree with V8's; only the *layout* differs. The sketch used (to be turned into the shared `JsNumberFormatter`):

```csharp
static string Js(double v) {
  if (v == 0) return "0";                    // also -0
  if (double.IsNaN(v)) return "NaN";         // JSON caller maps non-finite to null
  string sign = v < 0 ? "-" : ""; v = Math.Abs(v);
  if (double.IsInfinity(v)) return sign + "Infinity";
  string r = v.ToString("R", CultureInfo.InvariantCulture);       // e.g. "1.234E-07", "1E+21"
  int e10 = 0, ei = r.IndexOfAny(new[]{'E','e'}), dot;
  string m = r;
  if (ei >= 0) { e10 = int.Parse(r.Substring(ei + 1), CultureInfo.InvariantCulture); m = r.Substring(0, ei); }
  dot = m.IndexOf('.');
  string digits = dot >= 0 ? m.Remove(dot, 1) : m; int pointPos = dot >= 0 ? dot : m.Length;
  int lead = 0; while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
  digits = digits.Substring(lead).TrimEnd('0'); pointPos -= lead; if (digits.Length == 0) digits = "0";
  int n = pointPos + e10, k = digits.Length;  // value = 0.digits * 10^n
  /* then the four layout cases from 2.1 */
}
```

### 2.4 How .NET differs

**[oracle, .NET 11]**:

| value | JS | `double.ToString("R", Invariant)` | STJ `JsonValue.Create(x).ToJsonString()` |
|---|---|---|---|
| `1e21` | `1e+21` | `1E+21` | `1E+21` |
| `1e-7` | `1e-7` | `1E-07` | `1E-07` |
| `1e-6` | `0.000001` | `1E-06` | `1E-06` |
| `1.234e-7` | `1.234e-7` | `1.234E-07` | `1.234E-07` |
| `1e20` | `100000000000000000000` | `1E+20` | `1E+20` |
| `123456789012345680000` | same digits, integer form | `1.2345678901234568E+20` | same as R |
| `999999999999999900000` | integer form | `9.999999999999999E+20` | same |
| `1e300` | `1e+300` | `1E+300` | `1E+300` |
| `-0` | `0` | `-0` | `-0` |
| `NaN` | `null` (JSON) / `NaN` | `NaN` | **throws ArgumentException** |
| `Infinity` | `null` / `Infinity` | `Infinity` | **throws** |
| `5e-324` | `5e-324` | `5E-324` | `5E-324` |
| `0.1` (`G17`) | `0.1` | `0.1` (but `G17` gives `0.10000000000000001`) | `0.1` |

Differences to pin: (a) uppercase `E`, (b) exponent zero-padded to at least two digits (`E-07`), (c) .NET switches to exponent form far earlier than JS (observed: 1e20 prints as 1E+20 where JS prints 21 digits; 1e-6 prints 1E-06 where JS prints 0.000001), so thresholds differ, (d) `-0` keeps its sign, (e) non-finite throws or prints names. `G17`/`E16` are not shortest and must not be used. Always use `CultureInfo.InvariantCulture` (current culture changes the decimal separator). NaN/Infinity must be intercepted before formatting.

### 2.5 Number parsing (`JSON.parse`)

Grammar (ECMA-404): `-? (0 | [1-9][0-9]*) (\.[0-9]+)? ([eE][+-]?[0-9]+)?`. Value is the correctly rounded IEEE-754 double (round half to even) **[oracle]**:

| text | result |
|---|---|
| `1e400` / `-1e400` | `Infinity` / `-Infinity` (no error; stringify later makes them `null`) |
| `1e-400` | `0` |
| `2.4703282292062327e-324` | `0` (below half of min subnormal) |
| `2.4703282292062328e-324` | `5e-324` |
| `1.7976931348623159e308` | `Infinity` |
| `9007199254740993` | `9007199254740992` (ties to even) |
| `123456789012345678901234567890` | `1.2345678901234568e+29` |
| `-0` / `-0e0` | `-0` (preserved by parse; `Object.is(v,-0)` true) |
| `1e0`, `1E+2`, `0.1e1`, `1.0e+5` | `1`, `100`, `1`, `100000` |

So the C# parser has no integer type: integers beyond 2^53 are lossy, `1e0` becomes the double `1` (then V1 would re-emit `1`), and `-0` is preserved on decode. `double.Parse(s, NumberStyles.Float, Invariant)` agrees with all of the above **[oracle, .NET 11]**: `1e400 -> Infinity`, `1e-400 -> 0`, the two 2.47...e-324 cases, `9007199254740993`, `-0` (bits `8000...`). Do not use `decimal`/`BigInteger`. Note decode of `-0` in V1 gives `-0` double, whereas encode of `-0` writes `0`: V1 is not a lossless round trip even for values JSON can represent.

STJ differs on parse: `JsonDocument.Parse("1e400")` is accepted syntactically and `GetDouble()` returns Infinity here (no exception observed in .NET 11), and `JsonSerializer`/`Utf8JsonWriter` refuse to write Infinity. Leading zeros, `+1`, `.5`, `1.`, `NaN`, `Infinity`, `0x10` are rejected by both (identical set, see section 5).

---

## 3. String escaping (`QuoteJSONString`)

### 3.1 Rules

Output is `"` + escaped code units + `"`, iterating **UTF-16 code points** of the string (ES2019 "well-formed JSON.stringify"):

| Code unit(s) | Output |
|---|---|
| `"` (U+0022) | `\"` |
| `\` | `\\` |
| U+0008 | `\b` |
| U+0009 | `\t` |
| U+000A | `\n` |
| U+000C | `\f` |
| U+000D | `\r` |
| other U+0000-U+001F (includes U+000B) | `\u00xx` lowercase hex, 4 digits (`\u000b`, `\u001f`) |
| lone high surrogate U+D800-U+DBFF or lone low surrogate U+DC00-U+DFFF | `\udxxx`, **lowercase hex** (`\ud800`) |
| everything else, including valid surrogate pairs | emitted as-is |

Not escaped: `/` (no `\/`), `<`, `>`, `&`, `'`, `+`, U+007F (DEL), U+0080-U+009F, U+00A0, **U+2028 and U+2029** (raw; legal in JSON strings and since ES2019 in JS strings), U+FEFF, any non-ASCII. Because pairs are kept and lone surrogates escaped, the output is always valid Unicode and survives UTF-8 conversion byte-exactly.

**[oracle]** `JSON.stringify` of:

| input | output | UTF-8 bytes of the output |
|---|---|---|
| U+0000..U+001F in order | `"\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010...\u001f"` | `\b \t \n \f \r` short forms, rest `\u00xx` |
| `"` then `\` | `"\"\\"` | `225c225c5c22` |
| `/<>&'` | `"/<>&'"` | `222f3c3e262722` |
| U+007F | raw | `227f22` |
| U+0080 U+009F | raw | `22c280c29f22` |
| U+00A0 | raw | `22c2a022` |
| U+2028 U+2029 | raw | `22e280a8e280a922` |
| U+FEFF | raw | `22efbbbf22` |
| `é€` | raw | `22c3a9e282ac22` |
| U+1F600 (pair) | raw | `22f09f988022` |
| `"\ud800"` | `"\ud800"` | `225c756438303022` |
| `"\udc00"` | `"\udc00"` | `225c756463303022` |
| `"\ud800a"` | `"\ud800a"` | |
| `"\udc00\ud800"` (reversed) | `"\udc00\ud800"` | both escaped separately |
| `"\ud800" + valid pair` | `"\ud800😀"` | lone one escaped, pair raw |
| `"\udfff"` | `"\udfff"` | |
| U+0000 | `"\u0000"` | `225c753030303022` |

Object keys go through the same `QuoteJSONString`: `{'a"b\n\ud800':1}` -> `{"a\"b\n\ud800":1}` **[oracle]**.

### 3.2 Yjs consequences **[oracle]**

* V1 embed `{s:'\ud800'}` -> payload text `{"s":"\ud800"}` (`7b2273223a225c7564383030227d`), decodes back to the lone surrogate. V2 `writeAny` of the same string goes through the UTF-8 encoder and the peer sees `�` (U+FFFD): `efbfbd` appears in the V2 bytes (Q18). So V1 preserves lone surrogates losslessly and V2 does not; the C# writer must emit the escape (not U+FFFD) in V1 and the reader must produce a lone surrogate `char`. Format values behave the same: `format(0,1,{k:'\ud800'})` -> `"\ud800"` text (`08225c756438303022`).
* A string with U+2028/U+2029 is raw in the payload (`e280a8e280a9` in the V1 bytes).
* Awareness: state `{s:'\ud800'}` serialises as `"s":"\ud800"` (hex `2273223a225c7564383030225d`... inside the full update, observed `5c7564383030`).
* The escape text is ASCII, so `writeVarString` never needs to deal with the surrogate: but a **foreign** peer that emitted a raw lone surrogate would have it replaced by `efbfbd` on its own UTF-8 encode, and the JS decoder would parse U+FFFD. Raw U+FFFD in JSON text parses to U+FFFD **[oracle]**.

### 3.3 .NET differences **[oracle, .NET 11]**

| input | STJ default encoder | `UnsafeRelaxedJsonEscaping` | JS |
|---|---|---|---|
| `"` | `"` | `\"` | `\"` |
| `<>&'+` | `<>&'+` | raw | raw |
| `é€` | `é€` | raw | raw |
| U+0000-U+001F | `\u0000...`, `\b\t\n\f\r` short, `\u000B`, `\u001F` **uppercase hex** | same | **lowercase** hex |
| U+007F | `\u007F` | `\u007F` | raw |
| U+0080 | `\u0080` | `\u0080` | raw |
| U+2028/9 | `  ` | `  ` | raw |
| valid pair | `😀` | `😀` | raw |
| lone `\ud800` | **`�`** (silently replaced) | `�` | `\ud800` |
| `\ud800a` | `�a` | same | `\ud800a` |
| `\udc00\ud800` | `��` | same | two escapes |

No STJ configuration reproduces JS (U+007F-U+009F, U+2028/9, uppercase hex, surrogate pairs, and lone-surrogate replacement all differ even with `UnsafeRelaxedJsonEscaping`). STJ also cannot *read* a lone-surrogate escape into a string: `JsonElement.GetString()` on `"\ud800"` throws `InvalidOperationException: Cannot read incomplete UTF-16 JSON text as string with missing low surrogate`, even though `JsonDocument.Parse` itself accepted it. **Proposal: hand-written writer and parser**; the escape set is small. For the parser's lone-surrogate behaviour, build the `string` from `char`s directly.

---

## 4. Value handling in `JSON.stringify` (SerializeJSONProperty)

**[oracle]** All of these are what the oracle writes; Yjs only ever reaches them via `insertEmbed`, `format`, awareness states and `ContentJSON`.

| Value | Top-level | Object property | Array element |
|---|---|---|---|
| `undefined` | returns JS `undefined` (no text) | property **omitted** | `null` |
| function | `undefined` | omitted | `null` |
| symbol | `undefined` | omitted (also symbol-keyed props are ignored) | `null` |
| `NaN`, `±Infinity` | `null` | `null` | `null` |
| `-0` | `0` | `0` | `0` |
| BigInt | throws `TypeError: Do not know how to serialize a BigInt` | throws | throws |
| boxed `new Number(1)`, `new String('a')`, `new Boolean(false)` | unboxed: `1`, `"a"`, `false` | same | same |
| `Date` | ISO string via `toJSON` (invalid Date: `null`) | | |
| `Map`, `Set`, `ArrayBuffer` | `{}` | `{}` | `{}` |
| `Uint8Array([1,2])` | `{"0":1,"1":2}` | | |
| object with `toJSON()` | uses its result | | |
| cyclic structure | throws `TypeError: Converting circular structure to JSON` | | |
| sparse array `[,1,,]` | `[null,1,null]` | | |
| non-enumerable / inherited / class-getter members | skipped (own enumerable string keys only; instance own fields included) | | |

Consequences for Yjs:

* **Top-level `undefined`/function/symbol** gives the JS value `undefined`; `writeVarString(undefined)` then throws `TypeError: Cannot read properties of undefined (reading 'length')` (seen when calling `format(0,1,{k:undefined})` in the V1 encoder, and by a ContentJSON element that is a function) **[oracle]**. Hence ADR-0007 "top-level Undefined is an error in V1": the error must be raised at encode time, not silently written as `undefined` or `null`. ContentJSON special-cases `undefined` (writes the literal text `undefined`, which is *not* valid JSON), but a function/symbol element is not special-cased and throws.
* **BigInt**: V1 `insertEmbed(1,{b:1n})` throws at encode time; V2 accepts it (tag 122). `YValue` BigInt (Int64) in a V1 embed/format/awareness state at any depth must throw (not just top-level; ADR-0007 only names the top level, but `{b:1n}` throws too). **Proposal: the writer throws on `Int64` at any depth, and on `Undefined` only at top level; nested `Undefined` follows the table (object property omitted, array element `null`).**
* Embed/format/awareness values that cannot be constructed in `YValue` (Date, Map, boxed primitives, class instances, cycles) need no writer support. Cycles cannot exist in an immutable tree.
* Awareness quirk **[oracle]**: `encodeAwarenessUpdate` uses `states.get(id) || null`, so a falsy local state (`0`, `""`, `false`, `undefined`) is serialised as `null` (`...04 6e756c6c`), and `getLocalState()` also returns `null` for these. `setLocalState(5)` writes `35`, `"x"` writes `2278 22`, `[1]` writes `5b315d` (non-object states are allowed). Decide in the awareness API whether to mirror (proposal: mirror, since the bytes are observable).
* Depth: `JSON.stringify` recursion overflows in V8 around 1e5 nesting (`RangeError: Maximum call stack size exceeded`, observed at 100,000 nested arrays) while `JSON.parse` handled 1,000,000 nested arrays fine **[oracle]**. The C# writer and parser must use explicit stacks (ADR-0007 already requires it) and a `YLimits.MaxAnyDepth` cap on both, with parse failing safely rather than stack overflowing.

### 4.1 Key ordering (JSON-specific details)

`JSON.stringify` visits `EnumerableOwnProperties(keys)`, i.e. `OrdinaryOwnPropertyKeys`: **array-index keys ascending numerically first, then string keys in insertion order** (then symbols, skipped). Q08 states the rule; the oracle boundary table **[oracle]**:

`{a:1, 4294967294:1, 4294967295:1, '01':1, '-1':1, '1.5':1, '1e3':1, 1000:1}` -> `{"1000":1,"4294967294":1,"a":1,"4294967295":1,"01":1,"-1":1,"1.5":1,"1e3":1}`.

So array-index = canonical decimal string of an integer in `[0, 2^32-2]` (`4294967294` is an index, `4294967295` is not, `01`, `-1`, `1.5`, `1e3` are not). The empty key and keys with spaces are ordinary. `{z:1,10:1,9:1,'':1,' ':1}` -> `{"9":1,"10":1,"z":1,"":1," ":1}`.

For Yjs's own objects: embed `{b:1,2:1,a:1,1:1}` gives V1 text `{"1":1,"2":1,"b":1,"a":1}` (`7b2231223a312c2232223a312c2262223a312c2261223a317d`) **[oracle]**. **Duplicates**: a JS object cannot hold duplicate keys, so a `YValue` object built with a duplicate key must collapse to last value at first position before writing (`Object.fromEntries([['a',1],['b',2],['a',3]])` -> `{"a":3,"b":2}`) **[oracle]**.

### 4.2 Output shape

No whitespace at all (`[1,2]`, `{"a":1}`; separators `,` and `:` only), no trailing newline. Arrays: `[` elements `,` `]`; empty `[]`, `{}`. Object properties whose value is undefined/function/symbol are skipped *before* the comma logic, so no stray commas (`{u:undefined,f(){},k:1}` -> `{"k":1}`).

---

## 5. `JSON.parse` leniency and errors

`JSON.parse` is strict ECMA-404 (JSON grammar), with V8 detail below. **[oracle]** table, input -> outcome:

**Accepted**

| Input | Result |
|---|---|
| `null`, `true`, `"a"`, `1`, `[]`, `{}` | any value is a valid top-level text (scalars too) |
| ` \t\n\r1 ` | whitespace is exactly U+0020, U+0009, U+000A, U+000D |
| `"\/"` | `/` (the `\/` escape is legal) |
| `"A"`, `"\u0000"` | `A`, U+0000 |
| `"\ud800"`, `"\udc00\ud800"` | **lone / reversed surrogate escapes are accepted**, produce lone code units |
| `"𐀀"` (uppercase hex) | pair, `𐀀` |
| raw U+2028, U+007F, U+00A0 inside strings | allowed |
| raw lone surrogate inside a JS string passed to `JSON.parse` | preserved (only occurs in-process; wire text is UTF-8) |
| `-0`, `0e0`, `-0e0`, `1e5`, `1E5`, `1.0e+5` | numbers (see 2.5) |
| `{"a":1,"a":2}` | duplicate keys allowed: **last value wins** at the *first* position (`{"a":1,"b":2,"a":3}` -> `{"a":3,"b":2}`) |
| `{"__proto__":{"x":1},"y":2}` | `JSON.parse` uses CreateDataProperty: `__proto__` becomes an **own property** (`Object.keys` includes it, prototype unchanged, `o.x` is `undefined`). Unlike `readAny` (Q09), which uses plain assignment `obj[key] = v`. |
| `{"__proto__":5}` | own property, key kept |

**Rejected (SyntaxError)**

`01`, `00`, `-01` (leading zeros), `+1`, `.5`, `-.5`, `1.`, `1e`, `1e+`, `-`, `--1`, `0x10`, `NaN`, `Infinity`, `-Infinity`, `undefined`, `NULL`, `True`, `nul`, `tru`, `'a'` (single quotes), `[1,]`, `{"a":1,}` (trailing commas), `[,1]`, `{,}`, `{a:1}`, `{1:2}` (unquoted/number keys), `{"a"}`, `{"a":}`, `[1 2]`, `{"a":1 "b":2}`, `[1,2` (unterminated), `"abc` (unterminated string), `[1]x`, `1 2`, `"a" "b"`, `{"a":1}}` (trailing content), empty or whitespace-only input, comments (`// c\n1`, `/*c*/1`), U+00A0 / U+FEFF (BOM) / U+000B / U+000C / U+2028 / U+2029 as whitespace, raw U+0000 or any raw control character U+0000-U+001F inside a string (`"\t"`, `"\n"`), bad escapes (`"\x41"`, `"\a"`, `"\U0041"`), `"\u00"` and `"\u004g"` (short or non-hex `\u`).

V8 error messages (not part of the contract, tests should only assert "throws"): e.g. `Unexpected token ']', "[1,]" is not valid JSON`, `Bad control character in string literal in JSON at position 1`, `Expected double-quoted property name in JSON at position 7`. Error classes: `SyntaxError` always (non-string input is coerced: `JSON.parse(5)` -> `5`, `JSON.parse(undefined)` -> `SyntaxError: "undefined" is not valid JSON`).

Effects at the Yjs call sites **[oracle]**:

* Awareness: `applyAwarenessUpdate` does `JSON.parse(readVarString)` **without try/catch**, so a malformed state text throws out of `applyAwarenessUpdate` (`NaN`, `{"a":1,}`, empty string, truncated `[1,2`, two concatenated values `{"a":1}{"b":2}` all threw), and entries already applied earlier in the same update remain applied (the loop is not transactional; clients before the bad entry were already stored). Leading/trailing ASCII whitespace is tolerated (` {"a":1} ` ok), a leading BOM is not (`ef bb bf 31` -> SyntaxError, since the decoder keeps U+FEFF per Q18). `null` state is legal (client removal). Whole state may be any JSON value, e.g. `"..."` or a number. `{"a":1e2,"b":9007199254740993}` decodes to `{a:100, b:9007199254740992}` (lossy), and a re-encode (`modifyAwarenessUpdate` with an identity callback) of `{"b":1e2,"a":1.0,"2":0.10}` gives `{"2":0.1,"b":100,"a":1}` (renormalised: key order, number text, no whitespace).
* ContentJSON read: elements are parsed individually; `'undefined'` (exact text) -> `undefined`; `'undefined '`, `' undefined'`, `{"a":1,}` throw `SyntaxError` (parse error propagates out of `applyUpdate`).
* V1 embed/format read: `readJSON` errors propagate as `SyntaxError`; invalid UTF-8 in the JSON string is caught earlier by the fatal `TextDecoder` (`TypeError: The encoded data was not valid for encoding utf-8`). Both are failures the C# decoder must surface through the ADR-0005 error model (malformed JSON text is a decode error, not a crash).
* Re-encode canonicalisation: decode -> encode normalises key order (integer keys first), drops insignificant whitespace, rewrites numbers (`1e2` -> `100`, `1.0` -> `1`, `0.10` -> `0.1`, `-0` -> `0`), lowercases `\u` hex, unescapes `\/`, and un-escapes escaped printable characters. Exact byte round-trip of foreign V1 JSON text is therefore not guaranteed, and the C# implementation must not "preserve original text".

### 5.1 .NET parser differences **[oracle, .NET 11]**

`JsonDocument.Parse` (defaults) accepts and rejects the same set as `JSON.parse` for every row in the table above, **except**:

| Difference | Detail |
|---|---|
| Max depth | default `MaxDepth = 64`: `[`x65 throws `JsonReaderException`, even `[`x100 fails; JS has no practical limit (1e6 ok). Needs `JsonDocumentOptions.MaxDepth` raised or a custom parser. |
| Duplicate keys | `JsonDocument` keeps **both** (`{"a":1,"a":2}` raw text preserved). `JsonNode.Parse` of `{"a":1,"a":3}` returned `{"a":1,"a":3}` in this build (no dedupe on `ToJsonString`), while `JsonObject.Add("a",2)` throws `ArgumentException`. JS: last wins at first position. |
| Lone surrogate escape | parse accepted, but `GetString()` throws (section 3.3). |
| Whole-number precision | parser keeps raw text; `GetDouble` is correct rounding; no 2^53 issue until you pick a type. |
| Options | `AllowTrailingCommas` and `CommentHandling.Skip` make STJ *more lenient* than JS (`[1,]`, `// c`); never enable. With defaults, the accept/reject set matches. |
| `\/` | accepted, unescaped `/`: same. |
| Raw U+0000 etc. in strings | rejected: same. |
| BOM | `JsonDocument.Parse(string)` with leading U+FEFF rejects (same as JS). From a `byte[]`/`Utf8JsonReader` a UTF-8 BOM prefix is *skipped* by `JsonDocument.Parse(ReadOnlyMemory<byte>)` in some overloads: another reason to parse from the already-decoded string, not raw bytes. (not oracle-verified for the byte overload) |

**Proposal (match, own components).** Writer and reader are hand-written, operating on `YValue`: the reader may be built on `Utf8JsonReader` only if it is configured `MaxDepth = YLimits.MaxAnyDepth`, no trailing commas/comments, and strings are decoded by custom unescaping (to keep lone surrogates). A hand-written ~200 line recursive-descent (explicit stack) parser is simpler to make bit-exact and safe. Decode numbers with `double.Parse(NumberStyles.Float, InvariantCulture)` on a validated grammar slice (the validated slice excludes forms `double.Parse` accepts but JSON forbids: leading `+`, leading zeros, `.5`, `1.`, `Infinity`, `NaN`, hex, thousands, whitespace).

---

## 6. Awareness and V1 specific checklists

Behaviours the fixtures in TASK-1.10/1.11 should capture (all **[oracle]**):

1. V1 embed matrix on `insertEmbed(1, v)` for `v` in: `{n:NaN,i:-Infinity,z:-0}`, `{s:'\ud800'}`, `{s:'  '}`, `{e:'é😀'}`, `{u:undefined,a:[undefined]}` (-> `{"a":[null]}`), `{b:2**53+2,c:1e21,d:5e-324}` (-> `{"b":9007199254740994,"c":1e+21,"d":5e-324}`), `{b:1,2:1,a:1,1:1}`, `{b:1n}` (throws), `[1]`, `1`, `{f(){}, g:1}` (-> `{"g":1}`). `insertEmbed` requires an object or number/array (a string is inserted as text; `null`/`undefined` throw `TypeError` reading `constructor`), so scalar `null` embeds cannot occur from the public API but a wire `null` text is legal to decode.
2. Format values: `null` (NaN also becomes `null`), `0`, `{a:null}`, `"\ud800"`, `1e21` (-> `1e+21`). `format(..., {k: undefined})` throws in V1 (`Cannot read properties of undefined`), and `{k: NaN}` writes `null`, which Yjs interprets as "clear attribute".
3. Awareness: full-state update for `{name:'é',n:NaN,u:undefined,a:[undefined],s:'\ud800',z:-0,big:1e21,k:{b:1,2:1,a:1}}` serialises to text `{"name":"é","n":null,"a":[null],"s":"\ud800","z":0,"big":1e+21,"k":{"2":1,"b":1,"a":1}}` (the oracle hex starts `010501587b226e616d65223a22c3a9...`). Note the clock is a `varUint` and `null` text for a removed client.
4. ContentJSON: element list `[1, undefined, NaN, {b:1,2:2}, '\ud800', -0, 1e21]` encodes as texts `1`, `undefined`, `null`, `{"2":2,"b":1}`, `"\ud800"`, `0`, `1e+21` (update hex `010101000201016107013109756e646566696e6564046e756c6c0d7b2232223a322c2262223a317d08225c75643830302201300531652b323100`) and decodes to `[1, undefined, null, {2:2,b:1}, '\ud800', 0, 1e21]` (NaN becomes `null`, `-0` becomes `0`). Built in the oracle by constructing `Item(..., new ContentJSON([...]))` directly, since no public API creates ContentJSON.

---

## 7. XML attribute text is `ToString`, not JSON

`YXmlElement.toString` appends `key + '="' + attrs[key] + '"'`: a JS string concatenation, i.e. `ToString(value)`, **no escaping of quotes, `&`, `<`**, attributes sorted with default `Array.prototype.sort` (UTF-16 code unit order). Oracle outputs for one element **[oracle]**:

```
<p a="0.30000000000000004" b="1e+21" c="1e-7" d="0" e="1,2" f="[object Object]" g="null" h="undefined" i="NaN" j="true" k=""q"&<" l="123456789012345680000" m="5e-324" n="9007199254740994" o="[object Object]" p="1,2,3" q=",,x" r="1" s="Wed Dec 31 1969 19:00:00 GMT-0500 (Eastern Standard Time)"></p>
```

Takeaways: numbers use exactly the section 2 formatter (so `-0` -> `0`, `NaN` -> `NaN`, not `null`); `null` -> `null`, `undefined` -> `undefined`, booleans `true`/`false`; arrays are `Array.prototype.join(',')` recursively with `null` and `undefined` as the **empty string** (`[null,undefined,'x']` -> `,,x`, `[[1],[2,[3]]]` -> `1,2,3`); plain objects -> `[object Object]`; BigInt -> decimal digits (`1`), a Date would print a locale/time-zone-dependent string (not representable in `YValue`). `YXmlElement.setAttribute` accepts all of these through `typeMapSet`. Proposal: a shared `JsToString(YValue)` with these rules, sharing `JsNumberFormatter`; fixtures use only deterministic values (no Date). Attribute keys are plain text (no escaping, sorted ordinal).

---

## 8. Proposed shape of the C# components (feeds TASK-1.9/1.10)

* `JsNumberFormatter.ToString(double)`: section 2 layout over `double.ToString("R", Invariant)` digits (differentially verified on ~500k doubles), `-0` -> `"0"`; caller maps NaN/Inf to `null` for JSON and to `"NaN"`/`"Infinity"`/`"-Infinity"` for XML text.
* `JsJsonWriter.Write(YValue, IBufferWriter<char|byte>)`: explicit stack, array-index key ordering (Q08), strings per section 3.1 (lowercase hex, lone-surrogate escapes, no `/` or non-ASCII escaping), omit nested `Undefined` object members, `Undefined` array element -> `null`, throw on top-level `Undefined` and on `Int64` (BigInt) at any depth, NaN/Inf/-0 per 2.2, no whitespace. Bytes should go straight to UTF-8 (lone surrogates are already escaped, so a strict encoder never sees one).
* `JsJsonReader.Read(ReadOnlySpan<char>/utf8) -> YValue`: strict ECMA-404, leading/trailing whitespace limited to U+0020/09/0A/0D, depth-limited and iterative, duplicate keys last-wins at first position, `__proto__` an ordinary key (this actually matches `JSON.parse`, unlike `readAny`: Q09 stays a divergence only for the V2 path), numbers via `double.Parse` of the validated slice, lone surrogate escapes preserved, raw control characters rejected, trailing content rejected. Failure is a typed decode error (ADR-0005).
* Awareness: `JSON.stringify(state)` on `YValue`, falsy-to-`null`, parse errors surface from apply without rolling back earlier entries (observable JS behaviour; decide separately whether to diverge for atomic apply, ADR-0003 requires a recorded decision).
* Test shape (all oracle-driven per ADR-0011/0012): (a) number table of ~50 entries plus a seeded random sweep, comparing text; (b) string table from section 3.1 comparing exact UTF-8 bytes; (c) parse accept/reject table from section 5 (assert outcome class only, not message), incl. the numbers table of 2.5; (d) V1/V2 embed, format, awareness, ContentJSON fixtures from section 6; (e) property: `Parse(Write(v))` equals `v` modulo the lossy cases (`-0`, NaN, undefined, >2^53).

## 9. Open points

* Spec text for `QuoteJSONString`, `SerializeJSONObject` and `JSON.parse` could not be paged here; the rules above come from the oracle (Node 24 / V8) and are stable since ES2019. If a citation-grade quote is needed, fetch `https://tc39.es/ecma262/#sec-quotejsonstring` and `#sec-json.parse` with a larger window.
* The 499,834-double sweep is differential against V8, not a proof; the shortest-closest tie behaviour for exotic doubles is unchecked beyond that sample. A larger sweep in CI is cheap.
* STJ byte-overload BOM handling and `JsonNode.Parse` duplicate behaviour were noted but not rechecked on older runtimes. The project target decides whether it matters (it should not, since the proposal avoids STJ for text).
* Awareness non-atomic apply and the falsy-to-`null` behaviour are behaviours to mirror or consciously diverge from: record in the awareness decision (ADR-0014 scope).
