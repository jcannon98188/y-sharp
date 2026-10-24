using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Traceability;

[TraceabilitySample]
public class SampleTests
{
    [Fact(Explicit = true)]
    [Covers("ZZ-1", "ZZ-2")]
    public void Covered() { }

    [Fact(Explicit = true)]
    [PortOf("sample.js:testSample", "ZZ-3")]
    public void Ported() { }

    [Fact(Explicit = true)]
    [Diverges("ZZ-4", adr: "0003")]
    public void Diverged() { }

    [Fact(Explicit = true)]
    public void Untagged() { }

    internal void NotATest() { }
}

public class ScannerAndAttributeTests
{
    private static readonly TestTag[] Tags = TestScanner.Scan(new[] { typeof(SampleTests).Assembly }, includeSamples: true).ToArray();
    private static TestTag Sample(string method) => Tags.Single(t => t.Test == "YSharp.Tests.Traceability.SampleTests." + method);

    [Fact]
    public void Covers_Applied_IsExposedAsFilterableTrait()
    {
        var traits = new CoversAttribute("A-1", "A-2").GetTraits().Select(t => (t.Key, t.Value));
        Assert.Equal(new[] { ("Covers", "A-1"), ("Covers", "A-2") }, traits);
    }

    [Fact]
    public void PortOf_And_Diverges_ExposeTraits()
    {
        Assert.Equal(new[] { ("PortOf", "f.js:testX"), ("Covers", "M-1") }, new PortOfAttribute("f.js:testX", "M-1").GetTraits().Select(t => (t.Key, t.Value)));
        Assert.Equal(new[] { ("Diverges", "Q17"), ("Covers", "Q17") }, new DivergesAttribute("Q17", adr: "0003").GetTraits().Select(t => (t.Key, t.Value)));
    }

    [Fact]
    public void Scanner_TaggedTestMethods_AreRead()
    {
        var c = Sample("Covered");
        Assert.Equal(new[] { "ZZ-1", "ZZ-2" }, c.Covers);
        Assert.Equal(CoverLayer.Unit, c.Layer);
        var p = Sample("Ported");
        Assert.Equal(CoverLayer.Port, p.Layer);
        Assert.Equal(new[] { "sample.js:testSample" }, p.PortOf);
        Assert.Equal(new[] { "ZZ-3" }, p.Covers);
        Assert.Equal(new[] { "ZZ-4" }, Sample("Diverged").Diverges);
    }

    [Fact]
    public void Scanner_UntaggedAndNonTestMethods_AreIgnored()
    {
        Assert.DoesNotContain(Tags, t => t.Test.EndsWith(".Untagged") || t.Test.EndsWith(".NotATest"));
    }

    [Fact]
    public void RealScan_SampleClasses_AreSkipped()
    {
        var real = TestScanner.Scan(new[] { typeof(SampleTests).Assembly });
        Assert.DoesNotContain(real, t => t.Covers.Any(c => c.StartsWith("ZZ-")));
    }
}
