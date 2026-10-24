#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public static partial class YUpdate
{
    public static YStatus TryConvert(ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, IBufferWriter<byte> output, YLimits? limits = null) => throw Sketch.NI;
    public static YStatus TryValidate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null) => throw Sketch.NI;

}
