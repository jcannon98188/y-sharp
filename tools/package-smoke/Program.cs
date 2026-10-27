// Installed from the packed packages by smoke.sh (TASK-15): a doc round-trips an update, a server serves an edit, and the conformance suite runs.
using System;
using System.Linq;
using YSharp;
using YSharp.Server;
using YSharp.Server.Testing;

var alice = new Doc();
alice.GetText("notes").Insert(0, "Hello from the package");
var bob = new Doc();
var applied = bob.TryApplyUpdate(alice.EncodeStateAsUpdate());
var text = bob.GetText("notes").ToString();
if (!applied.Ok || text != "Hello from the package") return Fail($"round-trip gave {applied.Error} \"{text}\"");

await using (var server = new YServer())
{
    await server.EditAsync("doc", tx => tx.Doc.GetText("t").Insert(0, "served"));
    var read = await server.ReadAsync("doc", d => d.GetText("t").ToString());
    if (read != "served") return Fail($"server read \"{read}\"");
}

var results = await new InMemoryUpdateStoreConformance().RunAsync();
var failed = results.Where(r => r.Outcome == ConformanceOutcome.Failed).ToList();
if (failed.Count > 0) return Fail($"{failed.Count} conformance checks failed");

Console.WriteLine($"package smoke passed ({results.Count} conformance checks)");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"package smoke FAILED: {message}");
    return 1;
}
