namespace YSharp.TestKit.Rng;

/// <summary>A source of doubles in [0,1), the only thing the lib0 draw helpers need.</summary>
public interface IPrng
{
    /// <summary>Next float in [0,1).</summary>
    double Next();
}

/// <summary>Bit-exact port of lib0 <c>prng/Xorshift32.js</c>; seeds the Xoroshiro state.</summary>
public sealed class Xorshift32 : IPrng
{
    private int _state;

    public Xorshift32(uint seed)
    {
        Seed = seed;
        _state = unchecked((int)seed);
    }

    public uint Seed { get; }

    public double Next()
    {
        // JS: x ^= x << 13; x ^= x >> 17 (arithmetic, signed); x ^= x << 5
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return unchecked((uint)x) / 4294967296.0;
    }
}
