using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Allocation;

namespace YSharp.Tests.Allocation;

public enum BudgetKind
{
    /// <summary>Allocates exactly zero bytes per operation. Fixed by ADR 0009; never moves.</summary>
    Zero,

    /// <summary>Bounded bytes per operation, one limit per named part. Measured on the first implementation, then recorded plus 25%.</summary>
    Bounded,

    /// <summary>Hostile input of N bytes allocates at most K*N plus a fixed amount (limits <c>k</c> and <c>fixed</c>).</summary>
    Hostile,

    /// <summary>Slow memory tier: managed heap per item must not exceed the Yjs figure (bench/yjs-memory/yjs-bytes-per-item.json).</summary>
    BytesPerItem,

    /// <summary>Slow memory tier: zero gen2 collections and no large-object-heap growth beyond the declared allowance.</summary>
    SteadyState,
}

/// <summary>One row of the table. <see cref="Parts"/> lists the measured parts of the path; a bounded budget has one limit per part.</summary>
public sealed record Budget(string Id, string Path, BudgetKind Kind, params string[] Parts)
{
    /// <summary>The recorded limit of a part in bytes, or null while it is pending (<see cref="AllocationBudgets.Limits"/>).</summary>
    public long? LimitOf(string part = "") => AllocationBudgets.Limits.TryGetValue(Key(Id, part), out var v) ? v : null;

    /// <summary>The parts to check; a path with no named parts has the single unnamed part.</summary>
    public IReadOnlyList<string> PartNames => Parts.Length == 0 ? new[] { "" } : Parts;

    public static string Key(string id, string part) => part.Length == 0 ? id : id + ":" + part;
}

/// <summary>
/// The one table of allocation and memory budgets (ADR 0009, 0018). IDs are the registry's <c>BUD-*</c> entries and are stable: tests
/// reference them, <c>BudgetTableTests</c> cross-checks the table against <c>docs/inventory/budgets.yaml</c>.
/// <para>
/// Rules. Zero paths stay at 0. A bounded limit is the value measured on the first implementation plus 25% (<see cref="WithHeadroom"/>),
/// recorded in <see cref="Limits"/>, and only moves down afterwards; raising one is a reviewed change with a reason in the commit.
/// A bounded budget with no recorded limit is pending: its test measures and reports the value to record, then skips.
/// </para>
/// </summary>
public static class AllocationBudgets
{
    public const double Headroom = 1.25;

    /// <summary>
    /// The recorded limits, keyed <c>BUD-nn</c> or <c>BUD-nn:part</c>. A bounded budget without one is pending. Fill in
    /// <c>WithHeadroom(measured)</c> values as each milestone closes (ADR 0018 milestone gate).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, long> Limits = new Dictionary<string, long>
    {
        // TASK-4.12, owner decision (option A): each transaction hands its body a fresh 24-byte Transaction over pooled state, so a kept
        // reference stays stale for good (CC-9). Measured 24 bytes per operation in Release; recorded with headroom. Contended TryTransact runs no body: 0.
        ["BUD-01:Transact"] = WithHeadroom(24),
        ["BUD-01:ReadTransact"] = WithHeadroom(24),
        ["BUD-02:Uncontended"] = WithHeadroom(24),
        ["BUD-02:Contended"] = 0,
        // TASK-4.20, M3 gate: one-character inserts applied one update at a time (CodecBudgetTests), 1069 bytes per call in Release for V1 and V2.
        ["BUD-08:V1"] = WithHeadroom(1069),
        ["BUD-08:V2"] = WithHeadroom(1069),
        // TASK-4.27, pending owner sign-off: zero is out of reach while every insert allocates an Item and every transaction its 24-byte
        // Transaction (BUD-01 option A), so these paths are bounded. Measured in Release once Events landed: a map Set with a handler on
        // another type 215 bytes, an insert-and-delete commit 974, a one-character insert at the end 606. TASK-4.28 re-measures.
        ["BUD-04"] = WithHeadroom(215),
        ["BUD-05"] = WithHeadroom(974),
        ["BUD-07"] = WithHeadroom(606),
        // TASK-4.28, M4 gate: a scalar Push on an array 128 bytes per call, a scalar Set on a map 200, measured in Release.
        ["BUD-09:Push"] = WithHeadroom(128),
        ["BUD-09:Set"] = WithHeadroom(200),
        // TASK-4.37, M6 gate: a sync Step1/Step2 round trip 1464 bytes, awareness encode 376 and apply 704 per operation, measured in Release.
        ["BUD-10"] = WithHeadroom(1464),
        ["BUD-11:Encode"] = WithHeadroom(376),
        ["BUD-11:Apply"] = WithHeadroom(704),
        // TASK-4.42, M7 gate: the fuzz guard's bound (ADR 0013: 64 bytes per input byte plus 1 MiB), so the corpus and the fuzz inputs are held
        // to one figure. Measured in Release over fixtures/hostile: at most 56.5 bytes per input byte above the 1 MiB (hos-amp
        // 5000-structs-waiting-on-missing-origins), 16% under k = 64 rather than the usual 25% (owner sign-off in the task notes).
        ["BUD-12:k"] = 64,
        ["BUD-12:fixed"] = 1024 * 1024,
    };

