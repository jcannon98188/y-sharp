using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Tests.Structs;
using YSharp.Tests.Types;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;

namespace YSharp.Tests.Simulation;

/// <summary>The questions plans ask, answered from a live document.</summary>
internal sealed class DocView : ISimView
{
    private readonly Doc _doc;
    private readonly bool _array;

    public DocView(Doc doc, string kind)
    {
        _doc = doc;
        _array = kind == "array";
    }

    public int Length => _array ? _doc.GetArray("array").Length : _doc.GetText("text").Length;
    public int StringLength => _doc.GetText("text").ToString().Length;

    public int NestedArrayLength(int index)
    {
        var e = _doc.GetArray("array")[index];
        return e.Kind == YEntryKind.Type && e.Type is YArray a ? a.Length : -1;
    }
}

/// <summary>Runs plan steps on the simulation's documents through the ops interpreter, so the recorded op log is exactly what ran.</summary>
internal sealed class SimExecutor
{
    private static readonly OpsRegistry Registry = SimSteps.CreateRegistry();
    private static readonly ConditionalWeakTable<TestConnector, SimExecutor> Executors = new();

    private readonly OpsContext _ctx = new(Registry);

    private SimExecutor(TestConnector connector)
    {
        foreach (var user in connector.AllConns) _ctx.DeclareReplica(SimLog.Name(user.UserId), user.Doc);
    }

    public static SimExecutor For(TestConnector connector) => Executors.GetValue(connector, c => new SimExecutor(c));

    public void Run(JsonObject step)
    {
        var script = OpsScript.Parse("{\"schema\":1,\"steps\":[" + step.ToJsonString() + "]}", Registry);
        OpsInterpreter.RunSteps(_ctx, script.Steps);
    }
}

/// <summary>
/// The upstream assertions that sit inside the generators: after an array insert or delete the array equals a mirror list (it
/// exercises the search markers), after a text insert or delete the string equals the mirror string. They are the only checks
/// on the document while the run is in flight; convergence is checked by <see cref="SimHarness.Compare"/>.
/// </summary>
internal static class SimMirror
{
    public static Action? Begin(string kind, int mod, Doc doc, IReadOnlyList<JsonObject> steps)
    {
        if (kind == "array" && mod == 0) return Array(doc, steps[0], insert: true);
        if (kind == "array" && mod == 4 && steps.Count == 1 && steps[0]["target"] is JsonObject) return Array(doc, steps[0], insert: false);
        if (kind == "text" && mod == 0) return Text(doc, steps[0], insert: true);
        if (kind == "text" && mod == 1) return Text(doc, steps[0], insert: false);
        return null;
    }

    /// <summary>A cheap element key: numbers by value, nested types by kind and size (nested content is not what the mirror guards).</summary>
    private static string Key(YEntry e)
    {
        if (e.Kind == YEntryKind.Type)
            return e.Type switch { YArray a => "A" + a.Length, YMap m => "M" + m.Count, var t => t!.GetType().Name };
        var v = e.Value;
        return v.TryGetNumber(out var n) ? "N" + n : v.Kind == YValueKind.Null ? "null" : "V" + v.Json();
    }

    private static Action Array(Doc doc, JsonObject step, bool insert)
    {
        var array = doc.GetArray("array");
        var expected = array.ToArray().Select(Key).ToList();
        var at = step["at"]!.GetValue<int>();
        if (insert)
            expected.InsertRange(at, step["values"]!.AsArray().Select(v => v == null ? "null" : "N" + (double)v.GetValue<int>()));
        else
            expected.RemoveRange(at, step["len"]!.GetValue<int>());
        return () => Assert.Equal(expected, array.ToArray().Select(Key).ToList());
    }

    private static Action Text(Doc doc, JsonObject step, bool insert)
    {
        var text = doc.GetText("text");
        var before = text.ToString();
        var at = step["at"]!.GetValue<int>();
        var expected = insert
            ? before[..at] + step["value"]!.GetValue<string>() + before[at..]
            : before[..at] + before[(at + step["len"]!.GetValue<int>())..];
        return () => Assert.Equal(expected, text.ToString());
    }
}

/// <summary>The convergence checks the generators' tests add after <c>compare</c>.</summary>
internal static class SimChecks
{
    /// <summary>
    /// SIM-01. Upstream <c>checkResult</c> maps <c>result.users[i]</c> twice and compares it with itself, so it can never fail.
    /// This returns the index of the first replica whose key differs from replica 0, or -1 when all agree.
    /// </summary>
    public static int FirstDivergentReplica(IReadOnlyList<string> keys)
    {
        for (var i = 1; i < keys.Count; i++)
            if (!string.Equals(keys[0], keys[i], StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The text delta with embedded types as their JSON (upstream <c>typeToObject</c>).</summary>
    public static string DeltaKey(YText text) => text.DeltaJson();

    public static void CheckResult(IReadOnlyList<TestYInstance> users, bool cleanup)
    {
        var keys = users.Select(u => DeltaKey(u.Doc.GetText("text"))).ToList();
        var bad = FirstDivergentReplica(keys);
        Assert.True(bad < 0, $"replica {bad} and replica 0 hold different text:{Environment.NewLine}{(bad < 0 ? "" : keys[0])}{Environment.NewLine}{(bad < 0 ? "" : keys[bad])}");
        if (cleanup) Assert.Equal(0, TextProbe.CleanupFormatting(users[0].Doc.GetText("text")));
    }
}

/// <summary>
/// The seam for <c>Y.cleanupYTextFormatting</c> (upstream tests assert it returns 0 after convergence). The public API sketch has no
/// counterpart, so the tests need <c>internal int YText.CleanupFormatting()</c>: the number of redundant format items it removed.
/// </summary>
internal static class TextProbe
{
    private static readonly MethodInfo? Method =
        typeof(YText).GetMethod("CleanupFormatting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);

    public static int CleanupFormatting(YText text)
    {
        if (Method == null)
            throw new InvalidOperationException("YText.CleanupFormatting() is missing. Spec 10 needs `internal int CleanupFormatting()` on YText (JS cleanupYTextFormatting), see TextProbe.");
        try { return (int)Method.Invoke(text, null)!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }
}

/// <summary>Turns plans into the mods <see cref="SimHarness.ApplyRandomTests{T}"/> draws from (one per generator function).</summary>
internal static class SimDrive
{
    public static IReadOnlyList<Action<TestYInstance, IPrng, object?>> Mods(string kind)
    {
        var plans = SimPlans.For(kind);
        var mods = new List<Action<TestYInstance, IPrng, object?>>();
        for (var i = 0; i < plans.Count; i++)
        {
            var index = i;
            mods.Add((user, gen, _) => Apply(kind, index, plans[index], user, gen));
        }
        return mods;
    }

    private static void Apply(string kind, int mod, SimPlan plan, TestYInstance user, IPrng gen)
    {
        var steps = plan(gen, SimLog.Name(user.UserId), new DocView(user.Doc, kind), user.Tc.Counters);
        var verify = SimMirror.Begin(kind, mod, user.Doc, steps);
        var executor = SimExecutor.For(user.Tc);
        foreach (var step in steps)
        {
            user.Tc.Recorder?.Edit(step);
            executor.Run(step);
        }
        verify?.Invoke();
    }
}
