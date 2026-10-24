namespace YSharp.StoreSpike.Content;

/// <summary>The chunk ceiling of ADR 0018: no document-owned allocation grows past about 64 KB, so steady-state edits never reach the large object heap.</summary>
public static class ChunkLimits
{
    public const int ChunkBytes = 64 * 1024;
}