    /// <summary>The one-off large-object-heap growth BUD-14 tolerates: a single caller-supplied payload larger than a chunk (ADR 0018). The workload has none, so one chunk is slack.</summary>
    public const long LohAllowanceBytes = 128 * 1024;

    public const string TextInsert = "text-insert";
    public const string ArrayPush = "array-push";
    public const string MapSet = "map-set";

    public static readonly IReadOnlyList<Budget> All = new Budget[]
    {
        new("BUD-01", "Transact/ReadTransact with a static lambda, uncontended", BudgetKind.Bounded, "Transact", "ReadTransact"),
        new("BUD-02", "TryTransact, uncontended and contended", BudgetKind.Bounded, "Uncontended", "Contended"),
        new("BUD-03", "Scalar YValue reads and AttributeEquals", BudgetKind.Zero),
        new("BUD-04", "Event dispatch with no handlers for the changed type", BudgetKind.Bounded),
        new("BUD-05", "Commit with no Update subscriber", BudgetKind.Bounded),
        new("BUD-06", "EncodeStateAsUpdate into a pre-sized writer", BudgetKind.Zero),
        new("BUD-07", "One-character YText.Insert at the end", BudgetKind.Bounded),
        new("BUD-08", "TryApplyUpdate", BudgetKind.Bounded, "V1", "V2"),
        new("BUD-09", "Push and Set of scalars", BudgetKind.Bounded, "Push", "Set"),
        new("BUD-10", "Sync Step1/Step2 round trip", BudgetKind.Bounded),
        new("BUD-11", "Awareness encode and apply", BudgetKind.Bounded, "Encode", "Apply"),
        new("BUD-12", "Hostile input of N bytes", BudgetKind.Hostile, "k", "fixed"),
        new("BUD-13", "Managed heap per item after forced collection", BudgetKind.BytesPerItem, TextInsert, ArrayPush, MapSet),
        new("BUD-14", "Gen2 collections and LOH growth over 100k steady-state edits", BudgetKind.SteadyState),
    };

    /// <summary>The eleven named hot paths of ADR 0009 (BUD-01..11).</summary>
    public static IEnumerable<Budget> HotPaths => All.Where(b => b.Kind is BudgetKind.Zero or BudgetKind.Bounded);

    public static Budget Get(string id) => All.Single(b => b.Id == id);

    public static long WithHeadroom(long measured) => (long)Math.Ceiling(measured * Headroom);

    /// <summary>A bounded or hostile budget with a part that has no recorded limit.</summary>
    public static bool IsPending(Budget b) =>
        b.Kind switch
        {
            BudgetKind.Bounded or BudgetKind.Hostile => b.PartNames.Any(p => b.LimitOf(p) is null),
            _ => false,
        };

    public static int PendingCount => All.Count(IsPending);

    /// <summary>
    /// Asserts one part of a hot path against its budget: zero paths use 0, a recorded limit is enforced, and a pending bounded path
    /// measures and skips with the value to record.
    /// </summary>
    public static void Check(string id, string part, Action operation, int iterations = 200, int warmup = 50)
    {
        var b = Get(id);
        var key = Budget.Key(id, part);
        if (b.Kind == BudgetKind.Zero)
        {
            AllocationAssert.AtMost(key, 0, operation, iterations, warmup);
            return;
        }

        if (b.LimitOf(part) is { } limit)
        {
            AllocationAssert.AtMost(key, limit, operation, iterations, warmup);
            return;
        }

        var measured = AllocationAssert.Measure(operation, iterations, warmup);
        Assert.Skip($"{key} is pending: measured {measured} bytes per operation, record {WithHeadroom(measured)} in AllocationBudgets.Limits once the milestone closes (ADR 0018).");
    }
}
