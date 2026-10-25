using System;
using System.Buffers;
using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Allocation;

/// <summary>
/// The eleven hot-path allocation tests of ADR 0009 (BUD-01..11). They run in the serial "Allocation" collection, in Release only
/// (the helper skips Debug visibly), and assert exact counters from <c>GC.GetAllocatedBytesForCurrentThread</c>, never time.
/// Zero paths stay at 0; bounded paths are pending until the implementation records a measured value (<see cref="AllocationBudgets"/>).
/// </summary>
[Requires(Feature.Transactions)]
[Collection("Allocation")]
[Tier(Tier.Gating)]
public sealed class HotPathBudgetTests
{
    private const int Iterations = 200;
    private const int Warmup = 50;

    [Fact, Covers("BUD-01")]
    public void Transact_WithStaticLambda_StaysWithinItsBudget()
    {
        var doc = new Doc();
        AllocationBudgets.Check("BUD-01", "Transact", () => doc.Transact(0, static (_, _) => { }));
    }

    [Fact, Covers("BUD-01")]
    [Requires(Feature.Text, Feature.Array, Feature.Map)]
    public void ReadTransact_WithStaticLambda_StaysWithinItsBudget()
    {
        var doc = AllocDocs.Populated();
        AllocationBudgets.Check("BUD-01", "ReadTransact", () => doc.ReadTransact(static _ => { }));
    }

    [Fact, Covers("BUD-02")]
    public void TryTransact_Uncontended_StaysWithinItsBudget()
    {
        var doc = new Doc();
        AllocationBudgets.Check("BUD-02", "Uncontended", () => doc.TryTransact(static _ => { }));
    }

    [Fact, Covers("BUD-02")]
    public void TryTransact_Contended_AllocatesNothing()
    {
        var doc = new Doc();
        using var holder = new HeldWriter(doc);
        var acquired = false;
        AllocationBudgets.Check("BUD-02", "Contended", () => acquired |= doc.TryTransact(static _ => { }).Acquired);
        Assert.False(acquired, "the writer should have kept TryTransact from acquiring");
    }

    [Fact, Covers("BUD-04")]
    [Requires(Feature.Map, Feature.Events)]
    public void EventDispatch_WithNoHandlersForTheChangedType_AllocatesNothing()
    {
        var doc = AllocDocs.Populated();
        var observed = doc.GetMap("observed");
        observed.Observe(static (_, _) => { });   // handlers exist, but not on the type that changes
        var map = doc.GetMap("m");
        var value = (YEntry)1.0;
        AllocationBudgets.Check("BUD-04", "", () => map.Set("k0", value));
    }

    [Fact, Covers("BUD-05")]
    [Requires(Feature.Text, Feature.Events)]
    public void Commit_WithNoUpdateSubscriber_AllocatesNothing()
    {
        var doc = AllocDocs.Populated();
        var text = doc.GetText("t");
        AllocationBudgets.Check("BUD-05", "", () => doc.Transact(text, static (t, _) =>
        {
            t.Insert(t.Length, "x");
            t.Delete(t.Length - 1, 1);
        }));
    }

    [Fact, Covers("BUD-06")]
    [Requires(Feature.V1, Feature.Text, Feature.Array, Feature.Map)]
    public void EncodeStateAsUpdate_IntoPreSizedWriter_AllocatesNothing()
    {
        var doc = AllocDocs.Populated();
        var writer = Writers.PreSized();
        AllocationBudgets.Check("BUD-06", "", () =>
        {
            writer.Clear();
            doc.EncodeStateAsUpdate(writer);
        });
        Assert.True(writer.WrittenCount > 0);
    }

    [Fact, Covers("BUD-07")]
    [Requires(Feature.Text, Feature.Events)]
    public void OneCharacterTextInsert_AtTheEnd_AllocatesNothing()
    {
        var doc = AllocDocs.Populated();
        var text = doc.GetText("t");
        AllocationBudgets.Check("BUD-07", "", () => text.Insert(text.Length, "x"));
    }

