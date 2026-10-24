namespace YSharp.StoreSpike.Content;

/// <summary>
/// A reference into a shared chunked buffer. Splitting an item in two produces two slices over the same chunk: no copy and no allocation (ADR 0018, Content).
/// </summary>
public readonly record struct ContentSlice(int Chunk, int Start, int Length);
