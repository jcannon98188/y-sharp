using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;

namespace YSharp.Interop.Generation;

/// <summary>A plan plus a deterministic mutation of the update it produces (ADR 0012 "Compared": mutated valid updates).</summary>
public sealed record MutationInput(ScriptPlan Plan, int Seed, MutationKind Kind)
{
    public override string ToString() => $"{Kind} (seed {Seed}) of the update of [{Plan}]";

    public static Arbitrary<MutationInput> Arbitrary() => Arb.From(
        Gen.Choose(0, int.MaxValue).Select(s =>
        {
            var rng = new Random(s);
            return new MutationInput(ScriptPlan.Generate(s), rng.Next(), (MutationKind)rng.Next(Enum.GetValues<MutationKind>().Length));
        }),
        Shrink);

    public static IEnumerable<MutationInput> Shrink(MutationInput m)
    {
        foreach (var plan in ScriptPlan.Shrink(m.Plan)) yield return m with { Plan = plan };
        if (m.Kind != MutationKind.Truncate) yield return m with { Kind = MutationKind.Truncate };
    }
}

public static class Mutator
{
    /// <summary>The mutated bytes and the offset the mutation hit. A proper prefix for <see cref="MutationKind.Truncate"/>.</summary>
    public static (byte[] Bytes, int Offset) Apply(byte[] valid, MutationKind kind, int seed)
    {
        if (valid.Length == 0) return (valid, 0);
        var s = (uint)seed;
        switch (kind)
        {
            case MutationKind.Truncate:
            {
                var cut = (int)(s % (uint)valid.Length);
                return (valid[..cut], cut);
            }
            case MutationKind.FlipByte:
            {
                var at = (int)(s % (uint)valid.Length);
                var copy = (byte[])valid.Clone();
                copy[at] ^= (byte)(1 << (int)((s >> 16) % 8));
                return (copy, at);
            }
            case MutationKind.DeleteByte:
            {
                var at = (int)(s % (uint)valid.Length);
                return (valid.Where((_, i) => i != at).ToArray(), at);
            }
            default:
            {
                var at = (int)(s % (uint)(valid.Length + 1));
                var list = valid.ToList();
                list.Insert(at, (byte)(s >> 8));
                return (list.ToArray(), at);
            }
        }
    }

    /// <summary>Up to <paramref name="max"/> cut offsets in <c>[0, length)</c>: every offset for short updates, otherwise spread with both ends covered.</summary>
    public static IReadOnlyList<int> CutOffsets(int length, int max)
    {
        if (length <= max) return Enumerable.Range(0, length).ToList();
        var cuts = new SortedSet<int> { 0, 1, length - 1, length - 2 };
        for (var i = 0; cuts.Count < max; i++) cuts.Add((int)((long)i * (length - 1) / (max - 5)));
        return cuts.Where(c => c >= 0 && c < length).ToList();
    }
}