    [Theory, Covers("BUD-08")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    [Requires(Feature.V2, Feature.Text, Feature.Events)]
    public void TryApplyUpdate_HotPath_StaysWithinItsBudget(UpdateFormat format)
    {
        // Each call applies a new update to the target; the stream is long enough for warm-up, measurement and a diagnostic rerun's start.
        var updates = AllocDocs.UpdateStream(Iterations + Warmup + 8, format);
        var target = new Doc();
        var next = 0;
        AllocationBudgets.Check("BUD-08", format.ToString(), () =>
        {
            var result = target.TryApplyUpdate(updates[next++ % updates.Length], null, format);
            if (!result.Ok) throw new InvalidOperationException("the update was rejected: " + result.Error);
        });
    }

    [Fact, Covers("BUD-09")]
    [Requires(Feature.Array)]
    public void Push_OfAScalar_StaysWithinItsBudget()
    {
        var doc = new Doc();
        var arr = doc.GetArray("a");
        var one = new YEntry[] { 1.0 };
        AllocationBudgets.Check("BUD-09", "Push", () => arr.Push(one));
    }

    [Fact, Covers("BUD-09")]
    [Requires(Feature.Map)]
    public void Set_OfAScalar_StaysWithinItsBudget()
    {
        var doc = new Doc();
        var map = doc.GetMap("m");
        var value = (YEntry)1.0;
        AllocationBudgets.Check("BUD-09", "Set", () => map.Set("key", value));
    }

    [Fact, Covers("BUD-10")]
    [Requires(Feature.Sync)]
    public void SyncStep1Step2RoundTrip_HotPath_StaysWithinItsBudget()
    {
        var a = AllocDocs.Populated(clientId: 1);
        var b = AllocDocs.Populated(clientId: 2, chars: 50);
        var request = Writers.PreSized();
        var reply = Writers.PreSized();
        var sink = Writers.PreSized();
        AllocationBudgets.Check("BUD-10", "", () =>
        {
            request.Clear();
            reply.Clear();
            sink.Clear();
            YSync.WriteStep1(request, a);
            ReadOnlySpan<byte> input = request.WrittenSpan;
            YSync.TryReadMessage(ref input, reply, b, null, out _);   // Step1 at b appends the Step2 reply
            ReadOnlySpan<byte> step2 = reply.WrittenSpan;
            YSync.TryReadMessage(ref step2, sink, a, null, out _);    // Step2 at a
        });
    }

    [Fact, Covers("BUD-11")]
    [Requires(Feature.Awareness)]
    public void AwarenessEncode_HotPath_StaysWithinItsBudget()
    {
        using var awareness = new Awareness(new Doc());
        awareness.SetLocalState(1);
        var writer = Writers.PreSized(4096);
        AllocationBudgets.Check("BUD-11", "Encode", () =>
        {
            writer.Clear();
            awareness.EncodeUpdate(writer);
        });
    }

    [Fact, Covers("BUD-11")]
    [Requires(Feature.Awareness)]
    public void AwarenessApply_HotPath_StaysWithinItsBudget()
    {
        // A stream of updates with increasing clocks from one peer, so every apply takes the update path rather than being ignored.
        using var source = new Awareness(new Doc(new DocOptions { ClientId = 9 }));
        var updates = new byte[Iterations + Warmup + 8][];
        for (var i = 0; i < updates.Length; i++)
        {
            source.SetLocalState(i);
            var w = new ArrayBufferWriter<byte>();
            source.EncodeUpdate(w);
            updates[i] = w.WrittenSpan.ToArray();
        }

        using var target = new Awareness(new Doc());
        var next = 0;
        AllocationBudgets.Check("BUD-11", "Apply", () =>
        {
            var result = target.TryApplyUpdate(updates[next++ % updates.Length], null);
            if (!result.Ok) throw new InvalidOperationException("the awareness update was rejected: " + result.Error);
        });
    }
}
