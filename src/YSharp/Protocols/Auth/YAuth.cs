#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public static class YAuth
{
    public static void WritePermissionDenied(IBufferWriter<byte> writer, string reason) => throw Sketch.NI;
    /// A null permissionDeniedHandler throws ArgumentNullException at this call, before any byte is read (AU-5; JS fails with a TypeError only when type 0 is read).
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, Doc doc, Action<Doc, string> permissionDeniedHandler) => throw Sketch.NI;
}

