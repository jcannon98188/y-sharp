# Public API shape: implicit transactions, pooled events, Try* beside throwing forms

The C# surface is sketched in `docs/prototype/api-sketch/` (throwaway; the implementing agents turn it into a compiling stub). Choices that a reader would otherwise question:

- **Transactions.** The primary form is a callback (`Transact`, with a state-passing overload for allocation-free static lambdas). The scoped form is a `ref struct` so it cannot cross `await`. Mutators take no transaction argument: they join the calling thread's open transaction or open an implicit one (ADR 0006).
- **Events are pooled and valid only during dispatch.** Deltas are spans, path and keys are lazy. Handlers that keep data must copy it. `Doc.Update` hands out a `ReadOnlySpan<byte>` and encodes only while subscribed.
- **Reads on an unintegrated type return the empty value**, as in JS; writes buffer as prelim content.
- **List and map content is a `YEntry`** (Any, Binary, Type, Doc), because ContentBinary and ContentAny differ on the wire. A top-level `YValue` of kind Bytes becomes Binary, as in JS.
- **Index rules.** Array and list indexes throw as JS does, text clamps, negative indexes throw. The array indexer throws where JS returns `undefined`: a recorded divergence.
- **Two further divergences.** `YXmlElement.ToString()` lowercases with the invariant culture, and resolving a root-name relative position does not create a placeholder root type.
- **Exposed internals.** `ItemId`, `StateVector` and `DeleteSet` are public and immutable; items and structs are not. Awareness states are returned as snapshots.
- **Seams.** `IClientIdGenerator`, `TimeProvider` (Doc, UndoManager, Awareness) and a `Defer` hook for PermanentUserData.
- **Out of scope.** `toDOM`, async APIs and deprecated JS surface other than `Doc.ToJson` and `Destroyed`.
