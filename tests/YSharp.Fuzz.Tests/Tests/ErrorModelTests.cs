using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.Fuzz.Builder;
using YSharp.Fuzz.Guards;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// ADR 0005 (typed, non-throwing error model with contained failures), one test group per ERR entry. The fixed corpus and the FsCheck
/// layers carry ERR-21 ("no input ever faults a document"); the limit entries ERR-08 to ERR-10 and ERR-12 are in <see cref="LimitsTests"/>
/// and the document-depth entry ERR-11 in <see cref="CanaryTests"/>.
/// </summary>
[Requires(Feature.Integrate)]
public sealed class ErrorModelTests
{
    private static readonly byte[] HiV1 = UpdateBuilder.V1(UpdateBuilder.TextInsert(1, "t", "hi"));

    private static byte[] Unterminated => Hex.Bytes("80 80 80 80 80 80 80 80 80 80 00");

    // ------------------------------------------------------------------ ERR-01, ERR-02

    [Fact, Covers("ERR-01", Layer = CoverLayer.Unit)]
    public void Err01_TryApplyUpdate_ReturnsATypedResult_AndApplyUpdateThrowsTheSameCode()
    {
        var doc = new Doc();
        var result = doc.TryApplyUpdate(Unterminated);
        Assert.False(result.Ok);
        Assert.Equal(YError.Malformed, result.Error);

        var thrown = Assert.Throws<YDecodeException>(() => new Doc().ApplyUpdate(Unterminated));
        Assert.Equal(result.Error, thrown.Error);

        var good = new Doc();
        Assert.True(good.TryApplyUpdate(HiV1).Ok);
        new Doc().ApplyUpdate(HiV1);   // the throwing form does not throw on success
    }

    [Fact, Covers("ERR-02", Layer = CoverLayer.Unit)]
    [LibraryIndependent]
    public void Err02_ErrorsAreCodes_NoMessagesAreExposedOrCompared()
    {
        // the result types carry codes and counts only; a message would invite comparing JS error text (ADR 0003)
        Assert.DoesNotContain(typeof(ApplyResult).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.PropertyType == typeof(string));
        Assert.DoesNotContain(typeof(YStatus).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.PropertyType == typeof(string));
        Assert.Equal(typeof(YError), typeof(YDecodeException).GetProperty(nameof(YDecodeException.Error))!.PropertyType);
        foreach (var code in new[] { "None", "Malformed", "LimitExceeded", "Rejected", "UnknownMessage" })
            Assert.True(Enum.IsDefined(typeof(YError), code), $"YError.{code} is missing");
    }

    [Fact, Covers("ERR-02", Layer = CoverLayer.Unit)]
    public void Err02_DifferentJsFailureKinds_CollapseIntoTheSameCode()
    {
        // JS raises Error, RangeError or TypeError for these; y-sharp reports one code and the tests never look at anything else
        var inputs = new[]
        {
            Unterminated,
            Hex.Bytes("01 80 80 80 80 10 01 00"),                          // oversized count (RangeError in JS)
            Hex.Bytes("01 01 01 00 0b 01 01 74 00"),                       // unknown content ref (Error in JS)
            Hex.Bytes("01 01 01 00 04 01 01 74 01 ff 00"),                 // invalid UTF-8
            HiV1[..5],
        };
        foreach (var input in inputs)
        {
            var result = new Doc().TryApplyUpdate(input);
            Assert.False(result.Ok);
            Assert.True(result.Error is YError.Malformed or YError.LimitExceeded, $"{Hex.Of(input)} gave {result.Error}");
        }
    }

    // ------------------------------------------------------------------ ERR-03, ERR-04, ERR-05, ERR-06, ERR-07

    [Fact, Covers("ERR-03", Layer = CoverLayer.Unit)]
    [Requires(Feature.V2)]
    public void Err03_EofIsAlwaysADecodeError_VarintAtEofAndExhaustedV2Columns()
    {
        foreach (var hex in new[] { "", "01", "01 01", "01 01 01", "01 01 01 00 04" })
            Assert.Equal(YError.Malformed, new Doc().TryApplyUpdate(Hex.Bytes(hex)).Error);

        // V2: the update announces one struct but its info column is empty. JS reads a missing info byte as 0 (a GC of length 0) and accepts.
        var s = new ByteSink().VarUint(0);
        for (var col = 0; col < 9; col++) s.VarBytes(col == 5 ? new byte[] { 0 } : Array.Empty<byte>());
        s.Bytes(Hex.Bytes("01 01 00 00"));
        var v2 = new Doc().TryApplyUpdate(s.ToArray(), null, UpdateFormat.V2);
        Assert.False(v2.Ok);
        Assert.Equal(YError.Malformed, v2.Error);
    }

