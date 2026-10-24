using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Allocation;

/// <summary>The cross-check of ADR 0009: the table's stable IDs against the registry, the tests that reference them, and the ratchet rules. Runs in every configuration and tier.</summary>
[LibraryIndependent]
[Tier(Tier.Gating)]
public sealed class BudgetTableTests
{
    private static readonly string[] ExpectedIds = Enumerable.Range(1, 14).Select(i => $"BUD-{i:00}").ToArray();

    [Fact, Covers("BUD-01", "BUD-02", "BUD-03", "BUD-04", "BUD-05", "BUD-06", "BUD-07", "BUD-08", "BUD-09", "BUD-10", "BUD-11", "BUD-12", "BUD-13", "BUD-14")]
    public void Table_HasTheStableIds_InOrder() => Assert.Equal(ExpectedIds, AllocationBudgets.All.Select(b => b.Id).ToArray());

    [Fact]
    public void BudgetTable_EveryRow_MatchesTheRegistry()
    {
        var entries = InventoryLoader.LoadEntries(RepoRoot.Find()).Where(e => e.Id.StartsWith("BUD-", StringComparison.Ordinal)).ToList();
        Assert.Equal(ExpectedIds, entries.Select(e => e.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray());
        Assert.All(entries, e => Assert.Equal(14, e.Owner));
    }

    [Fact]
    public void BudgetTable_ElevenHotPaths_AreBud01To11()
    {
        Assert.Equal(11, AllocationBudgets.HotPaths.Count());
        Assert.Equal(ExpectedIds.Take(11), AllocationBudgets.HotPaths.Select(b => b.Id));
        Assert.Equal(7, AllocationBudgets.HotPaths.Count(b => b.Kind == BudgetKind.Zero));
        Assert.Equal(4, AllocationBudgets.HotPaths.Count(b => b.Kind == BudgetKind.Bounded));
    }

    [Fact]
    public void BudgetTable_ZeroPaths_NeverHaveARecordedLimit() =>
        Assert.DoesNotContain(AllocationBudgets.Limits.Keys, k => AllocationBudgets.Get(k.Split(':')[0]).Kind == BudgetKind.Zero);

    [Fact]
    public void BudgetTable_RecordedLimits_NameKnownIdsAndParts() =>
        Assert.All(AllocationBudgets.Limits, kv =>
        {
            var parts = kv.Key.Split(':');
            var b = AllocationBudgets.Get(parts[0]);
            Assert.Contains(parts.Length == 1 ? "" : parts[1], b.PartNames);
            Assert.True(kv.Value >= 0);
        });

    [Fact]
    public void Headroom_IsTwentyFivePercent_RoundedUp()
    {
        Assert.Equal(125, AllocationBudgets.WithHeadroom(100));
        Assert.Equal(2, AllocationBudgets.WithHeadroom(1));
        Assert.Equal(0, AllocationBudgets.WithHeadroom(0));
    }

    [Fact]
    public void BudgetTable_PendingBudgets_AreCountedAndNeverExceedTheRegistryPendingCount()
    {
        // Registry entries stay pending until the implementation exists (ADR 0018); the table's pending count is reported, not hidden.
        var pendingIds = AllocationBudgets.All.Where(AllocationBudgets.IsPending).Select(b => b.Id).ToList();
        var registryPending = InventoryLoader.LoadEntries(RepoRoot.Find()).Count(e => e.Id.StartsWith("BUD-", StringComparison.Ordinal) && e.Status == "pending");
        Assert.True(pendingIds.Count <= registryPending, $"{pendingIds.Count} table budgets are pending but only {registryPending} registry BUD entries are");
        Assert.Equal(new[] { "BUD-08", "BUD-09", "BUD-10", "BUD-11", "BUD-12" }.Where(id => !AllocationBudgets.Limits.Keys.Any(k => k.StartsWith(id, StringComparison.Ordinal))),
            pendingIds.Where(id => !AllocationBudgets.Limits.Keys.Any(k => k.StartsWith(id, StringComparison.Ordinal))));
    }

    [Fact]
    public void BudgetTable_EveryBudgetId_IsReferencedByATest()
    {
        var ids = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods().Cast<MemberInfo>().Append(t))
            .SelectMany(m => m.GetCustomAttributes<CoversAttribute>())
            .SelectMany(a => a.Ids)
            .Where(i => i.StartsWith("BUD-", StringComparison.Ordinal))
            .ToHashSet();
        Assert.True(ExpectedIds.All(ids.Contains), "a budget ID has no test: " + string.Join(", ", ExpectedIds.Where(id => !ids.Contains(id))));
    }

    [Fact]
    public void BudgetTable_EveryMemoryWorkload_HasAYjsBaseline()
    {
        foreach (var w in new[] { AllocationBudgets.TextInsert, AllocationBudgets.ArrayPush, AllocationBudgets.MapSet })
        {
            var figure = MemoryWorkloads.YjsBytesPerItem(w);
            Assert.True(figure is > 0, $"bench/yjs-memory/yjs-bytes-per-item.json has no figure for {w}; run node --expose-gc bench/yjs-memory/measure.mjs");
        }
    }

    [Fact]
    public void BudgetTable_WorkloadSizes_AreTheOnesInAdr0009()
    {
        Assert.Equal(1_000_000, MemoryWorkloads.TextInserts);
        Assert.Equal(1_000_000, MemoryWorkloads.ArrayPushes);
        Assert.Equal(100_000, MemoryWorkloads.MapKeys);
    }

    [Fact]
    public void YjsMeasureScript_AndWorkloads_UseTheSameBatchSize()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot.Find(), "bench", "yjs-memory", "measure.mjs"));
        Assert.Contains($"const BATCH = {MemoryWorkloads.Batch}", script);
    }
}
