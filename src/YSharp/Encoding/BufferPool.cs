#nullable enable
using System.Buffers;
using System.Threading;

namespace YSharp.Lib0;

/// <summary>
/// The one place encoders rent byte buffers (ADR 0018: every rented buffer is returned on every exit path). It counts what is
/// out so the hostile-input post-condition (ADR 0013) can check that nothing stays rented after a failed decode or apply.
/// </summary>
internal static class BufferPool
{
    private static long outstanding;

    /// <summary>The number of rented buffers not yet returned, process-wide.</summary>
    public static long Outstanding => Interlocked.Read(ref outstanding);

    public static byte[] Rent(int minimumLength)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
        Interlocked.Increment(ref outstanding);
        return buffer;
    }

    public static void Return(byte[] buffer)
    {
        Interlocked.Decrement(ref outstanding);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
