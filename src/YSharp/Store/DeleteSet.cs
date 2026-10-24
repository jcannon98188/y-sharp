#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed class DeleteSet
{
    public bool Contains(ItemId id) => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
}

