#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    public Doc? ParentDoc => throw Sketch.NI;
    public IReadOnlyCollection<Doc> Subdocs => throw Sketch.NI;
    public IReadOnlyCollection<string> GetSubdocGuids() => throw Sketch.NI;   // new set, duplicates collapsed (DOC-16)

    public event SubdocsHandler? SubdocsChanged;

}
