#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

/// Decode limits (ADR 0005). A limit is exceeded when the value is greater than the limit: a value equal to the limit is accepted, one more is
/// LimitExceeded. Each is checked against the length claim before anything is allocated, on every decode path (apply, merge, diff, convert,
/// state vector, validate; V1 and every V2 column), and a rejection leaves the document unchanged.
public sealed record YLimits
{
    public static YLimits Default { get; } = new();
    public int MaxUpdateBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxAnyDepth { get; init; } = 128;
    public int MaxStructsPerUpdate { get; init; } = 10_000_000;
    public int MaxPendingBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxStringBytes { get; init; } = 64 * 1024 * 1024;
}

