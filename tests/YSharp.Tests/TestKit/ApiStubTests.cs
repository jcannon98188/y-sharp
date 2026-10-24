using System;
using Xunit;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.TestKit;

/// <summary>
/// Infrastructure tests of the stub probe (ADR 0019). A NotImplementedException is a skip only while the library is the
/// API stub; once implemented it must reach the test runner as a failure.
/// </summary>
public sealed class ApiStubTests
{
    private static void ThrowsNotImplemented() => throw new NotImplementedException("stub");

    private static void Returns() { }

    private static void ThrowsOther() => throw new InvalidOperationException("other");

    [Fact]
    public void Detect_ProbeThrowsNotImplemented_IsStub() => Assert.True(ApiStub.Detect(ThrowsNotImplemented));

    [Fact]
    public void Detect_ProbeReturns_IsNotStub() => Assert.False(ApiStub.Detect(Returns));

    [Fact]
    public void Detect_ProbeThrowsSomethingElse_IsNotStub() => Assert.False(ApiStub.Detect(ThrowsOther));

    [Fact]
    public void Evaluate_WhileStub_ConvertsNotImplementedToSkip()
    {
        var outcome = ApiStub.Evaluate(isStub: true, ThrowsNotImplemented);
        Assert.Equal(ApiStub.Outcome.Skip, outcome);
    }

    [Fact]
    public void Evaluate_WhileStub_PassingBodyPasses() =>
        Assert.Equal(ApiStub.Outcome.Pass, ApiStub.Evaluate(isStub: true, Returns));

    [Fact]
    public void Evaluate_AfterImplementation_NotImplementedPropagates() =>
        Assert.Throws<NotImplementedException>(() => ApiStub.Evaluate(isStub: false, ThrowsNotImplemented));

    [Fact]
    public void Evaluate_AfterImplementation_OtherExceptionsPropagateInBothModes()
    {
        Assert.Throws<InvalidOperationException>(() => ApiStub.Evaluate(isStub: false, ThrowsOther));
        Assert.Throws<InvalidOperationException>(() => ApiStub.Evaluate(isStub: true, ThrowsOther));
    }

    [Fact]
    public void IsStub_MatchesTheRealLibrary()
    {
        // Computed once from YValue.Null; agrees with a direct probe of the same member.
        var direct = ApiStub.Detect(() => _ = YValue.Null);
        Assert.Equal(direct, ApiStub.IsStub);
    }
}
