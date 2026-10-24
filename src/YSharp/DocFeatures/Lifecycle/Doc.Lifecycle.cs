#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    public string Guid => throw Sketch.NI;

    public bool IsLoaded => throw Sketch.NI;
    public bool IsSynced => throw Sketch.NI;
    public bool IsDestroyed => throw Sketch.NI;

    // Spec 8: members JS exposes that the public sketch hides.
    internal bool ShouldLoad => throw Sketch.NI;
    internal bool AutoLoad => throw Sketch.NI;
    internal string? CollectionId => throw Sketch.NI;
    internal YValue Meta => throw Sketch.NI;
    /// Manual garbage collection of the whole store (JS Y.tryGc(createDeleteSetFromStructStore(store), store, gcFilter)).
    internal void CollectGarbage() => throw Sketch.NI;
    /// Provider hooks that raise the JS emit('load') and emit('sync', [isSynced]) (DOC-13, DOC-14).
    internal void SignalLoaded() => throw Sketch.NI;
    internal void SignalSynced(bool isSynced) => throw Sketch.NI;

    public event Action<Doc>? Loaded, Synced, Destroying, Destroyed;

    public void Load() => throw Sketch.NI;
    public void Destroy() => throw Sketch.NI;                 // a write transaction (CC-17)
    public void Dispose() => Destroy();

}
