using System;
using System.Buffers;
using System.Threading;
using Xunit;

namespace YSharp.Tests.Allocation;

internal static class AllocDocs
{
    /// <summary>A document with a little of everything, so encode and apply have real work.</summary>
    public static Doc Populated(ulong clientId = 1, int chars = 200)
    {
        var doc = new Doc(new DocOptions { ClientId = clientId });
        var text = doc.GetText("t");
        var arr = doc.GetArray("a");
        var map = doc.GetMap("m");
        doc.Transact(_ =>
        {
            text.Insert(0, new string('a', chars));
            for (var i = 0; i < 20; i++) arr.Push(new YEntry[] { i });
            for (var i = 0; i < 20; i++) map.Set("k" + i, i);
        });
        return doc;
    }

    /// <summary>
    /// <paramref name="count"/> consecutive updates of a fresh source document, one per local edit, so each applies new structs to a
    /// target that has applied the ones before it.
    /// </summary>
    public static byte[][] UpdateStream(int count, UpdateFormat format, ulong clientId = 7)
    {
        var source = new Doc(new DocOptions { ClientId = clientId });
        var text = source.GetText("t");
        var updates = new byte[count][];
        var i = 0;
        UpdateHandler capture = (update, _, _, _) => updates[i] = update.ToArray();
        if (format == UpdateFormat.V1) source.Update += capture; else source.UpdateV2 += capture;
        for (i = 0; i < count; i++) text.Insert(text.Length, "x");
        if (format == UpdateFormat.V1) source.Update -= capture; else source.UpdateV2 -= capture;
        return updates;
    }
}

/// <summary>Runs <paramref name="body"/> while another thread holds the document's write transaction.</summary>
internal sealed class HeldWriter : IDisposable
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _thread;

    public HeldWriter(Doc doc)
    {
        _thread = new Thread(() => doc.Transact(_ =>
        {
            _entered.Set();
            _release.Wait();
        })) { IsBackground = true, Name = "held-writer" };
        _thread.Start();
        if (!_entered.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("the holding writer never entered its transaction");
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(20));
    }
}

internal static class Writers
{
    public static ArrayBufferWriter<byte> PreSized(int bytes = 1 << 16) => new(bytes);
}
