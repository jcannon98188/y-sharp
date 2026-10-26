#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace YSharp;

/// <summary>
/// Decode limits (ADR 0005). A limit is exceeded when the value is greater than the limit: a value equal to the limit is accepted, one more is
/// LimitExceeded. Each is checked against the length claim before anything is allocated, on every decode path (apply, merge, diff, convert,
/// state vector, validate; V1 and every V2 column), and a rejection leaves the document unchanged. A negative limit is refused when it is set,
/// because the decoders compare against it as an unsigned length and a negative would switch the check off.
/// </summary>
public sealed record YLimits
{
    private int maxUpdateBytes = 64 * 1024 * 1024;
    private int maxAnyDepth = 128;
    private int maxStructsPerUpdate = 10_000_000;
    private int maxPendingBytes = 64 * 1024 * 1024;
    private int maxStringBytes = 64 * 1024 * 1024;

    public static YLimits Default { get; } = new();

    public int MaxUpdateBytes { get => maxUpdateBytes; init => maxUpdateBytes = NonNegative(value); }

    public int MaxAnyDepth { get => maxAnyDepth; init => maxAnyDepth = NonNegative(value); }

    public int MaxStructsPerUpdate { get => maxStructsPerUpdate; init => maxStructsPerUpdate = NonNegative(value); }

    public int MaxPendingBytes { get => maxPendingBytes; init => maxPendingBytes = NonNegative(value); }

    public int MaxStringBytes { get => maxStringBytes; init => maxStringBytes = NonNegative(value); }

    private static int NonNegative(int value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "A limit cannot be negative.");
}
