using System;
using System.Text;
using Xunit;
using YSharp.StoreSpike.Arenas;
using YSharp.StoreSpike.Harness;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Allocation;

/// <summary>
/// Spike candidate B, structs in chunked arenas (TASK-4.5). The tests drive the store through <see cref="IStoreDriver"/>, as the workloads do,
/// and observe it through the <see cref="ItemRef"/> accessors the store exposes. They check behaviour, not budgets: the budgets are measured
/// by the harness in the Nightly tier. The spike never touches the library, so the class is library independent (ADR 0020).
/// </summary>
[LibraryIndependent]
public sealed class ArenaStoreTests
{
    [Fact]
    public void Typing_NearTheEnd_ProducesTheSameTextAsAStringModel()
    {
        const int n = 5_000;
        var store = new ArenaStore();
        SpikeWorkloads.TypeText(store, n);

        var expected = new StringBuilder();
        for (var j = 0; j < n; j++)
        {
            var index = Math.Max(0, expected.Length - SpikeWorkloads.Back(j));
            expected.Insert(index, (char)('a' + j % 26));
        }

        Assert.Equal(expected.ToString(), store.Text());
        Assert.Equal(n, store.TextLength);
    }

    [Theory]
    [InlineData(9001)]
    [InlineData(42)]
    [InlineData(7)]
    public void RandomInsertsAndDeletes_WithAndWithoutCommits_MatchAStringModel(int seed)
    {
        var random = new Random(seed);
        var store = new ArenaStore();
        var model = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            if (model.Length > 0 && random.Next(3) == 0)
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

            if (random.Next(8) == 0) store.Commit();
            if (i % 97 == 0) Assert.Equal(model.ToString(), store.Text());
        }

