using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Xunit;
using YSharp.TestKit;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

[LibraryIndependent]
public class PrngGoldenTests
{
    private static JsonElement LoadGoldens()
    {
        using var s = typeof(Xorshift32).Assembly.GetManifestResourceStream("YSharp.TestKit.Goldens.prng-goldens.json")!;
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    private static string Bits(double d) => BitConverter.DoubleToInt64Bits(d).ToString("x16");

    public static IEnumerable<TheoryDataRow<int>> SeedIndexes()
    {
        var n = LoadGoldens().GetProperty("seeds").GetArrayLength();
        for (var i = 0; i < n; i++) yield return new TheoryDataRow<int>(i);
    }

    [Theory]
    [MemberData(nameof(SeedIndexes))]
    public void Xorshift32_Seeded_MatchesLib0Bits(int index)
    {
        var g = LoadGoldens().GetProperty("seeds")[index];
        var gen = new Xorshift32(g.GetProperty("seed").GetUInt32());
        foreach (var expected in g.GetProperty("xorshift32").EnumerateArray())
            Assert.Equal(expected.GetString(), Bits(gen.Next()));
    }

    [Theory]
    [MemberData(nameof(SeedIndexes))]
    public void Xoroshiro128Plus_Seeded_StateAndDrawsMatchLib0(int index)
    {
        var g = LoadGoldens().GetProperty("seeds")[index];
        var gen = new Xoroshiro128Plus(g.GetProperty("seed").GetUInt32());
        var state = g.GetProperty("state");
        for (var i = 0; i < 4; i++) Assert.Equal(state[i].GetUInt32(), gen.State[i]);
        foreach (var expected in g.GetProperty("xoroshiro").EnumerateArray())
            Assert.Equal(expected.GetString(), Bits(gen.Next()));
    }

    [Theory]
    [MemberData(nameof(SeedIndexes))]
    public void DrawHelpers_ReplayedInOrder_MatchLib0Results(int index)
    {
        var g = LoadGoldens().GetProperty("seeds")[index];
        var gen = Prng.Create(g.GetProperty("seed").GetUInt32());
        foreach (var step in g.GetProperty("script").EnumerateArray())
        {
            var call = step.GetProperty("call");
            var name = call[0].GetString()!;
            var result = step.GetProperty("result");
            switch (result.GetProperty("kind").GetString())
            {
                case "bool":
                    Assert.Equal(result.GetProperty("value").GetBoolean(), Prng.Bool(gen));
                    break;
                case "bytes":
                    Assert.Equal(result.GetProperty("hex").GetString(), Convert.ToHexString(Prng.Uint8Array(gen, call[1].GetInt32())).ToLowerInvariant());
                    break;
                case "string":
                    var units = new List<char>();
                    foreach (var u in result.GetProperty("units").EnumerateArray()) units.Add((char)u.GetInt32());
                    Assert.Equal(new string(units.ToArray()), CallString(gen, name, call));
                    break;
                default:
                    Assert.Equal(result.GetProperty("bits").GetString(), Bits(CallNumber(gen, name, call)));
                    break;
            }
        }
    }

    private static string CallString(IPrng gen, string name, JsonElement call) => name switch
    {
        "char" => Prng.Char(gen),
        "letter" => Prng.Letter(gen),
        "word" => call.GetArrayLength() switch { 1 => Prng.Word(gen), 3 => Prng.Word(gen, call[1].GetInt32(), call[2].GetInt32()), _ => throw new InvalidOperationException() },
        "utf16Rune" => Prng.Utf16Rune(gen),
        "utf16String" => call.GetArrayLength() == 1 ? Prng.Utf16String(gen) : Prng.Utf16String(gen, call[1].GetInt32()),
        _ => throw new InvalidOperationException(name),
    };

    private static double CallNumber(IPrng gen, string name, JsonElement call)
    {
        switch (name)
        {
            case "int32": return Prng.Int32(gen, call[1].GetDouble(), call[2].GetDouble());
            case "int31": return Prng.Int31(gen, call[1].GetDouble(), call[2].GetDouble());
            case "uint32": return Prng.Uint32(gen, call[1].GetDouble(), call[2].GetDouble());
            case "int53": return Prng.Int53(gen, call[1].GetDouble(), call[2].GetDouble());
            case "uint53": return Prng.Uint53(gen, call[1].GetDouble(), call[2].GetDouble());
            case "real53": return Prng.Real53(gen);
            case "oneOf":
                var items = new List<double>();
                foreach (var e in call[1].EnumerateArray()) items.Add(e.GetDouble());
                return Prng.OneOf(gen, items);
            default: throw new InvalidOperationException(name);
        }
    }

    [Fact]
    public void Xoroshiro128Plus_FreshToggle_AlternatesBetweenTwoHalves()
    {
        var a = new Xoroshiro128Plus(7);
        Assert.True(a.Fresh);
        a.Next();
        Assert.False(a.Fresh);
        a.Next();
        Assert.True(a.Fresh);
    }
}
