using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.Integration;
using YSharp.Store;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>
/// Item integration, pending structs, rejection and faulted documents (TASK-4.13) through the integrator's internal interface: decoded struct
/// refs go in, the struct store and the pending state come out. No codec and no shared type is needed. Expected values are worked by hand from
/// Yjs v13.6.33 (<c>Item.getMissing</c>, <c>Item.integrate</c>, <c>integrateStructs</c>, <c>readUpdateV2</c>, <c>readAndApplyDeleteSet</c>)
/// and from ADR 0005 for the y-sharp-own rejection and fault rules.
/// </summary>
[Requires(Feature.Integrate)]
public sealed class IntegrationEngineTests
{
    // ------------------------------------------------------------------ helpers

    private static StructRef Str(ulong client, long clock, string text, (ulong, long)? origin = null, (ulong, long)? right = null,
        string? root = "t")
    {
        var r = StructRef.String(client, clock, text);
        if (origin is { } o) r.Origin = new ItemId(o.Item1, o.Item2);
        if (right is { } ro) r.RightOrigin = new ItemId(ro.Item1, ro.Item2);
        if (origin is null && right is null) r.ParentRoot = root;
        return r;
    }

    private static UpdateRefs Update(params StructRef[] refs)
    {
        var u = new UpdateRefs();
        foreach (var g in refs.GroupBy(r => r.Client)) u.SetClient(g.Key, g.ToList());
        return u;
    }

    private static Doc Mk(ulong id = 100) => new(new DocOptions { ClientId = id });

