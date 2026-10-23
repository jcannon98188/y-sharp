using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.v3;

namespace YSharp.TestKit.Traceability;

/// <summary>The layer a test covers a registry entry at (ADR 0010).</summary>
public enum CoverLayer { Auto, Unit, Port, Fixture, Differential, Fuzz }

/// <summary>
/// Marks a test as covering registry IDs (ADR 0010). Implements xUnit v3's trait interface, so
/// <c>--filter-trait "Covers=SIM-10"</c> selects the tests for one ID. <see cref="CoverLayer.Auto"/> infers the layer
/// from the assembly: Fuzz.Tests is fuzz, Interop.Tests is differential, anything else is unit.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class CoversAttribute : Attribute, ITraitAttribute
{
    public CoversAttribute(string id, params string[] moreIds) => Ids = new[] { id }.Concat(moreIds).ToArray();

    public IReadOnlyList<string> Ids { get; }
    public CoverLayer Layer { get; init; } = CoverLayer.Auto;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        Ids.Select(i => new KeyValuePair<string, string>("Covers", i)).ToArray();
}

/// <summary>Marks a ported JS test: <c>[PortOf("y-map.tests.js:testMapHavingIterableAsConstructor", "M-10")]</c>. The first argument is the ledger key.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class PortOfAttribute : Attribute, ITraitAttribute
{
    public PortOfAttribute(string upstream, params string[] ids)
    {
        Upstream = upstream;
        Ids = ids;
    }

    public string Upstream { get; }
    public IReadOnlyList<string> Ids { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        new[] { new KeyValuePair<string, string>("PortOf", Upstream) }
            .Concat(Ids.Select(i => new KeyValuePair<string, string>("Covers", i))).ToArray();
}

/// <summary>Marks the test that shows the JS behaviour for a recorded divergence: <c>[Diverges("Q17", adr: "0003")]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class DivergesAttribute : Attribute, ITraitAttribute
{
    public DivergesAttribute(string id, string? adr = null)
    {
        Id = id;
        Adr = adr;
    }

    public string Id { get; }
    public string? Adr { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        new[] { new KeyValuePair<string, string>("Diverges", Id), new KeyValuePair<string, string>("Covers", Id) };
}

/// <summary>Marks a class of sample tests used to test the scanner; the real traceability scan skips it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TraceabilitySampleAttribute : Attribute { }
