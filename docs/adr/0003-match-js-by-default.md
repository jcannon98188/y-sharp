# Match JS behaviour by default

Interop is the core requirement, so y-sharp matches Yjs quirks unless doing so is unsafe. Each deliberate divergence is a recorded decision plus a tagged test that shows the JS behaviour. Behaviour that crashes or recurses without bound always diverges.
