using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using YSharp.Fuzz.Builder;
using YSharp.TestKit.Assertions;

namespace YSharp.Fuzz.Generators;

/// <summary>Which hostile class a model generator leans on. Each profile still draws from the common pool of interesting values.</summary>
public enum Profile { Clock, Ref, Sem, Len, Amp }

/// <summary>An update model with the format it is written in.</summary>
internal sealed record ModelInput(UpdateFormat Format, UpdateModel Model, string Note)
{
    public byte[] Bytes => Format == UpdateFormat.V1 ? UpdateBuilder.V1(Model) : UpdateBuilder.V2(Model);

    public override string ToString() => $"{Note} {Format} [{Model.StructCount} structs] {Hex.Short(Bytes)}";
}

/// <summary>A valid model together with the document text a correct decoder must produce.</summary>
internal sealed record ValidInput(UpdateFormat Format, UpdateModel Model, string ExpectedText)
{
    public byte[] Bytes => Format == UpdateFormat.V1 ? UpdateBuilder.V1(Model) : UpdateBuilder.V2(Model);

    public override string ToString() => $"valid {Format} [{Model.StructCount} structs] expect \"{ExpectedText}\" {Hex.Short(Bytes)}";
}

/// <summary>
/// FsCheck layer 2 (ADR 0013): the structure-aware update builder as a generator. Models are written by
/// <see cref="UpdateBuilder"/>, never by y-sharp's encoder, so they reach the pending and reject logic and give an independent check of
/// the decoder. One int per case seeds the construction, so a replayed FsCheck seed reproduces the model; shrinking removes whole
/// structs, blocks and delete ranges first (fewest structs), then simplifies what is left.
/// </summary>
internal static class HostileModels
{
    private static readonly ulong[] Clients = { 1, 2, 3, 900, (1UL << 31) - 1, 1UL << 31, uint.MaxValue };
    private static readonly ulong[] RareClients = { (1UL << 53) - 1, 1UL << 53, ulong.MaxValue };
    private static readonly ulong[] Clocks = { 0, 1, 2, 5, 10, (1UL << 31) - 1, 1UL << 31, (1UL << 53) - 1 };
    private static readonly ulong[] RareClocks = { 1UL << 53, ulong.MaxValue };
    private static readonly ulong[] HugeLengths = { 1UL << 31, 1UL << 32, 1UL << 40, (1UL << 53) - 1, 1UL << 53 };
    private static readonly string[] RootKeys = { "t", "m", "a", "" };

    // ------------------------------------------------------------------ valid models (decoder check, truncation)

    private static readonly string[] Words = { "a", "hello", "Ã©", "â‚¬uro", "xyz", "", "line\nbreak", "tab\t" };

    /// <summary>A valid single-client text update (V1 or V2) with optional deletes, and the text a correct decoder produces. BMP characters only, so a delete never splits a surrogate pair.</summary>
    public static ValidInput GenerateValid(int caseSeed)
    {
        var rng = new Random(caseSeed);
        var client = Clients[rng.Next(5)];   // below 2^31: a valid input must not depend on the HOS-CLOCK high-id decision
        var count = rng.Next(1, 7);
        var structs = new List<Struct>();
        var text = new StringBuilder();
        ulong clock = 0;
        for (var i = 0; i < count; i++)
        {
            var piece = Words[rng.Next(Words.Length)];
            if (piece.Length == 0) piece = "w";
            structs.Add(i == 0 ? Struct.InRoot("t", Content.Str(piece)) : Struct.InRoot("t", Content.Str(piece), origin: new Id(client, clock - 1)));
            text.Append(piece);
            clock += (ulong)piece.Length;
        }

        var deletes = new List<DeleteRange>();
        var chars = text.ToString().ToCharArray();
        var alive = Enumerable.Repeat(true, chars.Length).ToArray();
        for (var i = rng.Next(0, 3); i > 0; i--)
        {
            var from = rng.Next(chars.Length);
            var len = rng.Next(1, chars.Length - from + 1);
            deletes.Add(new DeleteRange((ulong)from, (ulong)len));
            for (var k = from; k < from + len; k++) alive[k] = false;
        }
        // delete ranges of one client in ascending clock order, merged, as the real writer would write them
        var merged = MergeRanges(deletes);
        var expected = new string(chars.Where((_, i) => alive[i]).ToArray());
        var model = new UpdateModel(new[] { new ClientBlock(client, 0, structs) },
            merged.Count == 0 ? Array.Empty<DeleteClient>() : new[] { new DeleteClient(client, merged) });
        return new ValidInput(rng.Next(2) == 0 ? UpdateFormat.V1 : UpdateFormat.V2, model, expected);
    }

