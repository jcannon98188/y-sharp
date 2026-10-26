#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public interface IClientIdGenerator { ulong Next(); }   // seam: fixtures pin client IDs

public sealed record DocOptions
{
    public bool Gc { get; init; } = true;
    public Func<ItemView, bool>? GcFilter { get; init; }
    public string? Guid { get; init; }
    public string? CollectionId { get; init; }
    public YValue Meta { get; init; }
    public bool AutoLoad { get; init; }
    public bool ShouldLoad { get; init; } = true;
    public ulong? ClientId { get; init; }
    public IClientIdGenerator? ClientIdGenerator { get; init; }
    public YLimits Limits { get; init; } = YLimits.Default;
    public TimeProvider? TimeProvider { get; init; }
}

