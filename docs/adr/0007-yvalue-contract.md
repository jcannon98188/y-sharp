# YValue is an immutable, wire-tag-free tree with explicit conversions

Extends ADR 0002. `YValue` models exactly nine cases (Undefined, Null, Bool, Number, BigInt, String, Bytes, Array, Object), so every JS Any value the wire can carry round-trips and nothing else can be built. Choices that differ from the obvious ones:

- **No wire tag.** Number is a `double`; BigInt is a `long`. The encoder picks the tag from the value, as `writeAny` does, so re-encoding canonicalises foreign tags. NaN is normalised to `0x7FF8000000000000` on encode and any NaN is accepted on decode. `-0` is preserved and encodes as `7d40`.
- **Object keys** are stored in insertion order. The JS rule (array-index keys ascending, then the rest in insertion order) is applied only on encode and JSON output. Duplicate keys keep the first position with the last value. `__proto__` is an ordinary key, a divergence from JS with a tagged test.
- **One tree type** serves V2 Any, V1 JSON embeds and formats, and awareness states, behind one JS-compatible JSON writer. A top-level Undefined or BigInt is an error in V1. Date, Map and ArrayBuffer cannot be built.
- **Bounded, iterative.** Decode is capped by `YLimits.MaxAnyDepth` and the other limits; encode, equality and JSON output use explicit stacks, and encode returns an error past the depth cap. Construction has no limit.
- **Immutable, copy-on-decode.** The document never aliases caller buffers. Reads expose spans and strings without allocating. `BytesOwned` is the explicit transfer factory. The 16-byte layout (an `object?` plus an 8-byte payload) is internal.
- **Equality.** `Equals`, `GetHashCode` and `==` are wire equality (NaN equals NaN, `+0` differs from `-0`). `AttributeEquals` is a separate method that follows YText's JS rule.
- **Conversions.** Implicit only from `bool`, `double`, `int`, `uint` and `string`. BigInt is always explicit; there is no implicit `long`. `TryGet*` is the primary accessor, with throwing `As*` for convenience.
