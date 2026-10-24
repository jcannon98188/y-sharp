using System;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz;

/// <summary>Nightly tests run only when <c>YSHARP_TIER=Nightly</c> (ADR 0013, 0015); in every other tier they skip visibly.</summary>
internal static class NightlyGate
{
    public static void Require()
    {
        if (YSharpEnv.CurrentTier != Tier.Nightly) Assert.Skip("Nightly tier only (set YSHARP_TIER=Nightly).");
    }

    public static bool IsNightly => YSharpEnv.CurrentTier == Tier.Nightly;
}

internal static class Hex
{
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Hex for a failure message, shortened for large inputs.</summary>
    public static string Short(byte[] bytes) => bytes.Length <= 96 ? Of(bytes) : Of(bytes.AsSpan(0, 96)) + $"... ({bytes.Length} bytes)";
}
