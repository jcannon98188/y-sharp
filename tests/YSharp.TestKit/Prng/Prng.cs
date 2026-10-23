using System;
using System.Collections.Generic;
using System.Text;

namespace YSharp.TestKit.Rng;

/// <summary>
/// Port of the lib0 <c>prng.js</c> draw helpers. Numbers are doubles, as in JS, and every helper consumes
/// exactly the draws upstream does, so seeds replay (ADR 0015).
/// </summary>
public static class Prng
{
    public static IPrng Create(uint seed) => new Xoroshiro128Plus(seed);

    public static bool Bool(IPrng gen) => gen.Next() >= 0.5;

    public static double Int53(IPrng gen, double min, double max) => Math.Floor(gen.Next() * (max + 1 - min) + min);

    public static double Uint53(IPrng gen, double min, double max) => Math.Abs(Int53(gen, min, max));

    public static double Int32(IPrng gen, double min, double max) => Math.Floor(gen.Next() * (max + 1 - min) + min);

    /// <summary>JS <c>int32(...) &gt;&gt;&gt; 0</c>.</summary>
    public static double Uint32(IPrng gen, double min, double max) => unchecked((uint)(long)Int32(gen, min, max));

    public static double Int31(IPrng gen, double min, double max) => Int32(gen, min, max);

    public static double Real53(IPrng gen) => gen.Next();

    public static string Char(IPrng gen) => ((char)(int)Int31(gen, 32, 126)).ToString();

    public static string Letter(IPrng gen) => ((char)(int)Int31(gen, 97, 122)).ToString();

    public static string Word(IPrng gen, int minLen = 0, int maxLen = 20)
    {
        var len = (int)Int31(gen, minLen, maxLen);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++) sb.Append((char)(int)Int31(gen, 97, 122));
        return sb.ToString();
    }

    public static string Utf16Rune(IPrng gen) => ((char)(int)Int31(gen, 0, 256)).ToString();

    public static string Utf16String(IPrng gen, int maxLen = 20)
    {
        var len = (int)Int31(gen, 0, maxLen);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++) sb.Append(Utf16Rune(gen));
        return sb.ToString();
    }

    public static T OneOf<T>(IPrng gen, IReadOnlyList<T> items) => items[(int)Int31(gen, 0, items.Count - 1)];

    public static byte[] Uint8Array(IPrng gen, int len)
    {
        var buf = new byte[len];
        for (var i = 0; i < buf.Length; i++) buf[i] = (byte)Int32(gen, 0, 255);
        return buf;
    }
}
