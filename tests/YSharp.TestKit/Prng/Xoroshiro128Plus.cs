namespace YSharp.TestKit.Prng;

/// <summary>
/// Bit-exact port of lib0 <c>prng/Xoroshiro128plus.js</c> (32-bit variant, two draws per 64-bit step,
/// the add carry of the low half is not propagated). Ported random tests reproduce upstream seeds with it (ADR 0015).
/// </summary>
public sealed class Xoroshiro128Plus : IPrng
{
    private readonly uint[] _state = new uint[4];

    public Xoroshiro128Plus(uint seed)
    {
        Seed = seed;
        var x = new Xorshift32(seed);
        for (var i = 0; i < 4; i++)
        {
            // JS: Uint32Array store of (next() * BITS32): truncate toward zero, modulo 2^32.
            _state[i] = unchecked((uint)(long)(x.Next() * 4294967295.0));
        }
        Fresh = true;
    }

    public uint Seed { get; }

    /// <summary>The <c>_fresh</c> toggle: true when the next draw is the first half of a 64-bit step.</summary>
    public bool Fresh { get; private set; }

    public System.ReadOnlySpan<uint> State => _state;

    public double Next()
    {
        var s = _state;
        if (Fresh)
        {
            Fresh = false;
            return unchecked(s[0] + s[2]) / 4294967296.0;
        }
        Fresh = true;
        var s0 = s[0];
        var s1 = s[1];
        var s2 = s[2] ^ s0;
        var s3 = s[3] ^ s1;
        unchecked
        {
            s[0] = (s1 << 23 | s0 >> 9) ^ s2 ^ (s2 << 14 | s3 >> 18);
            s[1] = (s0 << 23 | s1 >> 9) ^ s3 ^ (s3 << 14);
            s[2] = s3 << 4 | s2 >> 28;
            s[3] = s2 << 4 | s3 >> 28;
            return (s[1] + s[3]) / 4294967296.0;
        }
    }
}
