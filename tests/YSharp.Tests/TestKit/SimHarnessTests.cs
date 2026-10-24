using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.TestKit;

/// <summary>
/// Tests of the ported JS <c>testHelper</c> (TestConnector, init, compare, applyRandomTests).
/// Covers SIM-10 (TestConnector), SIM-11 (init), SIM-12 (compare), SIM-13 (applyRandomTests), SIM-14 (V2 framing), and the
/// fixes SIM-02 and SIM-04 (names proposed here; the registry assigns the final IDs).
/// </summary>
public class SimHarnessTests
{
    /// <summary>Plays back fixed draws so the exact consumption order can be asserted.</summary>
    private sealed class ScriptedPrng : IPrng
    {
        private readonly Queue<double> _draws;
        public ScriptedPrng(params double[] draws) => _draws = new Queue<double>(draws);
        public int Consumed { get; private set; }
        public double Next() { Consumed++; return _draws.Dequeue(); }
    }

    // ------------------------------------------------------------ pure parts

    [Fact]
    [Covers("SIM-10")]
    public void OrderedSet_RemoveThenAdd_MovesToEnd_LikeAJsSet()
    {
        var s = new OrderedSet<int>();
        s.Add(1); s.Add(2); s.Add(3);
        s.Add(2);
        Assert.Equal(new[] { 1, 2, 3 }, s.ToArray());
        s.Remove(2);
        s.Add(2);
        Assert.Equal(new[] { 1, 3, 2 }, s.ToArray());
        Assert.False(s.Remove(9));
        Assert.True(s.Contains(3));
    }

    [Fact]
    [Covers("SIM-14")]
    public void V2Framing_UsesSyncMessageTypeNumbers_AndRoundTripsPayloads()
    {
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        foreach (var type in new[] { SyncMessageType.Step1, SyncMessageType.Step2, SyncMessageType.Update })
        {
            var frame = SimFraming.FrameV2(type, payload);
            Assert.Equal((byte)type, frame[0]);
            var (t, p) = SimFraming.UnframeV2(frame);
            Assert.Equal(type, t);
            Assert.Equal(payload, p);
        }
        // a 300 byte payload needs a two byte varuint length
        Assert.Equal(1 + 2 + 300, SimFraming.FrameV2(SyncMessageType.Update, payload).Length);
    }

    [Fact]
    [Covers("SIM-14")]
    public void V2Framing_TruncatedFrame_Throws()
    {
        var frame = SimFraming.FrameV2(SyncMessageType.Update, new byte[] { 1, 2, 3 });
        Assert.Throws<FormatException>(() => SimFraming.UnframeV2(frame.AsSpan(0, frame.Length - 1).ToArray()));
    }

    [Fact]
    [Covers("SIM-13")]
    public void ChooseNetworkAction_AnyDraw_MatchesJsDrawOrderAndShortCircuit()
    {
        // 0.02*101 = 2.02 -> 2 (<=2): disconnect when the bool draw is >= 0.5, else reconnect
        var g1 = new ScriptedPrng(0.02, 0.9);
        Assert.Equal(NetworkAction.DisconnectRandom, SimHarness.ChooseNetworkAction(g1));
        Assert.Equal(2, g1.Consumed);
        Assert.Equal(NetworkAction.ReconnectRandom, SimHarness.ChooseNetworkAction(new ScriptedPrng(0.0, 0.1)));

        // first draw 50 (>2), second draw 0.005*101=0 (<=1): flush all, two draws
        var g2 = new ScriptedPrng(0.5, 0.005);
        Assert.Equal(NetworkAction.FlushAll, SimHarness.ChooseNetworkAction(g2));
        Assert.Equal(2, g2.Consumed);

        // 50, 50, third draw 0.4*101=40 (<=50): flush one random message, three draws
        var g3 = new ScriptedPrng(0.5, 0.5, 0.4);
        Assert.Equal(NetworkAction.FlushRandom, SimHarness.ChooseNetworkAction(g3));
        Assert.Equal(3, g3.Consumed);

        // 50, 50, 0.9*101=90 (>50): nothing, three draws
        var g4 = new ScriptedPrng(0.5, 0.5, 0.9);
        Assert.Equal(NetworkAction.None, SimHarness.ChooseNetworkAction(g4));
        Assert.Equal(3, g4.Consumed);
    }

    [Fact]
    [Covers("SIM-11")]
    public void ChooseEncoding_OneBoolDraw_SelectsV1OrV2ForTheWholeRun()
    {
        var low = new ScriptedPrng(0.1);
        Assert.Equal(UpdateFormat.V1, SimHarness.ChooseEncoding(low));
        Assert.Equal(1, low.Consumed);
        Assert.Equal(UpdateFormat.V2, SimHarness.ChooseEncoding(new ScriptedPrng(0.9)));
    }

    [Fact]
    [Covers("SIM-10")]
    public void PayloadCounters_NewConnector_AreFresh()
    {
        var a = new PayloadCounters();
        Assert.Equal(0, a.NextUniqueNumber());
        Assert.Equal(1, a.NextUniqueNumber());
        Assert.Equal(0, a.NextChar());
        Assert.Equal(0, new PayloadCounters().NextUniqueNumber());
    }

    // ------------------------------------------------------------ against the implementation (skips on the stub)

    private static void RunOrSkip(Action body)
    {
        try { body(); }
        catch (NotImplementedException) when (ApiStub.IsStub) { Assert.Skip(ApiStub.Reason); }
    }

    [Theory]
    [Covers("SIM-11")]
    [Covers("SIM-12")]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Init_ThenCompare_FreshUsers_Converge(uint seed)
    {
        RunOrSkip(() =>
        {
            var gen = Prng.Create(seed);
            var sim = SimHarness.Init(gen, users: 3);
            Assert.Equal(3, sim.Users.Count);
            Assert.Equal(new ulong[] { 0, 1, 2 }, sim.Users.Select(u => u.Doc.ClientId));
            SimHarness.Compare(sim.Users);
        });
    }

    [Fact]
    [Covers("SIM-13")]
    public void ApplyRandomTests_TextEdits_ConvergeForFixedSeeds()
    {
        RunOrSkip(() =>
        {
            for (uint seed = 1; seed <= 5; seed++)
            {
                var gen = Prng.Create(seed);
                SimHarness.ApplyRandomTests<object?>(gen,
                    new Action<TestYInstance, IPrng, object?>[]
                    {
                        (u, g, _) => { var t = u.Doc.GetText("text"); t.Insert((int)Prng.Int32(g, 0, t.Length), Prng.Word(g)); },
                        (u, g, _) => { var t = u.Doc.GetText("text"); if (t.Length > 0) t.Delete((int)Prng.Int32(g, 0, t.Length - 1), 1); },
                    }, iterations: 50);
            }
        });
    }
}
