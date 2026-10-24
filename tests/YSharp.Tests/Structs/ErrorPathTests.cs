using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>
/// Error paths and malformed-input inventory (docs/research/structs.md section L). Most of the JS throw sites are guarded
/// by the callers, so they are asserted as "held pending / ignored instead of failing"; the JS undefined-behaviour sites
/// (TypeError) become defined errors under ADR 0005. Hostile-input breadth belongs to Spec 11.
/// </summary>
[Requires(Feature.Integrate)]
public sealed class ErrorPathTests
{
    private static string Text(Doc d) => d.GetText("t").ToString();

    [Fact]
    [Covers("STR-69", "STR-72")]
    public void TheUnexpectedCaseSites_AreGuardedByTheirCallers_ThePayloadWaitsOrIsIgnored()
    {
        // addStruct clock gap and findIndexSS miss: a struct beyond the known state, and a delete range for an unknown client.
        var gap = Net.Mk(1);
        var gapResult = gap.TryApplyUpdate(Hex.Bytes("0101050504010174017900"));             // client 5, clock 5, first struct for that client
        Assert.True(gapResult.Ok);
        Assert.Empty(StructsProbe.Dump(gap));
        Assert.Equal("0101050504010174017900", Hex.Of(gap.EncodeStateAsUpdate()));                  // kept as pending, not stored

        var unknownDs = Net.Mk(1);
        Assert.True(unknownDs.TryApplyUpdate(Hex.Bytes("000107010001")).Ok);
        Assert.Empty(StructsProbe.Dump(unknownDs));

        // Skip.integrate and GC/Skip.getMissing: Skips are dropped before integration.
        var skip = Net.Mk(1);
        Assert.True(skip.TryApplyUpdate(Hex.Bytes("010205000a0204010174017900")).Ok);
        Assert.DoesNotContain(StructsProbe.All(skip), s => s.Kind == "skip");
    }

    [Fact]
    [Covers("STR-70")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Array, Feature.Map)]
    public void StructsThatCannotBeSplit_NeverNeedToBe_EvenWhenAPeerSyncsFromEveryIntermediateState()
    {
        var a = Net.Mk(1);
        var ups = Net.Capture(a);
        var arr = a.GetArray("arr");
        arr.Push(new YEntry[] { YValue.Bytes(new byte[] { 1 }), YValue.Bytes(new byte[] { 2 }), new YMap() });   // Binary, Binary, Type
        a.GetText("t").Insert(0, "ab", StructModelTests.Attr("b", true));                                         // Format, String, Format
        a.GetText("t").InsertEmbed(1, StructModelTests.Attr("img", 1));                                           // Embed
        arr.Delete(1, 2);

        for (var k = 0; k <= ups.Count; k++)
        {
            var d = Net.Mk(100 + (uint)k);
            for (var i = 0; i < k; i++) d.ApplyUpdate(ups[i]);
            Net.Sync(a, d);                                                                                       // the diff may start anywhere between structs
            Assert.Equal(Hex.Of(a.EncodeStateAsUpdate()), Hex.Of(d.EncodeStateAsUpdate()));
        }
    }

    [Theory]
    [InlineData("01 01 05 00 0b 01 01 74 00")]               // content ref 11: JS indexes an undefined table slot (TypeError)
    [InlineData("01 01 05 00 07 01 01 74 63 00")]            // ContentType with an unknown type ref
    [InlineData("01 01 05 00 04 01 01")]                     // truncated in the middle of a struct
    [InlineData("01 01 05 00 00 03")]                        // delete set missing at the end of the update
    [Covers("STR-71")]
    [Diverges("STR-71", adr: "0005")]
    [Requires(Feature.Text)]
    public void JsUndefinedBehaviourSites_BecomeDefinedErrors_AndTheDocumentIsUntouched(string hex)
    {
        var d = Net.Mk(1);
        d.GetText("t").Insert(0, "x");
        var before = Hex.Of(d.EncodeStateAsUpdate());

        // JS: the pinned Yjs throws a TypeError or RangeError, after integrating whatever came before the bad struct.
        var result = d.TryApplyUpdate(Hex.Bytes(hex.Replace(" ", "")));

        Assert.False(result.Ok);
        Assert.Equal(YError.Malformed, result.Error);
        Assert.Equal(before, Hex.Of(d.EncodeStateAsUpdate()));
        Assert.False(d.IsFaulted);
        Assert.Throws<YDecodeException>(() => d.ApplyUpdate(Hex.Bytes(hex.Replace(" ", ""))));
    }

    [Fact]
    [Covers("STR-71")]
    [Diverges("STR-71", adr: "0005")]
    [Requires(Feature.Text)]
    public void AStructWhoseParentIsNotAType_IsRejected_WhileTheRestOfTheUpdateApplies()
    {
        // JS turns this struct into a GC without complaint; ADR 0005 rejects it (and anything depending on it) and keeps going.
        var d = Net.Mk(1);
        d.GetText("t").Insert(0, "x");

        var result = d.TryApplyUpdate(Hex.Bytes("0101050004000100017900"));
        Assert.Equal(1, result.RejectedStructs);
        Assert.Equal("x", Text(d));
        Assert.False(d.IsFaulted);
        Assert.DoesNotContain(StructsProbe.All(d), s => s.Client == 5);
    }

    [Fact]
    [Covers("STR-72")]
    [Requires(Feature.Text)]
    public void SilentlyAcceptedCases_AnyInput_StayAccepted()
    {
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "ab");
        var update = a.EncodeStateAsUpdate();

        // trailing bytes after the delete set are not checked
        var d = Net.Mk(2);
        var padded = update.Concat(new byte[] { 0xff, 0xff }).ToArray();
        Assert.True(d.TryApplyUpdate(padded).Ok);
        Assert.Equal("ab", Text(d));

        // a struct that is already entirely known is dropped without complaint
        Assert.True(d.TryApplyUpdate(update).Ok);
        Assert.Equal("ab", Text(d));

        // bit 6 (parentSub) is ignored when an origin exists
        var e = Net.Mk(3);
        e.GetText("t").Insert(0, "x");
        Assert.True(e.TryApplyUpdate(Hex.Bytes("01010500a40100017900")).Ok);
        Assert.Equal("xy", Text(e));
    }

    [Fact]
    [Covers("STR-73")]
    [Diverges("STR-73", adr: "0005")]
    [Requires(Feature.Text, Feature.Events)]
    public void AnObserverThatThrows_DoesNotUnwindThroughTheMutation_AndEveryoneElseStillRuns()
    {
        // JS (pinned Yjs): observer 1 throws, observer 2 still runs, the update is emitted, and the FIRST error is rethrown
        // from the mutating call. y-sharp contains the fault instead (ADR 0005, ERR-13/14).
        var a = Net.Mk(1);
        var t = a.GetText("t");
        var log = new List<string>();
        var faults = new List<Exception>();
        a.ObserverFaulted += (fault, _) => faults.Add(fault);
        a.Update += (_, _, _, _) => log.Add("update");
        t.Observe((_, _) => { log.Add("observer 1"); throw new InvalidOperationException("o1"); });
        t.Observe((_, _) => log.Add("observer 2"));

        var thrown = Record.Exception(() => t.Insert(0, "x"));

        Assert.Null(thrown);
        Assert.Equal(new[] { "observer 1", "observer 2", "update" }, log);
        Assert.Equal("x", Text(a));
        Assert.Equal("o1", Assert.Single(faults).Message);
    }
}
