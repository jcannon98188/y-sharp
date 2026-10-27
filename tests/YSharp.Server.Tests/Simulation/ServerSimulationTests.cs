using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.Server.Tests.Mailbox;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Relay;
using YSharp.TestKit.Traceability;

namespace YSharp.Server.Tests.Simulation;

/// <summary>
/// The server tier end to end (TASK-5.7): seeded runs of y-protocols peers editing a text, an array and a map through <see cref="YServer"/>,
/// with peers dropping off and editing offline, the server restarting over the same store, and the log compacted both while the document is
/// open and between restarts. At the end every peer, a fresh peer loading from the store, and a <see cref="Doc"/> that applied every local
/// edit directly hold the same state. The edits are <see cref="RelayWorkload.Edit"/>'s, the relay tests' workload, without surrogate pairs:
/// a format that changes nothing, at an index inside a pair, turns the pair into two U+FFFD in that replica only and emits no update (as in
/// Yjs, yjs#248), so no path converges there. Every third seed runs with a validation hook, so the hot Doc is in the path too.
/// </summary>
[Requires(Feature.UpdateFunctions, Feature.V2, Feature.V2Functions, Feature.Sync, Feature.Awareness, Feature.Text, Feature.TextFormat, Feature.Array, Feature.Map, Feature.Events)]
public sealed class ServerSimulationTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(s => new object[] { s, s % 3 == 0 });

    [Theory, Covers("SRV-4"), Covers("SRV-9"), Covers("SRV-11"), Covers("SRV-19")]
    [MemberData(nameof(Seeds))]
    public async Task PeersThroughTheServer_WithDropsRestartsAndCompaction_ConvergeAsTheDocOnlyPathDoes(int seed, bool hooked)
    {
        var ct = TestContext.Current.CancellationToken;
        var rng = new Random(seed);
        var store = new InMemoryUpdateStore();
        ValidationHook accept = (in ValidationRequest _) => ValidationResult.Accept;
        // slow subscribers are MailboxTests' concern: here a peer may leave its queue unread for the whole run. So is the hook's deadline: under
        // a loaded test run a stall past it rejected an edit, which the server then never had
        YServer Start() => new(new YServerOptions
        {
            Store = store, Validation = hooked ? _ => accept : null, SubscriberQueueCapacity = 1 << 16, ValidationDeadline = Timeout.InfiniteTimeSpan,
        });

        // the Doc-only path: every local edit of every peer, applied directly
        var reference = new Doc();
        var server = Start();
        var peers = new List<Peer>();
        for (var i = 0; i < 4; i++)
        {
            var peer = await Peer.JoinAsync(server, "doc", (ulong)(i + 1));
            peer.Doc.Update += (u, origin, _, _) =>
            {
                if (origin is null) reference.ApplyUpdate(u);
            };
            peers.Add(peer);
        }

        var online = peers.ToHashSet();
        var restarts = 0;
        async Task RestartAsync()
        {
            // shutdown drains every accepted update; the log may be compacted while no one has it open; every peer comes back
            await server.DisposeAsync();
            if (rng.Next(2) == 0) await new UpdateLog(store).CompactAsync("doc", ct);
            server = Start();
            restarts++;
            foreach (var p in peers) await p.ReconnectAsync(server);
            online = peers.ToHashSet();
        }

        for (var step = 0; step < 250; step++)
        {
            if (step == 125) await RestartAsync();
            var peer = peers[rng.Next(peers.Count)];
            var roll = rng.Next(100);
            if (roll < 55)
            {
                if (online.Contains(peer)) await peer.EditAsync(d => RelayWorkload.Edit(rng, d, astral: false));
                else peer.EditOffline(d => RelayWorkload.Edit(rng, d, astral: false));
            }
            else if (roll < 78)
            {
                if (online.Contains(peer)) await peer.FlushAsync();
            }
            else if (roll < 90)
            {
                if (online.Remove(peer)) await peer.Connection.DisposeAsync();
                else
                {
                    await peer.ReconnectAsync(server);
                    online.Add(peer);
                }
            }
            else if (roll < 93)
            {
                await RestartAsync();
            }
            else if (roll < 96)
            {
                // compaction while the document is open: appends after the compacted sequence stay in the log (SRV-11)
                await new UpdateLog(store).CompactAsync("doc", ct);
            }
            else
            {
                foreach (var p in online) await p.DrainAsync();
            }
        }

        foreach (var p in peers.Where(p => !online.Contains(p))) await p.ReconnectAsync(server);
        for (var round = 0; round < 2; round++)
            foreach (var p in peers) await p.FlushAsync();
        foreach (var p in peers) await p.DrainAsync();

        Assert.All(peers, p => Assert.Empty(p.Denied));
        var expected = reference.EncodeStateAsUpdate();
        foreach (var p in peers) Assert.Equal(expected, p.Doc.EncodeStateAsUpdate());

        await server.DisposeAsync();
        await using var final = Start();
        var late = await Peer.JoinAsync(final, "doc", 99);
        await late.FlushAsync();
        Assert.Equal(expected, late.Doc.EncodeStateAsUpdate());
        Assert.True(restarts >= 1);
    }
}
