using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using YSharp.Fuzz.Corpus;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz.Generators;

/// <summary>A hostile input as bytes plus the format to apply it as, with a short origin note for failure messages.</summary>
internal sealed record RawInput(UpdateFormat Format, byte[] Bytes, string Origin)
{
    public override string ToString() => $"{Origin} {Format} [{Bytes.Length} bytes] {Hex.Short(Bytes)}";
}

public enum MutationKind { BitFlip, Overwrite, Insert, Delete, Splice, Inflate, Truncate, Duplicate }

/// <summary>One edit. Positions are per-mille of the current length so an op list stays meaningful when earlier ops change the length.</summary>
internal sealed record Mutation(MutationKind Kind, int Permille, int Value, int Length, int DonorPermille);

/// <summary>
/// FsCheck layer 1 (ADR 0013): a raw-byte mutator over valid seeds. Bit flips, byte overwrites (biased to the values that matter in a
/// varint stream), insertions, deletions, splices of another range of the seed, length inflation (a run of 0xff bytes in front of a
/// terminator, the ten-byte overflow forms) and truncation. Randomness comes from one int per case, so a replayed FsCheck seed
/// reproduces the exact bytes; shrinking goes to the shortest bytes.
/// </summary>
internal static class ByteMutator
{
    private static readonly byte[] InterestingBytes = { 0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a, 0x04, 0x08, 0x74 };

    public static IReadOnlyList<Mutation> RandomMutations(Random rng)
    {
        var count = rng.Next(1, 5);
        var ops = new List<Mutation>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = (MutationKind)rng.Next(Enum.GetValues<MutationKind>().Length);
            ops.Add(new Mutation(kind, rng.Next(0, 1000), rng.Next(0, 256), rng.Next(1, 5), rng.Next(0, 1000)));
        }
        return ops;
    }

    public static byte[] Apply(byte[] seed, IReadOnlyList<Mutation> ops)
    {
        var bytes = seed.ToList();
        foreach (var op in ops)
        {
            if (bytes.Count == 0 && op.Kind != MutationKind.Insert) continue;
            var pos = bytes.Count == 0 ? 0 : Math.Min(bytes.Count - 1, op.Permille * bytes.Count / 1000);
            switch (op.Kind)
            {
                case MutationKind.BitFlip:
                    bytes[pos] = (byte)(bytes[pos] ^ (1 << (op.Value & 7)));
                    break;
                case MutationKind.Overwrite:
                    bytes[pos] = op.Value % 3 == 0 ? InterestingBytes[op.Value / 3 % InterestingBytes.Length] : (byte)op.Value;
                    break;
                case MutationKind.Insert:
                    bytes.InsertRange(Math.Min(bytes.Count, pos), Enumerable.Range(0, op.Length).Select(i => (byte)(op.Value + i * 37)));
                    break;
                case MutationKind.Delete:
                    bytes.RemoveRange(pos, Math.Min(op.Length, bytes.Count - pos));
                    break;
                case MutationKind.Splice:
                {
                    var from = Math.Min(bytes.Count - 1, op.DonorPermille * bytes.Count / 1000);
                    var chunk = bytes.Skip(from).Take(op.Length * 3).ToList();
                    bytes.InsertRange(pos, chunk);
                    break;
                }
                case MutationKind.Inflate:
                    // a length or count that claims far more than the buffer holds: nine continuation bytes and a terminator
                    bytes.InsertRange(pos, Enumerable.Repeat((byte)0xff, 4 + op.Length * 2).Append((byte)(op.Value & 1)));
                    break;
                case MutationKind.Truncate:
                    bytes.RemoveRange(pos, bytes.Count - pos);
                    break;
                case MutationKind.Duplicate:
                    bytes.InsertRange(pos, bytes.Skip(pos).Take(op.Length * 2).ToList());
                    break;
            }
            if (bytes.Count > 1 << 20) bytes.RemoveRange(1 << 20, bytes.Count - (1 << 20));
        }
        return bytes.ToArray();
    }

    /// <summary>Seeds the mutator draws from: harvested valid updates up to <paramref name="maxBytes"/>.</summary>
    public static IReadOnlyList<SeedUpdates.Seed> Seeds(int maxBytes = 4096) =>
        SeedUpdates.Collect().Where(s => s.Bytes.Length <= maxBytes).ToList();

    public static RawInput Generate(int caseSeed, IReadOnlyList<SeedUpdates.Seed> seeds)
    {
        var rng = new Random(caseSeed);
        var seed = seeds[rng.Next(seeds.Count)];
        var ops = RandomMutations(rng);
        return new RawInput(seed.Format, Apply(seed.Bytes, ops), $"mutation of {seed.Source} ({string.Join("+", ops.Select(o => o.Kind))})");
    }

    public static Arbitrary<RawInput> Layer() => Layer(Seeds());

    public static Arbitrary<RawInput> Layer(IReadOnlyList<SeedUpdates.Seed> seeds) =>
        Arb.From(Gen.Choose(0, int.MaxValue).Select(s => Generate(s, seeds)), Shrink);

    /// <summary>Shortest first: halves, then single-byte removals, then zeroed bytes. Every candidate is no longer than the original.</summary>
    public static IEnumerable<RawInput> Shrink(RawInput input) => ShrinkBytes(input.Bytes).Select(b => input with { Bytes = b });

    public static IEnumerable<byte[]> ShrinkBytes(byte[] bytes)
    {
        if (bytes.Length == 0) yield break;
        yield return Array.Empty<byte>();
        if (bytes.Length > 1)
        {
            yield return bytes[..(bytes.Length / 2)];
            yield return bytes[(bytes.Length / 2)..];
        }
        var step = Math.Max(1, bytes.Length / 32);
        for (var i = 0; i < bytes.Length; i += step)
            yield return bytes.Take(i).Concat(bytes.Skip(i + 1)).ToArray();
        for (var i = 0; i < bytes.Length && i < 64; i++)
        {
            if (bytes[i] == 0) continue;
            var copy = (byte[])bytes.Clone();
            copy[i] = 0;
            yield return copy;
        }
    }
}
