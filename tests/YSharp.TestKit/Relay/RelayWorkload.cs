using System;
using System.Buffers;
using System.Collections.Generic;

namespace YSharp.TestKit.Relay;

/// <summary>
/// Updates for the relay tests (TASK-5.2): three replicas (clients 1, 2 and 3) make seeded random edits to a text (inserts, deletes, formats),
/// an array (values and nested maps) and a map, and now and then one syncs from another. Only local edits are recorded, as a server's relay
/// receives them from each client; the sync traffic between the replicas is what makes later edits depend on other clients' structs.
/// </summary>
public static class RelayWorkload
{
    private static readonly string[] Words = { "a", "bc", "def", "\u00e9", "\ud83d\ude00", "xyz12" };

    /// <summary>Every local update the replicas emitted, in emission order, in <paramref name="format"/>.</summary>
    public static List<byte[]> Updates(int seed, int steps, UpdateFormat format)
    {
        var rng = new Random(seed);
        var docs = new Doc[3];
        var updates = new List<byte[]>();
        const string sync = "relay-workload-sync";
        for (var i = 0; i < docs.Length; i++)
        {
            docs[i] = new Doc(new DocOptions { ClientId = (ulong)(i + 1) });
            void Record(ReadOnlySpan<byte> u, object? origin)
            {
                if (!ReferenceEquals(origin, sync)) updates.Add(u.ToArray());
            }
            if (format == UpdateFormat.V1) docs[i].Update += (u, origin, _, _) => Record(u, origin);
            else docs[i].UpdateV2 += (u, origin, _, _) => Record(u, origin);
        }

        for (var s = 0; s < steps; s++)
        {
            var doc = docs[rng.Next(docs.Length)];
            var text = doc.GetText("text");
            var array = doc.GetArray("array");
            var map = doc.GetMap("map");
            switch (rng.Next(10))
            {
                case 0:
                case 1:
                case 2:
                    text.Insert(rng.Next(text.Length + 1), Words[rng.Next(Words.Length)]);
                    break;
                case 3 when text.Length > 0:
                {
                    var at = rng.Next(text.Length);
                    text.Delete(at, 1 + rng.Next(Math.Min(3, text.Length - at)));
                    break;
                }
                case 4 when text.Length > 0:
                {
                    var at = rng.Next(text.Length);
                    var bold = new[] { new KeyValuePair<string, YValue>("bold", rng.Next(2) == 0 ? YValue.Null : (YValue)true) };
                    text.Format(at, 1 + rng.Next(text.Length - at), YValue.Object(bold));
                    break;
                }
                case 5:
                    array.Insert(rng.Next(array.Length + 1), rng.Next(4) == 0 ? new YEntry[] { new YMap() } : new YEntry[] { rng.Next(100), Words[rng.Next(Words.Length)] });
                    break;
                case 6 when array.Length > 0:
                    array.Delete(rng.Next(array.Length));
                    break;
                case 7:
                    map.Set("k" + rng.Next(4), rng.Next(1000));
                    break;
                case 8:
                    map.Delete("k" + rng.Next(4));
                    break;
                default:
                {
                    var from = docs[rng.Next(docs.Length)];
                    if (ReferenceEquals(from, doc)) break;
                    var sv = new ArrayBufferWriter<byte>();
                    doc.EncodeStateVector(sv);
                    doc.ApplyUpdate(from.EncodeStateAsUpdate(sv.WrittenSpan), sync);
                    break;
                }
            }
        }

        return updates;
    }
}
