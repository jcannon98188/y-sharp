using Xunit;
using YSharp.Interop;

// One Node child per test run (ADR 0012): started lazily by the first live test, verified against the pinned versions, shared by every test.
[assembly: AssemblyFixture(typeof(OracleHost))]
