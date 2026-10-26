using System;
using System.Runtime.CompilerServices;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Generators;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// The relay tier as a fuzz target (TASK-5.3 #1): the ADR 0013 byte-mutator and structure-aware layers folded into a <see cref="YRelay"/>
/// instead of a <see cref="Doc"/>, under the default and the tiny limits, with <see cref="RelayHostile"/>'s guard and post-condition.
/// </summary>
[Requires(Feature.Fuzz, Feature.UpdateFunctions, Feature.V2, Feature.V2Functions)]
public sealed class RelayHostileProperties
{
    private static (byte[], UpdateFormat)? Candidate(RawInput i) => (i.Bytes, i.Format);

    private static (byte[], UpdateFormat)? Candidate(ModelInput i) => (i.Bytes, i.Format);

    [Fact, Covers("HOS-MUT", "RLY-4")]
    public void ByteMutator_AnyMutationFoldedIntoARelay_IsContained() =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) => RelayHostile.ApplyAndCheck(i.ToString(), i.Bytes, i.Format), Candidate);

    [Fact, Covers("HOS-MUT", "RLY-7")]
    public void ByteMutator_UnderTinyLimits_IsContained() =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) => RelayHostile.ApplyAndCheck(i + " tiny", i.Bytes, i.Format, Hostile.Tiny), Candidate);

    [Fact, Covers("HOS-MUT", "RLY-4")]
    public void ByteMutator_FoldedIntoARelayWithState_ChangesNothingWhenRejected() =>
        FuzzRunner.Check("HOS-MUT", ByteMutator.Layer(), (RawInput i) =>
        {
            var relay = new YRelay();
            Assert.True(relay.TryApplyUpdate(UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hello"))).Ok);
            RelayHostile.ApplyTo(relay, i.ToString(), i.Bytes, i.Format);
            RelayHostile.AssertPostCondition(relay, i.ToString());
        }, Candidate);

    [Theory, Covers("HOS-CLOCK", "RLY-7")]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_ClockShapes_AreContained(bool tiny) => Models(Profile.Clock, "HOS-CLOCK", tiny);

    [Theory, Covers("HOS-REF")]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_References_AreContained(bool tiny) => Models(Profile.Ref, "HOS-REF", tiny);

    [Theory, Covers("HOS-SEM")]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_MeaninglessContent_IsContained(bool tiny) => Models(Profile.Sem, "HOS-SEM", tiny);

    [Theory, Covers("HOS-LEN")]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_CountClaims_AreContained(bool tiny) => Models(Profile.Len, "HOS-LEN", tiny);

    [Theory, Covers("HOS-AMP", "RLY-7")]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_ManyTinyStructsClientsAndRanges_AreContained(bool tiny) => Models(Profile.Amp, "HOS-AMP", tiny, cases: 200);

    private static void Models(Profile profile, string hosClass, bool tiny, int? cases = null) =>
        FuzzRunner.Check(hosClass, HostileModels.Layer(profile), (ModelInput i) =>
            RelayHostile.ApplyAndCheck(tiny ? i + " tiny" : i.ToString(), i.Bytes, i.Format, tiny ? Hostile.Tiny : null), Candidate, cases);

    // ------------------------------------------------------------------ nothing retained after a rejected update

    [Theory, Covers("RLY-4")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    public void ARejectedUpdate_IsNotRetainedByTheRelay(UpdateFormat format)
    {
        var relay = new YRelay(Hostile.Tiny);
        Assert.True(relay.TryApplyUpdate(UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hello"))).Ok);
        Assert.True(relay.TryApplyUpdate(UpdateBuilder.V1(Waiting(2, 'x'))).Ok);   // 900 pending bytes, under the 1 KB bound
        var input = RejectedInput(relay, format);
        for (var i = 0; i < 3 && input.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(input.IsAlive, "the relay keeps a rejected update's bytes alive");
        RelayHostile.AssertPostCondition(relay, "after the rejected updates");
    }

    /// <summary>Folds in an update over the tiny pending bound, and a malformed one, from a buffer only a weak reference holds afterwards.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectedInput(YRelay relay, UpdateFormat format)
    {
        // another 900 characters after a gap: past the 1 KB pending bound
        var model = Waiting(3, 'y');
        var bytes = format == UpdateFormat.V2 ? UpdateBuilder.V2(model) : UpdateBuilder.V1(model);
        Assert.Equal(YError.LimitExceeded, relay.TryApplyUpdate(bytes, format).Error);
        Assert.Equal(YError.LimitExceeded, relay.TryPrepareUpdate(bytes, format, out var prepared).Error);
        Assert.Null(prepared);
        var truncated = bytes[..^3];
        Assert.Equal(YError.Malformed, relay.TryApplyUpdate(truncated, format).Error);
        return new WeakReference(bytes);
    }

    /// <summary>900 characters from <paramref name="client"/> at clock 5: the client's clocks 0 to 4 are missing, so all of it is pending.</summary>
    private static UpdateModel Waiting(ulong client, char c) => UpdateModel.Single(client, 5, Struct.InRoot("t", Content.Str(new string(c, 900))));
}