        store.Commit();
        Assert.Equal(model.ToString(), store.Text());
        Assert.Equal(model.Length, store.TextLength);
    }

    [Fact]
    public void SteadyStateMix_KeepsTheTextLength_AndMatchesTheModel()
    {
        var store = new ArenaStore();
        var model = new StringBuilder();
        SpikeWorkloads.SteadyStateStart(store, 1_000);
        model.Append('a', 1_000);
        for (var i = 0; i < 6_000; i++)
        {
            var back = SpikeWorkloads.Back(i);
            if ((i & 1) == 0) model.Insert(Math.Max(0, model.Length - back), 'x');
            else model.Remove(Math.Max(0, model.Length - 1 - back), 1);
        }

        SpikeWorkloads.SteadyStateEdits(store, 6_000);

        Assert.Equal(model.ToString(), store.Text());
        Assert.Equal(1_000, store.TextLength);
    }

    [Fact]
    public void TypingARun_InOneTransaction_MergesIntoOneStructAtCommit()
    {
        var store = new ArenaStore();
        foreach (var c in "hello") store.InsertChar(store.TextLength, c);
        Assert.Equal(5, store.LiveStructCount);

        store.Commit();

        Assert.Equal(1, store.LiveStructCount);
        var item = store.FindItem(store.ClientId, 0);
        Assert.Equal(0, store.ClockOf(item));
        Assert.Equal(5, store.LengthOf(item));
        Assert.Equal(ContentKind.Text, store.KindOf(item));
        Assert.Equal("hello", store.Text());
    }

    [Fact]
    public void InsertingInsideAMergedRun_SplitsIt_WithoutCopyingTheText()
    {
        var store = new ArenaStore();
        foreach (var c in "held") store.InsertChar(store.TextLength, c);
        store.Commit();

        store.InsertChar(2, 'X');
        store.Commit();

        Assert.Equal("heXld", store.Text());
        var left = store.FindItem(store.ClientId, 0);
        var right = store.FindItem(store.ClientId, 2);
        Assert.Equal(2, store.LengthOf(left));
        Assert.Equal(2, store.LengthOf(right));
        Assert.Equal(4, store.ClockOf(store.RightOf(left)));
        Assert.Equal(right, store.RightOf(store.RightOf(left)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void TypingRunsJumpsAndDeletes_MatchAStringModel(int seed)
    {
        // Runs of typing merge at commit; later edits split the merged items again.
        var random = new Random(seed);
        var store = new ArenaStore();
        var model = new StringBuilder();
        for (var round = 0; round < 2_000; round++)
        {
            var cursor = random.Next(model.Length + 1);
            var run = random.Next(1, 12);
            for (var k = 0; k < run; k++)
            {
                var c = (char)('a' + random.Next(26));
                store.InsertChar(cursor + k, c);
                model.Insert(cursor + k, c);
            }

            var deletes = model.Length > 0 ? random.Next(Math.Min(6, model.Length)) : 0;
            var at = model.Length > deletes ? random.Next(model.Length - deletes) : 0;
            for (var k = 0; k < deletes; k++)
            {
                store.DeleteAt(at);
                model.Remove(at, 1);
            }

            store.Commit();
            if (round % 50 == 0) Assert.Equal(model.ToString(), store.Text());
        }

        Assert.Equal(model.ToString(), store.Text());
        Assert.True(store.LiveStructCount < store.Stats.StructCount, "commit merges free slots");
    }

    [Fact]
    public void ArrayPushes_MergeIntoOneStructAcrossBatches_AndReadBack()
    {
        // As in Yjs, where the 1M-push workload ends as one item: each commit merges its first push into the previous run.
        var store = new ArenaStore();
        SpikeWorkloads.PushScalars(store, 2_500);

        Assert.Equal(1, store.LiveStructCount);
        Assert.Equal(2_500, store.Stats.StructCount);
        var values = store.ArrayValues();
        Assert.Equal(2_500, values.Length);
        for (var i = 0; i < values.Length; i++) Assert.Equal(i, values[i]);
    }

    [Fact]
    public void SettingAKeyAgain_DeletesTheOldItem_AndKeepsOnePayloadPerKey()
    {
        var store = new ArenaStore();
        store.SetKey("a", 1);
        store.SetKey("b", 2);
        store.Commit();
        var first = store.CurrentItemOf("a");

        store.SetKey("a", 3);
        store.Commit();

        Assert.True(store.TryGetKey("a", out var a));
        Assert.Equal(3.0, a);
        Assert.True(store.TryGetKey("b", out var b));
        Assert.Equal(2.0, b);
        Assert.False(store.TryGetKey("c", out _));

        var current = store.CurrentItemOf("a");
        Assert.NotEqual(first, current);
        Assert.True(store.IsDeleted(first));
        Assert.Equal(ContentKind.Deleted, store.KindOf(first));
        Assert.Equal(first, store.LeftOf(current));
        Assert.True(store.DeleteSetContains(store.ClientId, store.ClockOf(first)));
    }

    [Fact]
    public void ManyKeys_AllReadBack()
    {
        var store = new ArenaStore();
        SpikeWorkloads.SetScalars(store, 20_000);

        for (var j = 0; j < 20_000; j += 7)
        {
            Assert.True(store.TryGetKey("k" + j, out var value));
            Assert.Equal(j, value);
        }

        Assert.Equal(20_000, store.LiveStructCount);
    }

    [Fact]
    public void AHandleToAMergedAwayItem_IsStale_AndItsSlotIsReusedUnderANewGeneration()
    {
        var store = new ArenaStore();
        store.InsertChar(0, 'a');
        store.InsertChar(1, 'b');
        var right = store.FindItem(store.ClientId, 1);
        Assert.True(store.IsLive(right));

        store.Commit();                                  // 'b' merges into 'a'; its slot goes on the free list

        Assert.False(store.IsLive(right));
        Assert.Throws<InvalidOperationException>(() => store.LengthOf(right));
        Assert.Equal(1, store.FreeSlotCount);

        store.InsertChar(0, 'c');                        // the next new item takes the freed slot
        var reused = store.FindItem(store.ClientId, 2);
        Assert.Equal(right.Index, reused.Index);
        Assert.NotEqual(right.Generation, reused.Generation);
        Assert.False(store.IsLive(right));
        Assert.Equal("cab", store.Text());
    }

    [Fact]
    public void DeletedText_LeavesLengthOnlyItems_AndReleasesTextChunksWithNoLiveCharacter()
    {
        // 40,000 one-character inserts fill one 32,768-character chunk and start a second; deleting all of the first chunk's characters frees it.
        var store = new ArenaStore();
        const int chars = 40_000;
        for (var i = 0; i < chars; i++) store.InsertChar(i, 'q');
        store.Commit();
        var firstItem = store.FindItem(store.ClientId, 0);

        for (var i = 0; i < 32_768; i++) store.DeleteAt(0);
        Assert.Equal(0, store.ReleasedTextChunks);   // payloads are released at commit, as Yjs garbage-collects there
        store.Commit();

        Assert.Equal(1, store.ReleasedTextChunks);
        Assert.Equal(new string('q', chars - 32_768), store.Text());
        var deleted = store.FindItem(store.ClientId, 0);
        Assert.Equal(firstItem, deleted);
        Assert.Equal(ContentKind.Deleted, store.KindOf(deleted));
        Assert.Equal(32_768, store.LengthOf(deleted));   // the deleted range merged into one length-only item
        Assert.Equal(1, store.DeleteRangeCount(store.ClientId));
    }

    [Fact]
    public void DeleteSet_RecordsDeletedClocks_AndMergesAdjacentRanges()
    {
        var store = new ArenaStore();
        foreach (var c in "abcdef") store.InsertChar(store.TextLength, c);
        store.Commit();

        store.DeleteAt(1);   // b, clock 1
        store.DeleteAt(2);   // d, clock 3
        store.Commit();
        Assert.Equal(2, store.DeleteRangeCount(store.ClientId));

        store.DeleteAt(1);   // c, clock 2: joins the two ranges
        store.Commit();

        Assert.Equal("aef", store.Text());
        Assert.Equal(1, store.DeleteRangeCount(store.ClientId));
        Assert.False(store.DeleteSetContains(store.ClientId, 0));
        Assert.True(store.DeleteSetContains(store.ClientId, 1));
        Assert.True(store.DeleteSetContains(store.ClientId, 3));
        Assert.False(store.DeleteSetContains(store.ClientId, 4));
    }

    [Fact]
    public void ClientIds_Are53Bit()
    {
        const ulong max = (1UL << 53) - 1;
        var store = new ArenaStore(max);
        store.InsertChar(0, 'z');
        store.Commit();

        Assert.Equal(max, store.ClientOf(store.FindItem(max, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArenaStore(max + 1));
    }

    [Fact]
    public void EveryChunk_AtOrBelow64KB_AfterSlowTierShapedWork()
    {
        var store = new ArenaStore();
        SpikeWorkloads.Build(store, SpikeWorkloads.TextInsert, 200_000);
        SpikeWorkloads.Build(store, SpikeWorkloads.ArrayPush, 200_000);
        SpikeWorkloads.Build(store, SpikeWorkloads.MapSet, 20_000);
        SpikeWorkloads.SteadyStateEdits(store, 10_000);

        Assert.True(store.MaxChunkBytes > 0);
        Assert.True(store.MaxChunkBytes <= 64 * 1024);
    }

    [Fact]
    public void SearchMarkers_AreCappedAt80()
    {
        var store = new ArenaStore();
        var random = new Random(5);
        for (var i = 0; i < 5_000; i++) store.InsertChar(random.Next(store.TextLength + 1), 'm');

        Assert.InRange(store.TextMarkerCount, 1, 80);
    }

    [Theory]
    [InlineData(SpikeWorkloads.TextInsert, 1.0)]
    [InlineData(SpikeWorkloads.MapSet, 1.0)]
    [InlineData(SpikeWorkloads.ArrayPush, 1.0)]
    public void Harness_RunsEveryWorkload_OnTheArenasCandidate(string workload, double structsPerOp)
    {
        var m = SpikeHarness.MeasureWorkload(ArenaStore.CandidateId, SpikeCandidates.Factory(ArenaStore.CandidateId), workload, 3_000);

        Assert.Equal(ArenaStore.CandidateId, m.Candidate);
        Assert.Equal(structsPerOp, m.StructsPerOp);
        Assert.True(m.OpsPerSecond > 0);
    }

    [Fact]
    public void Harness_RunsTheSteadyStateMix_OnTheArenasCandidate()
    {
        var m = SpikeHarness.MeasureSteadyState(ArenaStore.CandidateId, SpikeCandidates.Factory(ArenaStore.CandidateId), edits: 4_000, warmup: 200,
            startChars: 1_000);

        Assert.Equal(4_000, m.Items);
        Assert.Equal(SpikeWorkloads.SteadyState, m.Workload);
        Assert.True(m.StructsPerOp >= 0.5, "every insert creates a struct; deletes may split one");
    }
}