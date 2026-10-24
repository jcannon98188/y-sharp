using System;
using System.Text;
using Xunit;
using YSharp.StoreSpike.Content;
using YSharp.StoreSpike.Harness;
using YSharp.TestKit.Tiers;
using YSharp.StoreSpike.ObjectPerItem;

namespace YSharp.Tests.Allocation;

/// <summary>
/// Behaviour of spike candidate A, object per item (TASK-4.4, ADR 0018 spike amendment). Tests go through the driver interface, and through
/// <see cref="ItemRef"/> for the struct layout the merge rules produce. Expected layouts are worked by hand from Yjs v13.6.33
/// (<c>Item.mergeWith</c>, <c>tryToMergeWithLefts</c>, <c>findNextPosition</c>), not recomputed by the store's own code.
/// </summary>
public sealed class SpikeObjectPerItemTests
{
    [Fact]
    public void InsertChar_AtTheEndAndInTheMiddle_BuildsTheVisibleText()
    {
        var store = new ObjectPerItemStore();
        store.InsertChar(0, 'a');
        store.InsertChar(1, 'c');
        store.InsertChar(1, 'b');
        store.InsertChar(0, '>');
        store.Commit();

        Assert.Equal(">abc", store.Text());
        Assert.Equal(4, store.TextLength);
    }

    [Fact]
    public void Typing_AtTheEnd_MergesIntoOneStructAtCommit()
    {
        var store = Typed("abc");

        Assert.Equal(1, store.StructCountOf(store.LocalClient));
        Assert.Equal(3, store.TextStart.Length);
        Assert.True(store.TextStart.Right.IsNull);
    }

    [Fact]
    public void Insert_InsideAnItem_SplitsIt_AndTheSplitPartsDoNotRemerge()
    {
        // "abcd" is one struct (clocks 0..3). Inserting at 2 splits it into "ab" and "cd"; "X" (clock 4) sits between them. At commit "cd"
        // cannot merge back into "ab", because "ab".right is now "X" (Item.mergeWith requires left.right === right).
        var store = Typed("abcd");
        store.InsertChar(2, 'X');
        store.Commit();

        Assert.Equal("abXcd", store.Text());
        Assert.Equal(3, store.StructCountOf(store.LocalClient));

        var x = store.TextStart.Right;
        Assert.Equal(4u, x.Clock);
        Assert.Equal((store.LocalClient, 1u), store.OriginOf(x));
        Assert.Equal((store.LocalClient, 2u), store.RightOriginOf(x));
    }

    [Fact]
    public void Delete_SplitsOutTheCharacter_RecordsItInTheDeleteSet_AndReleasesItsContentAtCommit()
    {
        var store = Typed("abcd");
        store.DeleteAt(1);

        var pending = store.PendingDeletes;
        Assert.Equal(1, pending.Count);
        Assert.Equal(1u, pending[0].Clock);
        Assert.Equal(1u, pending[0].Length);
        var b = store.TextStart.Right;
        Assert.True(b.IsDeleted);
        Assert.Equal(ContentKind.Text, b.Kind);

        store.Commit();

        Assert.Equal("acd", store.Text());
        Assert.Equal(3, store.TextLength);
        Assert.Equal(3, store.StructCountOf(store.LocalClient));
        Assert.Equal(ContentKind.Deleted, b.Kind);
        Assert.False(b.IsCountable);
        Assert.Equal(0, store.PendingDeletes.Count);
    }

    [Fact]
    public void AdjacentDeletes_MergeInTheDeleteSet_AndTheDeletedStructsMergeAtCommit()
    {
        // "abcd", then delete "b" and "c": the splits leave [a][b][c][d]; [b] and [c] are linked, clock-adjacent and both deleted, so
        // tryMergeDeleteSet merges them into one deleted struct of length 2.
        var store = Typed("abcd");
        store.DeleteAt(1);
        store.DeleteAt(1);

        Assert.Equal(1, store.PendingDeletes.Count);
        Assert.Equal(new DeleteRange(0, 1, 2), store.PendingDeletes[0]);

        store.Commit();

        Assert.Equal("ad", store.Text());
        Assert.Equal(3, store.StructCountOf(store.LocalClient));
        var deleted = store.TextStart.Right;
        Assert.True(deleted.IsDeleted);
        Assert.Equal(2, deleted.Length);
        Assert.Equal(ContentKind.Deleted, deleted.Kind);
    }

