using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YSharp.Server.Tests.Storage;

/// <summary>
/// A store over simulated files, used only to exercise the conformance suite's crash hooks, as a durable backend would see them. Each document
/// is one log file of frames, <c>[kind][sequence:8][length:4][checksum:4][payload]</c>; compaction writes a new file and swaps it in, as a
/// rename would. A restart reparses the files: a frame cut short or failing its checksum at the end of the file is a torn append and is cut
/// off; a bad frame with intact data after it fails the read. Not a shipped backend (ADR 0024 SRV-12).
/// </summary>
public sealed class FramedTestStore : IUpdateStore
{
    /// <summary>The simulated disk: one file per document. It outlives a store instance, as a disk outlives a process.</summary>
    public sealed class Disk
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public readonly object Gate = new();
    }

    /// <summary>Deliberate contract breaks, to show the conformance suite catches each.</summary>
    public enum Defect { None, SkipsCorruptRecords, DropsLaterRecordsOnCompaction, KeepsTheCallersBuffer, ReusesSequencesAfterCompaction }

    private const int Header = 1 + 8 + 4 + 4;
    private readonly Disk _disk;
    private readonly Defect _defect;
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ReadOnlyMemory<byte>>> _aliased = new(StringComparer.Ordinal);

    public FramedTestStore(Disk disk, Defect defect = Defect.None)
    {
        _disk = disk;
        _defect = defect;
    }

    public Disk Medium => _disk;

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        var h = 2166136261u;
        foreach (var b in data) h = (h ^ b) * 16777619u;
        return h;
    }

    private static byte[] Frame(bool snapshot, long seq, ReadOnlySpan<byte> payload)
    {
        var f = new byte[Header + payload.Length];
        f[0] = snapshot ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(1), seq);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(9), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(13), Checksum(payload));
        payload.CopyTo(f.AsSpan(Header));
        return f;
    }

    /// <summary>Parses a file; cuts off a torn tail frame (and returns the trimmed file), fails on a bad frame followed by more data.</summary>
    private List<UpdateRecord> Parse(string docId, byte[] file, out int validLength)
    {
        var records = new List<UpdateRecord>();
        var at = 0;
        while (at < file.Length)
        {
            if (file.Length - at < Header) break;   // a header cut short: torn tail
            var snapshot = file[at] == 1;
            var seq = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(at + 1));
            var len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 9));
            var sum = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 13));
            if (len < 0 || file.Length - at - Header < len) break;   // a payload cut short: torn tail
            var payload = file.AsMemory(at + Header, len);
            var end = at + Header + len;
            if (Checksum(payload.Span) != sum)
            {
                if (end == file.Length) break;   // the last frame is bad: torn tail
                if (_defect == Defect.SkipsCorruptRecords)
                {
                    at = end;
                    continue;
                }

                throw new InvalidDataException($"{docId}: record {seq} fails its checksum and is followed by intact records.");
            }

            records.Add(new UpdateRecord(seq, payload.ToArray(), snapshot));
            at = end;
        }

        validLength = at;
        return records;
    }

    /// <summary>The records of a document, with a torn tail cut off the file. Call under the disk lock.</summary>
    private List<UpdateRecord> Open(string docId)
    {
        if (!_disk.Files.TryGetValue(docId, out var file)) return new List<UpdateRecord>();
        var records = Parse(docId, file, out var valid);
        if (valid < file.Length) _disk.Files[docId] = file.AsSpan(0, valid).ToArray();
        return records;
    }

    private long LastSequence(string docId, List<UpdateRecord> records)
    {
        if (_last.TryGetValue(docId, out var last)) return last;
        last = records.Count == 0 ? 0 : records[^1].Sequence;
        _last[docId] = last;
        return last;
    }

    public ValueTask<long> AppendAsync(string docId, ReadOnlyMemory<byte> updateV2, CancellationToken cancellationToken = default)
    {
        lock (_disk.Gate)
        {
            var records = Open(docId);
            var seq = LastSequence(docId, records) + 1;
            _last[docId] = seq;
            var file = _disk.Files.TryGetValue(docId, out var f) ? f : Array.Empty<byte>();
            var frame = Frame(false, seq, updateV2.Span);
            var grown = new byte[file.Length + frame.Length];
            file.CopyTo(grown, 0);
            frame.CopyTo(grown, file.Length);
            _disk.Files[docId] = grown;
            if (_defect == Defect.KeepsTheCallersBuffer)
            {
                if (!_aliased.TryGetValue(docId, out var list)) _aliased[docId] = list = new List<ReadOnlyMemory<byte>>();
                list.Add(updateV2);
            }

            return ValueTask.FromResult(seq);
        }
    }

    public async IAsyncEnumerable<UpdateRecord> ReadSinceAsync(string docId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<UpdateRecord> records;
        lock (_disk.Gate)
        {
            records = Open(docId);
            if (_defect == Defect.KeepsTheCallersBuffer && _aliased.TryGetValue(docId, out var aliased))
                for (var i = 0; i < records.Count && i < aliased.Count; i++)
                    records[i] = records[i] with { UpdateV2 = aliased[i] };
        }

        foreach (var r in records)
        {
            if (r.Sequence <= afterSequence) continue;
            yield return r;
        }

        await Task.CompletedTask;
    }

    public ValueTask CompactAsync(string docId, long uptoSequence, ReadOnlyMemory<byte> mergedV2, CancellationToken cancellationToken = default)
    {
        lock (_disk.Gate)
        {
            var records = Open(docId);
            if (uptoSequence > LastSequence(docId, records)) throw new ArgumentOutOfRangeException(nameof(uptoSequence));
            if (records.Count > 0 && records[0].IsSnapshot && records[0].Sequence >= uptoSequence) return ValueTask.CompletedTask;
            var next = new List<byte>(Frame(true, uptoSequence, mergedV2.Span));
            if (_defect != Defect.DropsLaterRecordsOnCompaction)
                foreach (var r in records)
                    if (!r.IsSnapshot && r.Sequence > uptoSequence) next.AddRange(Frame(false, r.Sequence, r.UpdateV2.Span));
            _disk.Files[docId] = next.ToArray();   // the rename: one reference swap
            if (_defect == Defect.ReusesSequencesAfterCompaction) _last[docId] = 0;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string docId, CancellationToken cancellationToken = default)
    {
        lock (_disk.Gate)
        {
            _disk.Files.Remove(docId);
            _last.Remove(docId);
            _aliased.Remove(docId);
        }

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- crash hooks

    /// <summary>Writes the first half of the next record's frame and stops, as a crash during the write would.</summary>
    public void TornAppend(string docId, ReadOnlySpan<byte> updateV2)
    {
        lock (_disk.Gate)
        {
            var records = Open(docId);
            var frame = Frame(false, LastSequence(docId, records) + 1, updateV2);
            var file = _disk.Files.TryGetValue(docId, out var f) ? f : Array.Empty<byte>();
            var grown = new byte[file.Length + frame.Length / 2];
            file.CopyTo(grown, 0);
            frame.AsSpan(0, frame.Length / 2).CopyTo(grown.AsSpan(file.Length));
            _disk.Files[docId] = grown;
        }
    }

    /// <summary>Flips a payload byte of record <paramref name="sequence"/>.</summary>
    public bool Corrupt(string docId, long sequence)
    {
        lock (_disk.Gate)
        {
            if (!_disk.Files.TryGetValue(docId, out var file)) return false;
            var at = 0;
            while (at + Header <= file.Length)
            {
                var seq = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(at + 1));
                var len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 9));
                if (seq == sequence && file[at] == 0 && len > 0)
                {
                    file[at + Header] ^= 0xFF;
                    return true;
                }

                at += Header + len;
            }

            return false;
        }
    }

    /// <summary>Writes part of the compacted file and stops before the rename, as a crash during compaction would: the old file stays.</summary>
    public void InterruptedCompaction(string docId, long uptoSequence, ReadOnlySpan<byte> mergedV2)
    {
        var frame = Frame(true, uptoSequence, mergedV2);
        lock (_disk.Gate) _disk.Files[docId + ".compacting"] = frame.AsSpan(0, frame.Length / 2).ToArray();
    }
}
