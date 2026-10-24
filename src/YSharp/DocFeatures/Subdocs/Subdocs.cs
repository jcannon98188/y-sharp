#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public delegate void SubdocsHandler(SubdocsChange change, Doc doc, Transaction tx);

public readonly struct SubdocsChange { public IReadOnlyList<Doc> Added { get; } public IReadOnlyList<Doc> Removed { get; } public IReadOnlyList<Doc> Loaded { get; } }