    private static List<DeleteRange> MergeRanges(List<DeleteRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Clock).ToList();
        var merged = new List<DeleteRange>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && merged[^1].Clock + merged[^1].Length >= r.Clock)
            {
                var last = merged[^1];
                merged[^1] = new DeleteRange(last.Clock, Math.Max(last.Clock + last.Length, r.Clock + r.Length) - last.Clock);
            }
            else merged.Add(r);
        }
        return merged;
    }

    public static Arbitrary<ValidInput> ValidLayer() => Arb.From(Gen.Choose(0, int.MaxValue).Select(GenerateValid), ShrinkValid);

    private static IEnumerable<ValidInput> ShrinkValid(ValidInput v) =>
        // the expected text is recomputed from scratch for the candidates that change it, so a shrink only keeps a candidate that is still valid
        v.Model.Blocks[0].Structs.Count > 1
            ? new[] { v with { Model = v.Model with { Blocks = new[] { v.Model.Blocks[0] with { Structs = v.Model.Blocks[0].Structs.Take(1).ToList() } }, DeleteSet = Array.Empty<DeleteClient>() }, ExpectedText = FirstPiece(v) } }
            : Array.Empty<ValidInput>();

    private static string FirstPiece(ValidInput v) => v.Model.Blocks[0].Structs[0].Content!.Text;

    // ------------------------------------------------------------------ hostile models

    public static ModelInput Generate(Profile profile, int caseSeed)
    {
        var rng = new Random(caseSeed);
        var format = rng.Next(2) == 0 ? UpdateFormat.V1 : UpdateFormat.V2;
        var blockCount = profile == Profile.Amp ? rng.Next(1, 4) : rng.Next(1, 4);
        var ids = new List<Id>();
        var blocks = new List<ClientBlock>();
        var clientPool = new List<ulong>();

        for (var b = 0; b < blockCount; b++)
        {
            var client = PickClient(rng, profile);
            clientPool.Add(client);
            var startClock = profile == Profile.Clock || rng.Next(8) == 0 ? PickClock(rng, profile) : 0;
            var structCount = profile == Profile.Amp ? rng.Next(50, 700) : rng.Next(1, 7);
            var structs = new List<Struct>();
            var clock = startClock;
            for (var i = 0; i < structCount; i++)
            {
                var st = GenStruct(rng, profile, client, clock, ids, clientPool);
                structs.Add(st);
                ids.Add(new Id(client, clock));
                clock = unchecked(clock + Math.Max(1, Math.Min(st.Units, 1UL << 20)));
            }
            ulong? structsClaim = profile == Profile.Len && rng.Next(2) == 0 ? ClaimAbove((ulong)structs.Count, rng) : null;
            blocks.Add(new ClientBlock(client, startClock, structs, structsClaim));
        }

        // duplicate client entries: last wins
        if (profile == Profile.Clock && rng.Next(3) == 0) blocks.Add(blocks[rng.Next(blocks.Count)] with { StartClock = PickClock(rng, profile) });
        // right-to-left block order, which the real writer never produces
        if (rng.Next(4) == 0) blocks.Reverse();

        var deletes = GenDeletes(rng, profile, clientPool);
        var model = new UpdateModel(blocks, deletes,
            NumClientsOverride: profile == Profile.Len && rng.Next(3) == 0 ? ClaimAbove((ulong)blocks.Count, rng) : null,
            NumDeleteClientsOverride: profile == Profile.Len && rng.Next(4) == 0 ? ClaimAbove((ulong)deletes.Count, rng) : null);
        return new ModelInput(format, model, $"hostile {profile} model");
    }

    private static ulong PickClient(Random rng, Profile profile)
    {
        if (profile is Profile.Clock or Profile.Len && rng.Next(12) == 0) return RareClients[rng.Next(RareClients.Length)];
        return Clients[rng.Next(Clients.Length)];
    }

    private static ulong PickClock(Random rng, Profile profile) =>
        profile == Profile.Clock && rng.Next(10) == 0 ? RareClocks[rng.Next(RareClocks.Length)] : Clocks[rng.Next(Clocks.Length)];

    private static ulong ClaimAbove(ulong actual, Random rng) =>
        rng.Next(4) switch { 0 => actual + 1, 1 => actual + 1000, 2 => 1UL << 32, _ => HugeLengths[rng.Next(HugeLengths.Length)] };

    private static Struct GenStruct(Random rng, Profile profile, ulong client, ulong clock, List<Id> ids, List<ulong> clientPool)
    {
        if (profile == Profile.Amp)
            return rng.Next(2) == 0 ? Struct.Gc(1) : Struct.Skip(1);

        var roll = rng.Next(10);
        if (roll == 0) return Struct.Gc(profile == Profile.Clock ? PickLength(rng) : (ulong)rng.Next(0, 4));
        if (roll == 1) return Struct.Skip(profile == Profile.Clock ? PickLength(rng) : (ulong)rng.Next(0, 4));

        var content = GenContent(rng, profile);
        Id? origin = null, rightOrigin = null;
        string? rootKey = RootKeys[rng.Next(RootKeys.Length)];
        Id? parentId = null;
        if (profile == Profile.Ref)
        {
            var refRoll = rng.Next(6);
            if (refRoll < 3) origin = RandomId(rng, ids, clientPool, client, clock);
            if (refRoll is 2 or 3) rightOrigin = RandomId(rng, ids, clientPool, client, clock);
            if (origin == null && rightOrigin == null && refRoll >= 4) { parentId = RandomId(rng, ids, clientPool, client, clock); rootKey = null; }
        }
        else if (ids.Count > 0 && rng.Next(3) == 0)
        {
            origin = ids[rng.Next(ids.Count)];
        }

        string? parentSub = rng.Next(6) == 0 ? "k" : null;
        byte? info = null;
        ulong? parentInfo = null;
        if (profile == Profile.Sem)
        {
            if (rng.Next(3) == 0) info = (byte)((rng.Next(0, 32) & 0x1f) | (parentSub != null ? 0x20 : 0) | (origin != null ? 0x80 : 0) | (rightOrigin != null ? 0x40 : 0));
            if (rng.Next(5) == 0) parentInfo = (ulong)new[] { 0, 2, 5, 127 }[rng.Next(4)];
        }
        return new Struct(StructKind.Item, Content: content, Origin: origin, RightOrigin: rightOrigin, RootKey: rootKey, ParentId: parentId,
            ParentSub: parentSub, InfoOverride: info, ParentInfoOverride: parentInfo);
    }

    private static Id RandomId(Random rng, List<Id> ids, List<ulong> clientPool, ulong self, ulong clock)
    {
        return rng.Next(5) switch
        {
            0 when ids.Count > 0 => ids[rng.Next(ids.Count)],
            1 => new Id(self, clock),                                   // itself
            2 => new Id(self, clock + (ulong)rng.Next(1, 4)),          // a later struct of the same client
            3 => new Id(clientPool[rng.Next(clientPool.Count)], (ulong)rng.Next(0, 3)),
            _ => new Id(Clients[rng.Next(Clients.Length)], (ulong)rng.Next(0, 5)),
        };
    }

    private static ulong PickLength(Random rng) => rng.Next(4) == 0 ? HugeLengths[rng.Next(HugeLengths.Length)] : (ulong)rng.Next(0, 6);

    private static Content GenContent(Random rng, Profile profile)
    {
        switch (rng.Next(profile == Profile.Sem ? 9 : 6))
        {
            case 0: return Content.Str(Words[rng.Next(Words.Length)]);
            case 1: return Content.DeletedOf(profile == Profile.Len ? PickLength(rng) : (ulong)rng.Next(0, 4));
            case 2: return Content.TypeOf((ulong)(profile == Profile.Sem ? rng.Next(0, 12) : rng.Next(0, 5)), "n");
            case 3: return Content.BinaryOf(Enumerable.Range(0, rng.Next(0, 4)).Select(i => (byte)i).ToArray());
            case 4: return Content.JsonOf(Enumerable.Range(0, rng.Next(0, 3)).Select(i => profile == Profile.Sem && rng.Next(2) == 0 ? "{{" : "1").ToArray()) with { CountOverride = profile == Profile.Len && rng.Next(3) == 0, Length = ClaimAbove(2, rng) };
            case 5:   // one integer; only a count above it is a claim (HOS-LEN expects a rejection exactly when some claim exceeds the data)
                return profile == Profile.Len && rng.Next(3) == 0
                    ? Content.AnyOf(ClaimAbove(1, rng), 0x7d, (byte)rng.Next(0, 100))
                    : Content.AnyOf(1, 0x7d, (byte)rng.Next(0, 100)) with { CountOverride = false };
            case 6: return new Content(ContentKind.Embed, Text: rng.Next(2) == 0 ? "{{" : "{}", Raw: new byte[] { 0x7e });
            case 7: return new Content(ContentKind.Format, Text: rng.Next(2) == 0 ? "nope" : "1", Name: "bold", Raw: new byte[] { 0x7e });
            default: return new Content(ContentKind.Doc, Text: "guid", Raw: rng.Next(2) == 0 ? Array.Empty<byte>() : new byte[] { 0x7e });
        }
    }

    private static List<DeleteClient> GenDeletes(Random rng, Profile profile, List<ulong> clientPool)
    {
        var result = new List<DeleteClient>();
        var clients = profile == Profile.Amp ? rng.Next(0, 4) : rng.Next(0, 3);
        for (var c = 0; c < clients; c++)
        {
            var rangeCount = profile == Profile.Amp ? rng.Next(1, 600) : rng.Next(0, 4);
            var ranges = new List<DeleteRange>();
            for (var r = 0; r < rangeCount; r++)
            {
                var clock = profile == Profile.Amp ? (ulong)(r * 2) : (ulong)rng.Next(0, 8);
                var length = profile is Profile.Amp or Profile.Len && rng.Next(4) == 0 ? HugeLengths[rng.Next(HugeLengths.Length)] : (ulong)rng.Next(0, 6);
                ranges.Add(new DeleteRange(clock, length));
            }
            ulong? claim = profile == Profile.Len && rng.Next(3) == 0 ? ClaimAbove((ulong)ranges.Count, rng) : null;
            result.Add(new DeleteClient(clientPool[rng.Next(clientPool.Count)], ranges, claim));
        }
        return result;
    }

    public static Arbitrary<ModelInput> Layer(Profile profile) =>
        Arb.From(Gen.Choose(0, int.MaxValue).Select(s => Generate(profile, s)), Shrink);

    /// <summary>Fewest structs first: whole blocks, then single structs, then delete clients and ranges, then claims, then content.</summary>
    public static IEnumerable<ModelInput> Shrink(ModelInput input)
    {
        var model = input.Model;
        for (var b = 0; b < model.Blocks.Count && model.Blocks.Count > 1; b++)
            yield return input with { Model = model with { Blocks = model.Blocks.Where((_, i) => i != b).ToList() } };
        for (var b = 0; b < model.Blocks.Count; b++)
        {
            var block = model.Blocks[b];
            if (block.Structs.Count > 1)
                yield return input with { Model = model with { Blocks = Replace(model.Blocks, b, block with { Structs = block.Structs.Take(block.Structs.Count / 2).ToList(), NumStructsOverride = null }) } };
            for (var s = 0; s < block.Structs.Count && block.Structs.Count > 1 && s < 32; s++)
                yield return input with { Model = model with { Blocks = Replace(model.Blocks, b, block with { Structs = block.Structs.Where((_, i) => i != s).ToList(), NumStructsOverride = null }) } };
        }
        for (var d = 0; d < model.DeleteSet.Count; d++)
            yield return input with { Model = model with { DeleteSet = model.DeleteSet.Where((_, i) => i != d).ToList(), NumDeleteClientsOverride = null } };
        if (model.NumClientsOverride != null) yield return input with { Model = model with { NumClientsOverride = null } };
        if (model.NumDeleteClientsOverride != null) yield return input with { Model = model with { NumDeleteClientsOverride = null } };
        if (input.Format == UpdateFormat.V2) yield return input with { Format = UpdateFormat.V1 };
    }

    private static List<ClientBlock> Replace(IReadOnlyList<ClientBlock> blocks, int index, ClientBlock block)
    {
        var list = blocks.ToList();
        list[index] = block;
        return list;
    }
}
