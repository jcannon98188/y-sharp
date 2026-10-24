# Match JS behaviour by default

Interop is the core requirement, so y-sharp matches Yjs quirks unless doing so is unsafe. Each deliberate divergence is a recorded decision plus a tagged test that shows the JS behaviour. Behaviour that crashes or recurses without bound always diverges.

Recorded divergences are listed beside the tests that carry `[Diverges]`. One that shapes the API: client ids are `ulong` and accepted up to 2^53-1 (the lib0 limit, ADR 0008); a client id above 2^53-1 on the wire is Malformed, where JS silently rounds it to the nearest double (Q12, ENC-37).