    /// <summary>The visible text of root <paramref name="name"/>: the undeleted string items, left to right.</summary>
    private static string Text(Doc doc, string name = "t")
    {
        var sb = new StringBuilder();
        for (var i = new ItemRef(doc.RootBranch(name).Start); !i.IsNull; i = i.Right)
            if (!i.IsDeleted && i.Kind == ContentKind.String) doc.Store.AppendString(sb, i);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ integration

    [Fact]
    [Covers("STR-23")]
    public void ARootItem_IsLinkedIntoItsRootType_AndAdvancesTheState()
    {
        var d = Mk();
        var rejected = d.IntegrateRemote(Update(Str(1, 0, "ab")));

        Assert.Equal(0, rejected);
        Assert.Equal("ab", Text(d));
        Assert.Equal(2, d.Store.GetState(1));
        Assert.Equal(2, d.RootBranch("t").Length);
    }

    [Fact]
    [Covers("STR-13", "STR-62", "STR-64")]
    public void MissingDependency_IsLookedForInOrigin_ThenRightOrigin_AndTheStructWaitsUntilBothArrive()
    {
        var a = Update(Str(1, 0, "A"));
        var c = Update(Str(3, 0, "C"));
        StructRef B() => Str(2, 0, "B", origin: (1, 0), right: (3, 0));

        var d = Mk();
        d.IntegrateRemote(Update(B()));
        Assert.Equal("", Text(d));
        Assert.Equal(new[] { new KeyValuePair<ulong, long>(1, 0) }, d.Pending!.Missing);   // origin's client first
        d.IntegrateRemote(a);
        Assert.Equal("A", Text(d));
        Assert.Equal(0, d.Store.GetState(2));
        Assert.Equal(0L, d.Pending!.Missing[3]);                                           // retried, now waiting for the right origin
        d.IntegrateRemote(c);
        Assert.Equal("ABC", Text(d));
        Assert.Null(d.Pending);

        var e = Mk();
        e.IntegrateRemote(Update(B()));
        e.IntegrateRemote(Update(Str(3, 0, "C")));
        Assert.Equal("C", Text(e));
        Assert.Equal(0, e.Store.GetState(2));
        e.IntegrateRemote(Update(Str(1, 0, "A")));
        Assert.Equal("ABC", Text(e));
        Assert.Null(e.Pending);
    }

    [Fact]
    [Covers("STR-14")]
    public void ResolvingAnOriginInsideAStruct_SplitsIt_AfterTheOriginAndBeforeTheRightOrigin()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "abc")));
        d.IntegrateRemote(Update(Str(2, 0, "X", origin: (1, 0), right: (1, 1))));

        Assert.Equal(new[] { (0L, 1L), (1L, 2L) }, Structs(d, 1).Select(s => (s.Clock, s.Length)));
        var x = Assert.Single(Structs(d, 2));
        Assert.Equal(new ItemId(1, 0), d.Store.OriginOf(x));
        Assert.Equal(new ItemId(1, 1), d.Store.RightOriginOf(x));
        Assert.Equal("aXbc", Text(d));
    }

    [Fact]
    [Covers("STR-15", "STR-21")]
    public void AnItemWithAnOrigin_InheritsParentAndKey_FromItsLeftNeighbour_AndReplacesTheKeysValue()
    {
        var d = Mk();
        var first = MapEntry(1, 0, "m", "k", 1);
        var second = StructRef.Any(1, 1, new YValue[] { 2 });
        second.Origin = new ItemId(1, 0);
        d.IntegrateRemote(Update(first, second));

        var s = Structs(d, 1);
        Assert.All(s, x => Assert.Same(d.RootBranch("m"), x.Parent));
        Assert.Equal(new[] { "k", "k" }, s.Select(x => d.Store.ParentSubOf(x)));
        Assert.Equal(new[] { true, false }, s.Select(x => x.IsDeleted));                  // the replaced value is deleted at once
        Assert.Equal(s[1], new ItemRef(d.RootBranch("m").Map!.Get("k")));
        Assert.Equal(0, d.RootBranch("m").Length);                                         // map entries never count
    }

    [Fact]
    [Covers("STR-17")]
    public void APartiallyKnownStruct_IntegratesOnlyItsUnseenSuffix_WithTheLastKnownIdAsOrigin()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "he")));
        d.IntegrateRemote(Update(Str(1, 0, "hello")));

        Assert.Equal("hello", Text(d));
        // "llo" integrates with origin (1,1), the last known id, so it is the right neighbour of "he" and the commit merges the two
        // (oracle, Yjs 13.6.33: one ContentString struct (0,5) after applying "he" then "hello")
        var s = Assert.Single(Structs(d, 1));
        Assert.Equal((0L, 5L), (s.Clock, s.Length));
        Assert.Equal("hello", d.Store.GetString(s));

        d.IntegrateRemote(Update(Str(1, 0, "hello")));                                     // entirely known: dropped
        Assert.Equal("hello", Text(d));
        Assert.Single(Structs(d, 1));
    }

    [Theory]
    [InlineData(1UL, 2UL, "aABb")]
    [InlineData(2UL, 1UL, "aBAb")]
    [Covers("STR-18", "STR-19", "STR-20")]
    public void SameOrigin_TheLowerClientIdGoesLeft_WhateverTheDeliveryOrder(ulong idA, ulong idB, string expected)
    {
        foreach (var aFirst in new[] { true, false })
        {
            var d = Mk();
            d.IntegrateRemote(Update(Str(9, 0, "ab")));
            var ua = Update(Str(idA, 0, "A", origin: (9, 0), right: (9, 1)));
            var ub = Update(Str(idB, 0, "B", origin: (9, 0), right: (9, 1)));
            d.IntegrateRemote(aFirst ? ua : ub);
            d.IntegrateRemote(aFirst ? ub : ua);
            Assert.Equal(expected, Text(d));
        }
    }

    [Fact]
    [Covers("STR-18", "STR-19", "STR-20")]
    public void NullOrigins_ConflictAtTheStart_AndResolveByClientId()
    {
        foreach (var order in Permutations(new ulong[] { 5, 3, 4 }))
        {
            var d = Mk();
            foreach (var client in order) d.IntegrateRemote(Update(Str(client, 0, client.ToString())));
            Assert.Equal("345", Text(d));
        }
    }

    [Fact]
    [Covers("STR-18", "STR-19", "STR-20", "STR-23")]
    public void ConflictZone_WithItemsInsideItFromTwoClients_ConvergesUnderEveryDeliveryOrder()
    {
        // client 1 wrote "ab"; clients 1, 2 and 3 each insert two characters between a and b; 2 and 3 then type inside their own insert
        var updates = new[]
        {
            Str(1, 2, "AA", origin: (1, 0), right: (1, 1)),
            Str(2, 0, "BB", origin: (1, 0), right: (1, 1)),
            Str(2, 2, "x", origin: (2, 1), right: (1, 1)),
            Str(3, 0, "CC", origin: (1, 0), right: (1, 1)),
            Str(3, 2, "y", origin: (3, 0), right: (3, 1)),
        };

        var results = new HashSet<string>();
        foreach (var order in Permutations(updates))
        {
            var d = Mk();
            d.IntegrateRemote(Update(Str(1, 0, "ab")));
            foreach (var u in order) d.IntegrateRemote(Update(u));
            Assert.Null(d.Pending);
            results.Add(Text(d));
        }

        Assert.Equal(new[] { "aAABBxCyCb" }, results);
    }

    [Fact]
    [Covers("STR-21", "STR-23")]
    public void MapKeyRace_TheYataWinnerBecomesTheValue_AndEveryLoserIsStoredDeleted()
    {
        StructRef A1() => MapEntry(1, 0, "m", "k", "a1");
        StructRef A2()
        {
            var r = StructRef.Any(1, 1, new YValue[] { "a2" });
            r.Origin = new ItemId(1, 0);
            return r;
        }

        foreach (var aFirst in new[] { true, false })
        {
            var d = Mk();
            var ua = Update(A1(), A2());
            var ub = Update(MapEntry(2, 0, "m", "k", "b1"));
            d.IntegrateRemote(aFirst ? ua : ub);
            d.IntegrateRemote(aFirst ? ub : ua);

            var current = new ItemRef(d.RootBranch("m").Map!.Get("k"));
            Assert.Equal(new ItemId(2, 0), d.Store.IdOf(current));
            Assert.Equal("b1", d.Store.GetValue(current, 0).AsString());
            Assert.All(Structs(d, 1), s => Assert.True(s.IsDeleted));
            Assert.False(current.IsDeleted);
        }
    }

    [Fact]
    [Covers("STR-23", "STR-24")]
    public void AnItemIntoADeletedParent_IsDeletedAtOnce_OrBecomesAGcWhenTheParentsContentWasCollected()
    {
        foreach (var collected in new[] { false, true })
        {
            // with GC on the commit would collect the deleted type at once (oracle: the late child is then a GC struct), so the
            // uncollected case runs with GC off and the collected case collects it by hand
            var d = new Doc(new DocOptions { ClientId = 100, Gc = false });
            var nested = new Branch();
            var typeItem = StructRef.OfPayload(1, 0, ContentKind.Type, nested);
            typeItem.ParentRoot = "arr";
            var deleteIt = Update(typeItem);
            deleteIt.AddDelete(1, 0, 1);
            d.IntegrateRemote(deleteIt);
            if (collected) d.Transact(_ => d.Store.GcContent(Structs(d, 1)[0]));

            var late = MapEntry(2, 0, null, "k", "late");
            late.ParentId = new ItemId(1, 0);
            var deletedInTx = false;
            d.Transact(tx =>
            {
                d.IntegrateUpdate(tx, Update(late));
                deletedInTx = tx.DeleteSet.Contains(new ItemId(2, 0));
            });

            Assert.Equal(1, d.Store.GetState(2));                                          // the state advances either way
            var s = Assert.Single(Structs(d, 2));
            Assert.True(s.IsDeleted);
            Assert.Equal(collected, s.IsGc);
            Assert.Equal(1, s.Length);
            Assert.Equal(!collected, deletedInTx);
            if (!collected) Assert.Same(nested, s.Parent);
        }
    }

    [Fact]
    [Covers("STR-25", "STR-22")]
    public void ContentDeleted_IsIntegratedDeleted_InTheTransactionsDeleteSet_AndNeverCounts()
    {
        var d = Mk();
        var deleted = StructRef.Deleted(1, 0, 3);
        deleted.ParentRoot = "arr";
        var values = StructRef.Any(1, 3, new YValue[] { 1, 2 });
        values.Origin = new ItemId(1, 2);
        var inDeleteSet = false;
        d.Transact(tx =>
        {
            d.IntegrateUpdate(tx, Update(deleted, values));
            inDeleteSet = tx.DeleteSet.Contains(new ItemId(1, 2)) && !tx.DeleteSet.Contains(new ItemId(1, 3));
        });

        Assert.True(inDeleteSet);
        Assert.Equal(2, d.RootBranch("arr").Length);
        Assert.Equal(new[] { true, false }, Structs(d, 1).Select(s => s.IsDeleted));
    }

    [Fact]
    [Covers("STR-16", "STR-33", "STR-39")]
    public void GcAndSkip_NeverWait_AGcIsStoredFromItsOffset_AndASkipIsDropped()
    {
        var d = Mk();
        d.IntegrateRemote(Update(StructRef.Gc(1, 0, 2)));
        d.IntegrateRemote(Update(StructRef.Gc(1, 0, 5), StructRef.Skip(1, 5, 2), Str(1, 7, "z", origin: (4, 0))));

        // the known part of GC(0,5) is skipped and GC(2,3) is stored from the offset; the commit merges it into GC(0,2)
        // (oracle, Yjs 13.6.33: one GC struct (0,5))
        var s = Structs(d, 1);
        Assert.Equal(new[] { (0L, 5L) }, s.Select(x => (x.Clock, x.Length)));
        Assert.All(s, x => Assert.True(x.IsGc));
        Assert.Equal(5, d.Store.GetState(1));
        var waiting = Assert.Single(d.Pending!.Clients);
        Assert.Equal(7L, Assert.Single(waiting.Value).Clock);                             // the struct after the Skip waits; the Skip is not kept
    }

    // ------------------------------------------------------------------ pending state

    [Fact]
    [Covers("STR-03", "STR-63", "ERR-06")]
    public void AStructBeyondTheClientsState_NeverEntersTheStore_ItWaitsAsPending()
    {
        var d = Mk();
        var rejected = d.IntegrateRemote(Update(Str(5, 5, "ty")));

        Assert.Equal(0, rejected);
        Assert.Equal(0, d.Store.StructCount(5));
        Assert.Equal(4L, d.Pending!.Missing[5]);                                            // retry once the state passes clock 4
        var (client, refs) = Assert.Single(d.Pending.Clients);
        Assert.Equal((5UL, 5L, 2L), (client, refs[0].Clock, refs[0].Length));
    }

    [Fact]
    [Covers("STR-62", "STR-63", "STR-64")]
    public void WhenOneStructWaits_TheEarlierOnesIntegrate_AndTheRestOfItsClientIsKept()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "a"), Str(1, 1, "b", origin: (2, 0)), Str(1, 2, "c", origin: (1, 1))));

        Assert.Equal("a", Text(d));
        Assert.Equal(1, d.Store.GetState(1));
        Assert.Equal(new[] { 1L, 2L }, d.Pending!.Clients.Single().Value.Select(r => r.Clock));

        d.IntegrateRemote(Update(Str(2, 0, "X", origin: (1, 0))));
        Assert.Equal("aXbc", Text(d));
        Assert.Null(d.Pending);
    }

    [Fact]
    [Covers("STR-63", "STR-64", "ERR-10")]
    public void ThePredictedPendingSize_IsExactlyWhatTheUpdateLeaves_ForRandomWaitingShapes()
    {
        // the size is kept as the pending state changes (TASK-11): gaps, Skips, overlaps, out-of-order clocks, pending deletes and retries
        for (var seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var limited = Mk();
            var reference = Mk();
            for (var step = 0; step < 30; step++)
            {
                var (refs, deletes) = RandomWaitingUpdate(rng);
                UpdateRefs Make()
                {
                    var u = Update(refs);
                    foreach (var (client, clock, length) in deletes) u.AddDelete(client, clock, length);
                    return u;
                }

                reference.IntegrateRemote(Make());
                var expected = Doc.V1Bytes(reference.Pending, reference.PendingDeletes);
                Assert.False(limited.TryIntegrateRemote(Make(), null, expected - 1, out _), $"seed {seed} step {step}: accepted one byte under {expected}");
                Assert.True(limited.TryIntegrateRemote(Make(), null, expected, out _), $"seed {seed} step {step}: rejected at {expected}");
                Assert.Equal(expected, Doc.V1Bytes(limited.Pending, limited.PendingDeletes));
            }

            Assert.Equal(Text(reference), Text(limited));
        }
    }

    /// <summary>One or two clients' runs of strings and GCs from a random clock, Skips between, origins in other clients, sometimes deletes.</summary>
    private static (StructRef[] Refs, (ulong, long, long)[] Deletes) RandomWaitingUpdate(Random rng)
    {
        var refs = new List<StructRef>();
        var first = (ulong)rng.Next(1, 5);
        foreach (var client in rng.Next(2) == 0 ? new[] { first } : new[] { first, first % 4 + 1 })
        {
            var clock = (long)rng.Next(0, 10);
            var count = rng.Next(1, 4);
            for (var i = 0; i < count; i++)
            {
                var length = rng.Next(1, 3);
                switch (i > 0 && i < count - 1 ? rng.Next(3) : rng.Next(2))
                {
                    case 0:
                        var other = (client + (ulong)rng.Next(1, 4) - 1) % 4 + 1;
                        refs.Add(Str(client, clock, length == 1 ? "a" : "ab", origin: rng.Next(2) == 0 ? (other, rng.Next(0, 10)) : null));
                        break;
                    case 1:
                        refs.Add(StructRef.Gc(client, clock, length));
                        break;
                    default:
                        refs.Add(StructRef.Skip(client, clock, length));
                        break;
                }

                clock += length;
            }
        }

        var deletes = new List<(ulong, long, long)>();
        if (rng.Next(3) == 0) deletes.Add(((ulong)rng.Next(1, 5), rng.Next(0, 12), rng.Next(1, 4)));
        return (refs.ToArray(), deletes.ToArray());
    }

    [Fact]
    [Covers("STR-64")]
    public void IndependentPendingUpdates_MergeIntoOnePendingState_AndEachIntegratesWhenItsDependencyArrives()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(2, 0, "b", origin: (1, 0))));
        d.IntegrateRemote(Update(Str(3, 0, "c", origin: (4, 0))));
        d.IntegrateRemote(Update(Str(2, 0, "b", origin: (1, 0))));                         // the same again: merged, not doubled
        Assert.Equal(new ulong[] { 2, 3 }, d.Pending!.Clients.Select(c => c.Key));
        Assert.All(d.Pending.Clients, c => Assert.Single(c.Value));
        Assert.Equal(new ulong[] { 1, 4 }, d.Pending.Missing.Keys.OrderBy(k => k));

        d.IntegrateRemote(Update(Str(1, 0, "a")));
        Assert.Equal("ab", Text(d));
        Assert.Equal(new ulong[] { 3 }, d.Pending!.Clients.Select(c => c.Key));
        d.IntegrateRemote(Update(Str(4, 0, "x")));                                         // x goes after b (the scan passes a, then b)
        Assert.Equal("abxc", Text(d));
        Assert.Null(d.Pending);
    }

    [Fact]
    [Covers("STR-64")]
    public void PendingStructs_OverlappingOnesMerge_SlicedWhereTheyOverlap()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 2, "cd", origin: (1, 1))));
        d.IntegrateRemote(Update(Str(1, 3, "def", origin: (1, 2))));
        var refs = d.Pending!.Clients.Single().Value;
        Assert.Equal(new[] { (2L, 2L), (4L, 2L) }, refs.Select(r => (r.Clock, r.Length)));
        Assert.Equal("ef", refs[1].Text);
        Assert.Equal(new ItemId(1, 3), refs[1].Origin);

        d.IntegrateRemote(Update(Str(1, 0, "ab")));
        Assert.Equal("abcdef", Text(d));
        Assert.Null(d.Pending);
    }

    [Fact]
    [Covers("STR-65")]
    public void ADeleteSet_ArrivingBeforeItsStructs_IsHeld_AndAppliedWhenTheyArrive()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "ab")));
        var deletes = new UpdateRefs();
        deletes.AddDelete(1, 1, 3);                                                        // b is known, clocks 2..3 are not
        d.IntegrateRemote(deletes);
        Assert.Equal("a", Text(d));
        var held = Assert.Single(Ranges(d.PendingDeletes!));
        Assert.Equal(new DeleteRange(1, 2, 2), held);

        d.IntegrateRemote(Update(Str(1, 2, "cde", origin: (1, 1))));
        Assert.Equal("ae", Text(d));
        Assert.Null(d.PendingDeletes);
        // split where the range ends inside an item; the commit then merges the deleted b with the deleted cd
        // (oracle, Yjs 13.6.33: (0,1) live, (1,3) ContentDeleted, (4,1) live)
        Assert.Equal(new[] { (0L, 1L, false), (1L, 3L, true), (4L, 1L, false) },
            Structs(d, 1).Select(s => (s.Clock, s.Length, s.IsDeleted)));
    }

    [Fact]
    [Covers("STR-65")]
    public void ADeleteRange_InsideOneItem_SplitsItThreeWays_AndReapplyingItChangesNothing()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "abcde")));
        var deletes = new UpdateRefs();
        deletes.AddDelete(1, 1, 3);
        d.IntegrateRemote(deletes);
        d.IntegrateRemote(deletes);

        Assert.Equal("ae", Text(d));
        Assert.Equal(new[] { (0L, 1L, false), (1L, 3L, true), (4L, 1L, false) }, Structs(d, 1).Select(s => (s.Clock, s.Length, s.IsDeleted)));
        Assert.Null(d.PendingDeletes);
    }

    // ------------------------------------------------------------------ rejection (ADR 0005)

    [Fact]
    [Covers("ERR-07")]
    public void AStructWhoseParentIsNotAType_IsRejectedWithItsDependents_TheRestApplies_AndItIsNeverKept()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "hi")));
        var x = StructRef.String(2, 0, "x");
        x.ParentId = new ItemId(1, 0);                                                     // a string item, not a type
        var bad = Update(x, Str(2, 1, "y", origin: (2, 0)), Str(4, 0, "z", origin: (2, 1)), Str(3, 0, "ok"));

        Assert.Equal(3, d.IntegrateRemote(bad));                                           // x, y after it, and z that depends on y
        Assert.Contains("ok", Text(d));
        Assert.Contains("hi", Text(d));
        Assert.Equal(0, d.Store.GetState(2));
        Assert.Equal(0, d.Store.GetState(4));
        Assert.Null(d.Pending);
        Assert.False(d.IsFaulted);

        var x2 = StructRef.String(2, 0, "x");
        x2.ParentId = new ItemId(1, 0);
        Assert.Equal(2, d.IntegrateRemote(Update(x2, Str(2, 1, "y", origin: (2, 0)))));    // never retried: rejected again
    }

    [Fact]
    [Covers("ERR-07", "ERR-21")]
    public void ADependencyOnTheStructsOwnFuture_IsRejected_NotAFault()
    {
        var d = Mk();
        Assert.Equal(1, d.IntegrateRemote(Update(Str(5, 0, "x", origin: (5, 3)))));
        Assert.Equal(1, d.IntegrateRemote(Update(Str(5, 0, "x", right: (5, 0)))));
        var p = StructRef.String(5, 0, "x");
        p.ParentId = new ItemId(5, 0);
        Assert.Equal(1, d.IntegrateRemote(Update(p)));
        Assert.False(d.IsFaulted);
        Assert.Equal(0, d.Store.StructCount(5));
    }

    // ------------------------------------------------------------------ faulted documents (ERR-20)

    [Fact]
    [Covers("ERR-20")]
    public void AViolatedInvariant_FaultsTheDocumentForGood_ItStaysReadable_AndRefusesEveryChange()
    {
        var d = Mk();
        d.IntegrateRemote(Update(Str(1, 0, "hi")));
        Assert.False(d.IsFaulted);
        d.InjectInvariantViolation();
        Assert.False(d.IsFaulted);                                                        // detected by the next change, not by the hook

        Assert.Throws<YInternalException>(() => d.IntegrateRemote(Update(Str(1, 2, "!", origin: (1, 1)))));
        Assert.True(d.IsFaulted);

        Assert.Equal("hi", Text(d));                                                       // readable
        Assert.Equal(2, d.Store.GetState(1));
        Assert.Throws<YInternalException>(() => d.IntegrateRemote(Update(Str(2, 0, "x"))));
        var deletes = new UpdateRefs();
        deletes.AddDelete(1, 0, 1);
        Assert.Throws<YInternalException>(() => d.IntegrateRemote(deletes));
        Assert.Throws<YInternalException>(() => d.Transact(tx => d.DeleteItem(tx, Structs(d, 1)[0])));
        Assert.Throws<YInternalException>(() => d.Transact(tx =>
        {
            var item = d.Store.CreateLocalItem(d.ClientId, d.RootBranch("t"), ItemRef.Null, ItemRef.Null, default, false, ContentKind.String,
                d.Store.AppendText("x"), 1);
            d.IntegrateLocal(tx, item);
        }));
        Assert.Equal("hi", Text(d));
        Assert.Equal(0, d.Store.GetState(2));
        Assert.True(d.IsFaulted);
        d.Transact(_ => { });                                                              // the lock is free again
        Assert.True(d.IsFaulted);
    }

    // ------------------------------------------------------------------ local integration and deletion

    [Fact]
    [Covers("STR-23", "STR-26")]
    public void ALocalItem_IsLinkedBetweenItsNeighbours_AndDeletingItTwice_DeletesOnce()
    {
        var d = Mk(7);
        d.IntegrateRemote(Update(Str(1, 0, "ac")));
        var a = d.Store.GetItemCleanEnd(1, 0);
        var c = a.Right;
        var ranges = 0;
        d.Transact(tx =>
        {
            var item = d.Store.CreateLocalItem(7, a.Parent!, a, c, default, false, ContentKind.String, d.Store.AppendText("b"), 1);
            d.IntegrateLocal(tx, item);
            d.DeleteItem(tx, item);
            d.DeleteItem(tx, item);
            ranges = tx.DeleteRanges.Count;
        });

        Assert.Equal("ac", Text(d));
        Assert.Equal(1, ranges);
        Assert.Equal(new ItemId(1, 0), d.Store.OriginOf(Structs(d, 7)[0]));
        Assert.Equal(2, d.RootBranch("t").Length);
    }

    [Fact]
    [Covers("STR-42", "STR-26")]
    public void DeletingANestedType_DeletesEverythingInside_AtAnyDepth()
    {
        var d = Mk();
        var refs = new List<StructRef>();
        var depth = 2000;                                                                  // deep enough that recursion per level would hurt
        for (var i = 0; i < depth; i++)
        {
            var t = StructRef.OfPayload(1, i, ContentKind.Type, new Branch());
            if (i == 0) t.ParentRoot = "arr";
            else t.ParentId = new ItemId(1, i - 1);
            refs.Add(t);
        }

        var leaf = MapEntry(1, depth, null, "k", 1);
        leaf.ParentId = new ItemId(1, depth - 1);
        refs.Add(leaf);
        d.IntegrateRemote(Update(refs.ToArray()));
        Assert.Equal(1, d.RootBranch("arr").Length);

        var deletes = new UpdateRefs();
        deletes.AddDelete(1, 0, 1);
        d.IntegrateRemote(deletes);

        Assert.Equal(0, d.RootBranch("arr").Length);
        Assert.All(Structs(d, 1), s => Assert.True(s.IsDeleted));
    }

    // ------------------------------------------------------------------ more helpers

    private static StructRef MapEntry(ulong client, long clock, string? root, string key, YValue value)
    {
        var r = StructRef.Any(client, clock, new[] { value });
        r.ParentRoot = root;
        r.ParentSub = key;
        return r;
    }

    private static List<ItemRef> Structs(Doc d, ulong client) =>
        Enumerable.Range(0, d.Store.StructCount(client)).Select(i => d.Store.StructAt(client, i)).ToList();

    private static List<DeleteRange> Ranges(DeleteRanges ranges) => Enumerable.Range(0, ranges.Count).Select(i => ranges[i]).ToList();

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
