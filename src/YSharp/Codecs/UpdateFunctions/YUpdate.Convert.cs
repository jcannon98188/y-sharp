#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public static partial class YUpdate
{
    public static YStatus TryConvert(ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, IBufferWriter<byte> output, YLimits? limits = null) => throw Sketch.NI;
    /// <summary>Whether <paramref name="update"/> decodes as <c>TryApplyUpdate</c> would decode it: Malformed or LimitExceeded, or success. Nothing is applied.</summary>
    public static YStatus TryValidate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        limits ??= YLimits.Default;
        if (update.Length > limits.MaxUpdateBytes) return new YStatus(YError.LimitExceeded);
        var error = Doc.Decode(update, format, limits, out _);
        return error == YError.None ? default : new YStatus(error);
    }

}
