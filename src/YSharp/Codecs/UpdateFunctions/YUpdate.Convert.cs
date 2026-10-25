#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public static partial class YUpdate
{
    /// <summary>
    /// <paramref name="update"/> rewritten from <paramref name="from"/> to <paramref name="to"/> (Yjs <c>convertUpdateFormat</c>): the structs in
    /// read order, Skips kept, then the delete set as <c>readDeleteSet</c> and <c>writeDeleteSet</c> pass it through. The same format is written
    /// again too, as Yjs does.
    /// </summary>
    public static YStatus TryConvert(ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, IBufferWriter<byte> output, YLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        limits ??= YLimits.Default;
        var error = DecodeForFunctions(update, from, limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        Write(to, structs, InWriteOrder(refs.Deletes), output);
        return default;
    }

    /// <summary>Whether <paramref name="update"/> decodes as <c>TryApplyUpdate</c> would decode it: Malformed or LimitExceeded, or success. Nothing is applied.</summary>
    public static YStatus TryValidate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        limits ??= YLimits.Default;
        if (update.Length > limits.MaxUpdateBytes) return new YStatus(YError.LimitExceeded);
        var error = Doc.Decode(update, format, limits, out _);
        return error == YError.None ? default : new YStatus(error);
    }

}
