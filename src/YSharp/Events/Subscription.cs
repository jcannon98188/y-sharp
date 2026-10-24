#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly struct Subscription : IDisposable { public void Dispose() => throw Sketch.NI; }

