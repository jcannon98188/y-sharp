using System;

namespace YSharp.TestKit.Assertions;

/// <summary>The one hex helper for every test project: lowercase hex out, spaced or unspaced hex in.</summary>
public static class Hex
{
    /// <summary>Bytes of a spaced or unspaced hex string.</summary>
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    /// <summary>Lowercase hex of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Hex for a failure message, shortened for large inputs.</summary>
    public static string Short(byte[] bytes) => bytes.Length <= 96 ? Of(bytes) : Of(bytes.AsSpan(0, 96)) + $"... ({bytes.Length} bytes)";
}