    [Fact]
    public void SetKey_NewAndOverwritten_KeepsTheLatestValue_AndDeletesTheReplacedItem()
    {
        var store = new ObjectPerItemStore();
        store.SetKey("k1", 1);
        store.SetKey("k2", 2);
        store.Commit();
        var first = store.MapEntry("k1");

        store.SetKey("k1", 10);
        store.Commit();

        Assert.Equal(10.0, store.ValueOfKey("k1"));
        Assert.Equal(2.0, store.ValueOfKey("k2"));
        Assert.Null(store.ValueOfKey("k3"));
        Assert.Equal(2, store.MapKeyCount);
        Assert.Equal(3, store.Stats.StructCount);

        // Yjs: the replaced item stays as a deleted left neighbour of the new one, with its content released.
        var current = store.MapEntry("k1");
        Assert.NotEqual(first, current);
        Assert.Equal(first, current.Left);
        Assert.True(first.IsDeleted);
        Assert.Equal(ContentKind.Deleted, first.Kind);
        Assert.Equal((store.LocalClient, first.Clock), store.OriginOf(current));
    }

    [Fact]
    public void PushValue_ManyBatches_KeepsTheValuesInOrder_InOneMergedStruct()
    {
        var store = new ObjectPerItemStore();
        SpikeWorkloads.PushScalars(store, 3_500);

        var values = store.ArrayValues();
        Assert.Equal(3_500, values.Length);
        for (var i = 0; i < values.Length; i++) Assert.Equal(i, values[i]);

        // Every push is clock-adjacent and linked to the previous one, and the values are contiguous in the column, so the commit merge
        // folds the whole array into one struct, as Yjs does (one ContentAny for the whole array-push workload).
        Assert.Equal(1, store.StructCountOf(store.LocalClient));
        Assert.Equal(3_500, store.Stats.StructCount);
    }

    [Fact]
    public void RandomEdits_WithRandomCommits_MatchAStringModel()
    {
        var random = new Random(2024);
        var store = new ObjectPerItemStore();
        var model = new StringBuilder();
        for (var step = 0; step < 30_000; step++)
        {
            if (model.Length > 0 && random.Next(5) < 2)
            {
                var index = random.Next(model.Length);
                store.DeleteAt(index);
                model.Remove(index, 1);
            }
            else
            {
                var index = random.Next(model.Length + 1);
                var c = (char)('a' + random.Next(26));
                store.InsertChar(index, c);
                model.Insert(index, c);
            }

            if (random.Next(7) == 0) store.Commit();
            Assert.Equal(model.Length, store.TextLength);
        }

        store.Commit();
        Assert.Equal(model.ToString(), store.Text());
        Assert.InRange(store.SearchMarkerCount, 1, SearchMarkers.Max);
    }

    [Fact]
    public void TextWorkload_MatchesTheControlStore()
    {
        var store = new ObjectPerItemStore();
        var control = new ControlStore();
        SpikeWorkloads.TypeText(store, 20_000);
        SpikeWorkloads.TypeText(control, 20_000);

        Assert.Equal(control.Text(), store.Text());
        Assert.Equal(20_000, store.Stats.StructCount);
        Assert.Equal(20, store.Stats.Commits);
    }

    [Fact]
    public void SteadyState_KeepsTheLength_AndMatchesTheControlStore()
    {
        var store = new ObjectPerItemStore();
        var control = new ControlStore();
        foreach (IStoreDriver d in new IStoreDriver[] { store, control })
        {
            SpikeWorkloads.SteadyStateStart(d, 2_000);
            SpikeWorkloads.SteadyStateEdits(d, 6_000);
        }

        Assert.Equal(2_000, store.TextLength);
        Assert.Equal(control.Text(), store.Text());
        Assert.Equal(0, store.PendingDeletes.Count);
    }

    [Fact]
    public void EveryChunk_AtOrBelow64KB_AfterSlowTierShapedWork()
    {
        var store = new ObjectPerItemStore();
        SpikeWorkloads.Build(store, SpikeWorkloads.TextInsert, 200_000);
        SpikeWorkloads.Build(store, SpikeWorkloads.ArrayPush, 200_000);
        SpikeWorkloads.Build(store, SpikeWorkloads.MapSet, 50_000);
        SpikeWorkloads.SteadyStateEdits(store, 5_000);

        Assert.Equal(50_000, store.MapKeyCount);
        Assert.Equal(49_999.0, store.ValueOfKey("k49999"));
        Assert.True(store.MaxChunkBytes <= 64 * 1024, $"largest block {store.MaxChunkBytes} bytes");
    }

