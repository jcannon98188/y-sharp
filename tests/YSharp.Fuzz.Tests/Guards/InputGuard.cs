using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace YSharp.Fuzz.Guards;

/// <summary>
/// Calibrated guard constants (ADR 0013). They start at the ADR's values and are ratcheted down as the corpus shows
/// real headroom: change them only downward, with the measured corpus maximum in the commit message.
/// </summary>
internal static class Budget
{
    /// <summary>Per-input wall-clock limit; a timeout is a y-sharp hang.</summary>
    public static readonly TimeSpan InputTimeout = TimeSpan.FromMilliseconds(EnvInt("YSHARP_FUZZ_TIMEOUT_MS", 2000));

    /// <summary>Bytes allocated per input byte (k of the k*N+C bound). ADR 0009 / 0013 start value.</summary>
    public const long BytesPerInputByte = 64;

    /// <summary>Constant allowance (C of the k*N+C bound): the document, its root types and the result.</summary>
    public const long ConstantBytes = 1024 * 1024;

    /// <summary>Probe-thread stack: small so recursion on input depth fails fast.</summary>
    public const int ProbeStackBytes = 256 * 1024;

    public static long AllocationBound(long inputBytes) => BytesPerInputByte * inputBytes + ConstantBytes;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}

/// <summary>What the guarded call did, measured on the probe thread.</summary>
internal sealed record GuardResult<T>(T Value, long AllocatedBytes, TimeSpan Elapsed);

/// <summary>
/// Runs decode and apply on a probe thread with a 256 KB stack and a per-input timeout (ADR 0013 Guards). A timeout fails the
/// test as a hang (the thread is abandoned, it is a background thread); an exception from the body is re-thrown on the caller
/// with its original stack, so a <see cref="NotImplementedException"/> from the stub still skips the test. Allocation is
/// counted on the probe thread with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
internal static class InputGuard
{
    public static GuardResult<T> Run<T>(string label, Func<T> body, TimeSpan? timeout = null, int stackBytes = Budget.ProbeStackBytes)
    {
        T value = default!;
        ExceptionDispatchInfo? error = null;
        long allocated = 0;
        var elapsed = TimeSpan.Zero;

        var thread = new Thread(() =>
        {
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                value = body();
                elapsed = watch.Elapsed;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }, stackBytes)
        {
            IsBackground = true,
            Name = "ysharp-fuzz-probe",
        };
        thread.Start();

        var limit = timeout ?? Budget.InputTimeout;
        if (!thread.Join(limit))
        {
            Assert.Fail($"HANG: {label} did not finish within {limit.TotalMilliseconds:0} ms (the probe thread was abandoned).");
        }
        error?.Throw();
        return new GuardResult<T>(value, allocated, elapsed);
    }

    public static void Run(string label, Action body, TimeSpan? timeout = null, int stackBytes = Budget.ProbeStackBytes) =>
        Run<object?>(label, () => { body(); return null; }, timeout, stackBytes);

    /// <summary>Fails when the guarded call allocated more than k*N+C bytes for an input of <paramref name="inputBytes"/> bytes.</summary>
    public static void AssertAllocationWithinBound(string label, long allocated, long inputBytes)
    {
        var bound = Budget.AllocationBound(inputBytes);
        if (allocated > bound)
            Assert.Fail($"OVER-ALLOCATION: {label} allocated {allocated:N0} bytes for {inputBytes:N0} input bytes; the bound is {Budget.BytesPerInputByte}*N+{Budget.ConstantBytes:N0} = {bound:N0}.");
    }
}
