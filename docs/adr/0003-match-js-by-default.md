# Match JS behaviour by default

Interop is the core requirement, so y-sharp matches Yjs quirks unless doing so is unsafe. Each deliberate divergence is a recorded decision plus a tagged test that shows the JS behaviour. Behaviour that crashes or recurses without bound always diverges.

Recorded divergences are listed beside the tests that carry `[Diverges]`. One that shapes the API: client ids are `ulong` and accepted up to 2^53-1 (the lib0 limit, ADR 0008); a client id above 2^53-1 on the wire is Malformed, where JS silently rounds it to the nearest double (Q12, ENC-37).

Clocks and lengths (Q22): JS holds them as doubles; y-sharp holds clocks and lengths as `long` and finds structs by a plain binary search. Below 2^53 this is observably identical. On the wire a clock, a length or a clock plus a length that is negative, non-integer or above 2^53-1 is Malformed, where JS rounds silently. Varuint fields cannot be negative or fractional, so in practice this rejects 2^53 and above, the sum of a start clock and a length above 2^53-1 (struct ranges and delete-set ranges), and a negative clock produced by a V2 diff column. Tests: `Q22_*` in `MalformedInputTests`.
