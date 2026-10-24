#nullable enable
using System;

namespace YSharp.Lib0;

/// <summary>
/// What <see cref="AnyReader"/> delivers, in wire order. The reader checks the structure and the limits; the sink decides what to
/// build (TASK-4.9 builds <c>YValue</c>s) and how to treat duplicate keys. Every container is closed by its matching End call,
/// and a failed read stops without closing the containers still open.
/// </summary>
internal interface IAnySink
{
    void Undefined();
    void Null();
    void Bool(bool value);

    /// <summary>A JS number: from the integer tag (-0 included), float32 or float64. Any NaN pattern arrives as <see cref="double.NaN"/>.</summary>
    void Number(double value);

    void BigInt(long value);
    void String(string value);

    /// <summary>A view into the input that is valid for this call only; copy what is kept (ADR 0007).</summary>
    void Bytes(ReadOnlySpan<byte> value);

    /// <summary>The element count, already checked against the bytes remaining, so a sink may reserve it.</summary>
    void BeginArray(int count);
    void EndArray();

    /// <summary>The member count, already checked against the bytes remaining. Each member is a <see cref="Key"/> call then a value.</summary>
    void BeginObject(int count);
    void Key(string key);
    void EndObject();
}
