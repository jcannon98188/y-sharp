using System;
using System.Collections.Generic;
using Xunit.v3;

namespace YSharp.TestKit.Tiers;

/// <summary>ADR 0015 tiers. Gating is the default and runs everywhere with no Node.</summary>
public enum Tier { Gating, Smoke, Nightly }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class TierAttribute : Attribute, ITraitAttribute
{
    private readonly Tier _tier;
    public TierAttribute(Tier tier) => _tier = tier;
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        new[] { new KeyValuePair<string, string>("Tier", _tier.ToString()) };
}

/// <summary>Marks a test that needs a Node child. Such tests are never <see cref="Tier.Gating"/>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class NeedsNodeAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        new[] { new KeyValuePair<string, string>("Needs", "Node") };
}

/// <summary>The documented environment variables (ADR 0012, 0015, 0017).</summary>
public static class YSharpEnv
{
    /// <summary><c>YSHARP_TIER</c>: which tier a CI job selects.</summary>
    public static Tier CurrentTier => ParseTier(Environment.GetEnvironmentVariable("YSHARP_TIER"));

    /// <summary><c>YSHARP_REPLAY</c>: path of one saved script to re-run with no generation.</summary>
    public static string? ReplayPath => Nullable(Environment.GetEnvironmentVariable("YSHARP_REPLAY"));

    /// <summary><c>YSHARP_SEED</c>: pins one randomized-test seed locally.</summary>
    public static uint? Seed => ParseSeed(Environment.GetEnvironmentVariable("YSHARP_SEED"));

    public static Tier ParseTier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Tier.Gating
        : Enum.TryParse<Tier>(value, ignoreCase: true, out var t) ? t
        : throw new ArgumentException($"Unknown YSHARP_TIER '{value}' (use Gating, Smoke or Nightly).");

    public static uint? ParseSeed(string? value) => string.IsNullOrWhiteSpace(value) ? null : uint.Parse(value);

    private static string? Nullable(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
