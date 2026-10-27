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
/// open and between restarts. At the end every peer and a fresh peer loading from the store hold the same state, byte for byte, and a
/// <see cref="Doc"/> that applied every local edit directly holds the same content. Not the same bytes: a remote change to formatted text
/// makes a replica delete the format items it left redundant (Yjs cleanupYTextAfterTransaction), and which ones depends on the order the
/// changes arrived in. The peers send their cleanup deletes as their own updates, and the run settles until none is left unsent; the direct
/// Doc's stay its own. The edits are <see cref="RelayWorkload.Edit"/>'s, the relay tests' workload, without surrogate pairs:
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
        // settle: a peer applying a format change deletes the format items it left redundant, and sends those deletes as its own update
        for (var round = 0; ; round++)
        {
            Assert.True(round < 10, "the peers did not settle");
            foreach (var p in peers) await p.SendUnsentAsync();
            foreach (var p in peers) await p.FlushAsync();
            foreach (var p in peers) await p.FlushAsync();
            foreach (var p in peers) await p.DrainAsync();
            if (!peers.Any(p => p.HasUnsent)) break;
        }

        Assert.All(peers, p => Assert.Empty(p.Denied));
        var expected = peers[0].Doc.EncodeStateAsUpdate();
        foreach (var p in peers) Assert.Equal(expected, p.Doc.EncodeStateAsUpdate());
        Assert.Equal(FormattedText(reference), FormattedText(peers[0].Doc));
        Assert.Equal(reference.GetArray("array").ToJson(), peers[0].Doc.GetArray("array").ToJson());
        Assert.Equal(MapEntries(reference), MapEntries(peers[0].Doc));

        await server.DisposeAsync();
        await using var final = Start();
        var late = await Peer.JoinAsync(final, "doc", 99);
        await late.FlushAsync();
        Assert.Equal(expected, late.Doc.EncodeStateAsUpdate());
        Assert.True(restarts >= 1);
    }

    // content, not JSON: an object's members compare in order, and a map's keys are in the order this replica first saw them

    /// <summary>The text with its formatting, run by run.</summary>
    private static List<(string? Text, YValue Attributes)> FormattedText(Doc doc) =>
        doc.GetText("text").ToDelta().Select(op => (op.Text, op.Attributes)).ToList();

    /// <summary>The map's entries, by key.</summary>
    private static List<KeyValuePair<string, YValue>> MapEntries(Doc doc)
    {
        Assert.True(doc.GetMap("map").ToJson().TryGetObject(out var map));
        return Enumerable.Range(0, map.Count).Select(i => map[i]).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
    }
}