    [Fact, Covers("ERR-04", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err04_TrailingBytes_DuplicateClientsAndUnsortedInput_StayAccepted()
    {
        var trailing = new Doc();
        Assert.True(trailing.TryApplyUpdate(HiV1.Concat(new byte[] { 0xff, 0xff }).ToArray()).Ok);
        Assert.Equal("hi", trailing.GetText("t").ToString());

        // two blocks of client 1: the later one replaces the earlier (last wins)
        var dup = UpdateModel.Of(new ClientBlock(1, 0, new[] { Struct.InRoot("t", Content.Str("a")) }), new ClientBlock(1, 0, new[] { Struct.InRoot("t", Content.Str("bb")) }));
        var dupDoc = new Doc();
        Assert.True(dupDoc.TryApplyUpdate(UpdateBuilder.V1(dup)).Ok);
        Assert.Equal("bb", dupDoc.GetText("t").ToString());

        // clients in ascending order (the writer emits descending) and an unsorted delete set
        var unsorted = UpdateBuilder.V1(UpdateModel.Of(
                new ClientBlock(1, 0, new[] { Struct.InRoot("u", Content.Str("a")) }),
                new ClientBlock(2, 0, new[] { Struct.InRoot("v", Content.Str("b")) }))
            .WithDeletes(new DeleteClient(1, new[] { new DeleteRange(5, 1), new DeleteRange(0, 1) }), new DeleteClient(9, new[] { new DeleteRange(0, 1) })));
        var unsortedDoc = new Doc();
        Assert.True(unsortedDoc.TryApplyUpdate(unsorted).Ok);
        Assert.Equal("", unsortedDoc.GetText("u").ToString());   // the delete range (0,1) removed the only character
        Assert.Equal("b", unsortedDoc.GetText("v").ToString());
    }

    [Fact, Covers("ERR-05", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err05_ADecodeFailure_AnywhereInTheUpdate_ChangesNothing()
    {
        // a good struct followed by a truncated delete set: JS integrates the struct first, y-sharp validates everything first
        var truncatedDs = HiV1[..^1].Concat(new byte[] { 1 }).ToArray();
        var doc = new Doc();
        var result = doc.TryApplyUpdate(truncatedDs);
        Assert.Equal(YError.Malformed, result.Error);
        Assert.Equal("", doc.GetText("t").ToString());
        Assert.Empty(doc.GetStateVector().Clients);

        // a good block of client 2 followed by a block of client 1 with an invalid content ref
        var badSecond = UpdateBuilder.V1(UpdateModel.Of(
            new ClientBlock(2, 0, new[] { Struct.InRoot("t", Content.Str("ok")) }),
            new ClientBlock(1, 0, new[] { new Struct(StructKind.Item, Content: Content.Str("x"), RootKey: "t", InfoOverride: 0x0b) })));
        var other = new Doc();
        Assert.Equal(YError.Malformed, other.TryApplyUpdate(badSecond).Error);
        Assert.Equal("", other.GetText("t").ToString());
        Assert.Empty(other.GetStateVector().Clients);
    }

    [Fact, Covers("ERR-06", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err06_PendingStructs_AreRetainedState_NotFailure()
    {
        var waiting = UpdateBuilder.V1(UpdateModel.Single(2, 0, Struct.InRoot("t", Content.Str("!"), origin: new Id(1, 1))));
        var doc = new Doc();
        var result = doc.TryApplyUpdate(waiting);
        Assert.True(result.Ok, $"a struct waiting for its origin is not an error ({result.Error})");
        Assert.Equal(0, result.RejectedStructs);
        Assert.Empty(doc.GetStateVector().Clients);
        Assert.Equal("", doc.GetText("t").ToString());

        Assert.True(doc.TryApplyUpdate(HiV1).Ok);   // the origin (1,1) arrives: the waiting struct integrates after it
        Assert.Equal("hi!", doc.GetText("t").ToString());
        Assert.Equal(1, doc.GetStateVector()[2]);
    }

    [Fact, Covers("ERR-07", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err07_AStructThatCannotBeIntegrated_IsRejectedWithItsDependents_AndTheRestApplies()
    {
        var doc = new Doc();
        Assert.True(doc.TryApplyUpdate(HiV1).Ok);
        // client 2: a string item whose parent is the string item (1,0), not a type; then an item that depends on it. Client 3 is fine.
        var update = UpdateBuilder.V1(UpdateModel.Of(
            new ClientBlock(2, 0, new[]
            {
                Struct.InItem(new Id(1, 0), Content.Str("x")),
                Struct.InRoot("t", Content.Str("y"), origin: new Id(2, 0)),
            }),
            new ClientBlock(3, 0, new[] { Struct.InRoot("t", Content.Str("ok")) })));

        var result = doc.TryApplyUpdate(update);
        Assert.False(result.Ok);
        Assert.Equal(YError.Rejected, result.Error);
        Assert.Equal(2, result.RejectedStructs);
        var text = doc.GetText("t").ToString();
        Assert.Contains("ok", text, StringComparison.Ordinal);
        Assert.Contains("hi", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("y", text, StringComparison.Ordinal);
        Assert.False(doc.IsFaulted);

        // never retried: the same update again rejects the same two structs and changes nothing
        var before = doc.EncodeStateAsUpdate();
        var again = doc.TryApplyUpdate(update);
        Assert.Equal(YError.Rejected, again.Error);
        Assert.Equal(2, again.RejectedStructs);
        Assert.Equal(before, doc.EncodeStateAsUpdate());
    }

    // ------------------------------------------------------------------ ERR-13, ERR-14, ERR-15, ERR-19

    [Fact, Covers("ERR-13", "ERR-14", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Events)]
    public void Err13_Err14_ObserverFaults_AreContained_AndReportedNotThrown()
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        var ran = new List<string>();
        var faults = new List<Exception>();
        var updates = 0;
        text.Observe((_, _) => { ran.Add("first"); throw new InvalidOperationException("observer bug"); });
        text.Observe((_, _) => ran.Add("second"));
        doc.ObserverFaulted += (fault, _) => faults.Add(fault);
        doc.Update += (_, _, _, _) => updates++;

        var result = doc.TryApplyUpdate(HiV1);                       // does not throw
        Assert.True(result.Ok);
        Assert.Equal(1, result.ObserverFaults);
        Assert.Equal(new[] { "first", "second" }, ran);              // every handler ran
        Assert.Equal("hi", text.ToString());                         // the data is committed
        Assert.Equal(1, updates);                                    // and the update event was emitted
        Assert.Single(faults);
        Assert.IsType<InvalidOperationException>(faults[0]);

        // the throwing entry point and local mutations contain the fault too
        var other = new Doc();
        var otherText = other.GetText("t");
        otherText.Observe((_, _) => throw new InvalidOperationException("again"));
        other.ApplyUpdate(HiV1);
        otherText.Insert(2, "!");
        other.Transact(_ => otherText.Insert(0, ">"));
        Assert.Equal(">hi!", otherText.ToString());
        Assert.False(other.IsFaulted);
    }

    [Fact, Covers("ERR-15", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err15_ExceptionsFromTheCallersTransactBody_Propagate()
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        var thrown = Assert.Throws<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "kept");
            throw new InvalidOperationException("the caller's own bug");
        }));
        Assert.Equal("the caller's own bug", thrown.Message);
        Assert.False(doc.IsFaulted);
        text.Insert(0, "after");                                     // the document and the lock are usable again
        Assert.Contains("after", text.ToString(), StringComparison.Ordinal);
    }

    [Fact, Covers("ERR-19", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Events)]
    public void Err19_OutOfMemory_IsNeverCaught()
    {
        var doc = new Doc();
        var faulted = 0;
        doc.GetText("t").Observe((_, _) => throw new OutOfMemoryException("simulated environment fault"));
        doc.ObserverFaulted += (_, _) => faulted++;
        Assert.Throws<OutOfMemoryException>(() => doc.TryApplyUpdate(HiV1));
        Assert.Equal(0, faulted);   // not reported as an observer fault: it is not the observer's bug
    }

    // ------------------------------------------------------------------ ERR-16, ERR-17, ERR-18, ERR-20

    [Fact, Covers("ERR-16", Layer = CoverLayer.Unit)]
    [LibraryIndependent]
    public void Err16_ProgrammerErrors_UseAFamilySeparateFromDecodeErrors()
    {
        foreach (var type in new[] { typeof(YConcurrencyException), typeof(YLifetimeException), typeof(YInternalException), typeof(YTypeConflictException) })
        {
            Assert.True(type.IsAssignableTo(typeof(YException)), $"{type.Name} is not a YException");
            Assert.False(type.IsAssignableTo(typeof(YDecodeException)), $"{type.Name} must not be a decode error");
        }
        Assert.True(typeof(YDecodeException).IsAssignableTo(typeof(YException)));
        Assert.False(typeof(YDecodeException).IsAssignableTo(typeof(YTypeConflictException)));
    }

    [Fact, Covers("ERR-16", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Map)]
    public void Err16_ATypeConflict_IsAProgrammerError_NotADecodeError()
    {
        var doc = new Doc();
        doc.GetMap("x");
        var e = Assert.ThrowsAny<Exception>(() => doc.GetText("x"));
        Assert.IsType<YTypeConflictException>(e);
        Assert.IsNotType<YDecodeException>(e);
    }

    [Fact, Covers("ERR-17", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Array)]
    public void Err17_JsDefinedLocalBehaviour_ArrayOverflowThrows_TextClamps()
    {
        var doc = new Doc();
        var array = doc.GetArray("a");
        array.Push(new YEntry[] { 1, 2 });
        var overflow = Assert.ThrowsAny<Exception>(() => array.Insert(5, new YEntry[] { 3 }));
        Assert.IsNotType<YDecodeException>(overflow);
        Assert.Equal(2, array.Length);

        var text = doc.GetText("t");
        text.Insert(100, "end");                                      // clamps to the end, as JS does
        Assert.Equal("end", text.ToString());
        text.Insert(100, "!");
        Assert.Equal("end!", text.ToString());
    }

    [Fact, Covers("ERR-18", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text, Feature.Array)]
    public void Err18_UndefinedLocalCases_NegativeIndexes_ThrowArgumentOutOfRange()
    {
        var doc = new Doc();
        var array = doc.GetArray("a");
        array.Push(new YEntry[] { 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Insert(-1, new YEntry[] { 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = array[-1]; });
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Delete(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetText("t").Insert(-1, "x"));
        Assert.Equal(1, array.Length);
    }

    [Fact, Covers("ERR-20", Layer = CoverLayer.Unit)]
    public void Err20_TheInternalFaultFamily_Exists_AndANewDocumentIsNotFaulted()
    {
        Assert.True(typeof(YInternalException).IsAssignableTo(typeof(YException)));
        Assert.False(new Doc().IsFaulted);
    }

    [Fact, Covers("ERR-20", Layer = CoverLayer.Unit)]
    [Requires(Feature.Text)]
    public void Err20_AViolatedInvariant_ThrowsYInternalException_AndFaultsTheDocument_ItStaysReadable()
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        text.Insert(0, "hi");
        doc.InjectInvariantViolation();   // the internal fault hook: no public input can do this (ERR-21)
        Assert.False(doc.IsFaulted);      // detected by the next operation that touches the store, not by the hook

        Assert.Throws<YInternalException>(() => text.Insert(2, "!"));
        Assert.True(doc.IsFaulted);

        // readable
        Assert.Equal("hi", text.ToString());
        Assert.NotEmpty(doc.EncodeStateAsUpdate());
        Assert.Equal(2, doc.GetStateVector()[doc.ClientId]);
        // refuses further mutation and apply, and changes nothing
        Assert.Throws<YInternalException>(() => text.Insert(0, "x"));
        Assert.Throws<YInternalException>(() => doc.ApplyUpdate(HiV1));
        Assert.Equal(YError.Rejected, doc.TryApplyUpdate(HiV1).Error);
        Assert.Equal("hi", text.ToString());
        Assert.True(doc.IsFaulted);
    }
}
