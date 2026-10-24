# Source modules, seams and file ownership in src/YSharp

Extends ADR 0015 (layout) and the ADR 0018 milestone amendment. The implementation stays one assembly, `src/YSharp`. This ADR splits it into modules so the sequential core and the parallel fan-out (M3, M4, M5, M6) can be specced without two specs editing the same file. It also places the two seams that carry the most risk.

## Modules

| Module (folder) | Owns | Milestone |
|---|---|---|
| `Encoding/` (namespace `YSharp.Lib0`) | lib0 primitives: varints, strings, buffer reader and writer, rented buffers, Any encoding | M1 |
| `Values/` | `YValue`, JS-compatible JSON writer and reader, error types, `YLimits` | M1 |
| `Store/` | struct storage in the layout chosen by M0, the struct store, content buffers, internal delete set, ids | M2 |
| `Integration/` | item integration, pending structs, rejection, garbage collection | M2 |
| `Transactions/` | the document lock (ADR 0006), transaction lifecycle, cleanup and merge, update emission | M2 |
| `Codecs/` | the update encoder/decoder interface, the V1 adapter (M2), the V2 adapter (M3), `YUpdate` merge, diff and convert | M2, M3 |
| `Types/` | the list and map core, search markers, text formatting, then one sub-folder per shared type | M4 |
| `Events/` | event building, pooled dispatch, observer-fault containment (ADR 0005) | M4 |
| `DocFeatures/Undo`, `Snapshots`, `Positions`, `Subdocs`, `UserData` | one folder each | M5 |
| `Protocols/Awareness`, `Sync`, `Auth` | one folder each | M6 |

Public types stay in the `YSharp` namespace. Internals live in `YSharp.<Module>`.

## Seams

- **Storage layout is hidden behind `Store/`.** The store exposes an opaque handle (`readonly struct ItemRef`) and small accessors that can be inlined. Integration, codecs and types never learn whether an item is an object or an index into an arena. A layout change (ADR 0018 point 4) touches only `Store/`. There is no `IStore` interface: it would have one adapter, so the seam would be hypothetical, and it would put virtual calls on the hot path.
- **The codec seam is real.** V1 and V2 are two adapters behind one encoder/decoder interface, the same shape as `UpdateEncoderV1/V2` in Yjs. Integration and encoding are generic over it (`where TDecoder : struct, IUpdateDecoder`), so the JIT specialises each adapter without virtual dispatch. M3 adds the V2 adapter without editing M2's files.
- **Lifecycle hooks are partial methods.** M2 declares C# `partial` methods at every point where a later milestone joins transaction cleanup: before observers, observers, after the transaction, subdocs and GC. The owning milestone implements each one in its own file of the same partial class. An unimplemented hook compiles away. Internal events or delegates were rejected because they cost a call on every transaction. Editing M2's cleanup directly was rejected because it breaks file ownership.

## Ownership

- **One owner per file.** Every file has exactly one owning spec. A public type that carries members from several milestones (`Doc`, `YType`, `Transaction` and others) is a `partial class` with one file per owning module, for example `Doc.cs` (M2), `Doc.Events.cs` (M4) and `Doc.Subdocs.cs` (M5).
- **Additive internal changes.** A spec may add an internal member to a module it does not own, such as a new accessor or helper, and records it in its task notes.
- **Changes to someone else's module.** Changing the behaviour or signature of a module a spec does not own requires a separate Backlog task against that module. That task runs alone, one writer per module at a time, and the spec that needs it waits.

## Consequences

An M0 spec, "Source layout skeleton", runs alongside the spike and the feature-gates spec (ADR 0020). It moves the five stub files into these folders and partial files and declares the lifecycle hook points. It changes no behaviour: every member still throws `NotImplementedException`, and an API-diff check shows the public surface is unchanged. M1 starts after it.

## Amendment: the `Encoding/` folder uses the namespace `YSharp.Lib0`

The `Encoding/` module keeps its folder, but its types live in the namespace `YSharp.Lib0`, not `YSharp.Encoding`. A `YSharp.Encoding` namespace shadows `System.Text.Encoding` wherever code inside `YSharp` writes `Encoding.UTF8`, and it broke the existing TestKit and fuzz code (found by TASK-4.8). Renaming the namespace avoids editing those suite files. Later specs put codec and lib0 types in `YSharp.Lib0`.
