using System;
using Xunit;

namespace YSharp.TestKit.Stubs;

/// <summary>
/// The one place that knows whether y-sharp is still the Spec 1 API stub (ADR 0019). Tests compile against the stub and skip
/// while it is one; a <see cref="NotImplementedException"/> is a skip only while <see cref="IsStub"/> is true. After the
/// implementation lands, the same exception is a failure and must not be swallowed.
/// </summary>
public static class ApiStub
{
    public const string Reason = "YSharp is still the API stub (ADR 0019).";

    public enum Outcome { Pass, Skip }

    private static readonly Lazy<bool> Computed = new(() => Detect(() => _ = YValue.Null));

    /// <summary>True while a known stub member (<c>YValue.Null</c>) throws <see cref="NotImplementedException"/>. Computed once.</summary>
    public static bool IsStub => Computed.Value;

    /// <summary>The probe logic with an injected probe: stub only if the probe throws <see cref="NotImplementedException"/>.</summary>
    public static bool Detect(Action probe)
    {
        try { probe(); return false; }
        catch (NotImplementedException) { return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Runs <paramref name="body"/>. A <see cref="NotImplementedException"/> yields <see cref="Outcome.Skip"/> when
    /// <paramref name="isStub"/>, and propagates otherwise; every other exception propagates.
    /// </summary>
    public static Outcome Evaluate(bool isStub, Action body)
    {
        try { body(); return Outcome.Pass; }
        catch (NotImplementedException) when (isStub) { return Outcome.Skip; }
    }

    /// <summary>Skips the current test while the library is the stub.</summary>
    public static void SkipIfStub()
    {
        if (IsStub) Assert.Skip(Reason);
    }

    /// <summary>Runs <paramref name="body"/>; a stub's NotImplementedException skips the test, anything after implementation fails it.</summary>
    public static void Run(Action body)
    {
        if (Evaluate(IsStub, body) == Outcome.Skip) Assert.Skip(Reason);
    }

    public static T Run<T>(Func<T> body)
    {
        T result = default!;
        if (Evaluate(IsStub, () => result = body()) == Outcome.Skip) Assert.Skip(Reason);
        return result;
    }
}
