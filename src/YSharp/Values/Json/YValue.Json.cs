#nullable enable
using System;
using System.Buffers;

namespace YSharp;

public readonly partial struct YValue
{
    /// <summary>
    /// Writes the value as <c>JSON.stringify</c> would, as UTF-8 and byte for byte (Q30). A top-level Undefined or a BigInt at any depth is
    /// <see cref="YError.Rejected"/> (ADR 0007); nested Undefined vanishes from objects and becomes null in arrays. Nothing is written on failure.
    /// </summary>
    public YStatus TryWriteJson(IBufferWriter<byte> writer) => JsJsonWriter.Write(this, writer ?? throw new ArgumentNullException(nameof(writer)));

    /// <summary>
    /// Parses <c>JSON.parse</c>-acceptable text. Malformed text is <see cref="YError.Malformed"/>; nesting past <c>MaxAnyDepth</c> is
    /// <see cref="YError.LimitExceeded"/>. Numbers are doubles, so integers past 2^53 are rounded.
    /// </summary>
    public static YStatus TryParseJson(ReadOnlySpan<byte> utf8, in YLimits? limits, out YValue value) => JsJsonReader.Read(utf8, limits, out value);
}