    [Fact]
    public void ClientIds_Are53Bit()
    {
        var store = new ObjectPerItemStore(ClientTable.MaxClientId);
        store.InsertChar(0, 'a');
        store.Commit();

        Assert.Equal(ClientTable.MaxClientId, store.ClientOf(store.TextStart));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectPerItemStore(ClientTable.MaxClientId + 1));
    }

    [Fact]
    public void DeletingEveryCharacterOfATextChunk_MakesTheChunkReleasable()
    {
        // One chunk holds 32,768 characters. Typing 40,000 fills chunk 0 and starts chunk 1; deleting the first 32,768 characters
        // releases every slice of chunk 0.
        var store = new ObjectPerItemStore();
        // Commit as the workloads do. One 40,000-character transaction would be quadratic: Yjs findMarker walks left over every
        // clock-adjacent item, and nothing merges them before the commit.
        for (var i = 0; i < 40_000; i++)
        {
            store.InsertChar(i, 'x');
            if (i % 1000 == 999) store.Commit();
        }

        store.Commit();
        Assert.Equal(0, store.ReleasableTextChunks);

        // Committing every 256 deletes lets the commit merge the tombstones into one struct, so lookups do not walk thousands of them.
        for (var i = 0; i < TextBuffer.ChunkChars; i++)
        {
            store.DeleteAt(0);
            if (i % 256 == 255) store.Commit();
        }
        store.Commit();

        Assert.Equal(40_000 - TextBuffer.ChunkChars, store.TextLength);
        Assert.Equal(1, store.ReleasableTextChunks);
    }

    [Fact]
    public void Candidates_ObjectPerItem_IsRegisteredWithTheHarness()
    {
        Assert.IsType<ObjectPerItemStore>(SpikeCandidates.Factory(SpikeCandidates.ObjectPerItem)());

        var m = SpikeHarness.MeasureSteadyState(SpikeCandidates.ObjectPerItem, SpikeCandidates.Factory(SpikeCandidates.ObjectPerItem),
            edits: 4_000, warmup: 200, startChars: 1_000);
        Assert.Equal(0.5, m.StructsPerOp);
    }

    [Tier(Tier.Nightly)]
    [Theory]
    [InlineData(SpikeWorkloads.TextInsert)]
    [InlineData(SpikeWorkloads.ArrayPush)]
    [InlineData(SpikeWorkloads.MapSet)]
    [InlineData(SpikeWorkloads.SteadyState)]
    public void Process_ObjectPerItem_MeetsTheSpikeGates(string workload)
    {
#if DEBUG
        Assert.Skip("The spike measurements run only in Release (ADR 0009).");
#endif
        if (YSharpEnv.CurrentTier != Tier.Nightly) Assert.Skip("The spike measurements belong to the Nightly tier (YSHARP_TIER=Nightly).");

        var m = SpikeProcess.MedianOfRuns(SpikeCandidates.ObjectPerItem, workload);
        TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(m));

        // ADR 0018 selection gates: Yjs bytes per operation, zero gen2 collections in the steady state, no LOH growth.
        var ceiling = workload switch
        {
            SpikeWorkloads.TextInsert => 194.77,
            SpikeWorkloads.ArrayPush => 8.31,
            SpikeWorkloads.MapSet => 312.26,
            _ => double.MaxValue,
        };
        Assert.True(m.BytesPerOp <= ceiling, $"{m.BytesPerOp} bytes per operation, gate {ceiling}");
        Assert.Equal(0, m.LohGrowthBytes);
        if (workload == SpikeWorkloads.SteadyState) Assert.Equal(0, m.Gen2Collections);
    }

    [Fact]
    public void SmallStartList_RandomInsertsAndRemoves_MatchAListModel_AndStartSmall()
    {
        var list = new SmallStartList<long>();
        list.Add(1);
        Assert.True(list.MaxBlockBytes <= 16 * sizeof(long));

        // 8,192 longs per full block: enough elements to cross several block boundaries.
        var random = new Random(77);
        var model = new System.Collections.Generic.List<long> { 1 };
        for (var i = 0; i < 60_000; i++)
        {
            var roll = random.Next(20);
            if (model.Count > 0 && roll < 4)
            {
                var index = random.Next(model.Count);
                list.RemoveAt(index);
                model.RemoveAt(index);
            }
            else if (model.Count > 0 && roll == 4)
            {
                var index = random.Next(model.Count);
                var count = random.Next(Math.Min(model.Count - index, 10) + 1);
                list.RemoveRange(index, count);
                model.RemoveRange(index, count);
            }
            else
            {
                var index = random.Next(model.Count + 1);
                list.Insert(index, i);
                model.Insert(index, i);
            }
        }

        Assert.Equal(model.Count, list.Count);
        for (var i = 0; i < model.Count; i++) Assert.Equal(model[i], list[i]);
        Assert.True(model.Count > SmallStartList<long>.BlockCapacity);
        Assert.Equal(64 * 1024, list.MaxBlockBytes);

        list.RemoveRange(0, list.Count - 1);
        Assert.Equal(model[^1], list[0]);
    }

    private static ObjectPerItemStore Typed(string text)
    {
        var store = new ObjectPerItemStore();
        for (var i = 0; i < text.Length; i++) store.InsertChar(i, text[i]);
        store.Commit();
        return store;
    }
}
