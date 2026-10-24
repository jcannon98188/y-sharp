#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public static partial class YUpdate
{
    public static YStatus TryMerge(IReadOnlyList<ReadOnlyMemory<byte>> updates, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null) => throw Sketch.NI;
    public static YStatus TryDiff(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null) => throw Sketch.NI;
    public static YStatus TryStateVectorFromUpdate(ReadOnlySpan<byte> update, IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null) => throw Sketch.NI;

}
