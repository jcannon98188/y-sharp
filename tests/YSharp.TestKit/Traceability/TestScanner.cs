using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace YSharp.TestKit.Traceability;

/// <summary>Reflects over built test assemblies and reads the traceability attributes on their tests.</summary>
public static class TestScanner
{
    /// <summary>Classes carrying this attribute are samples for the scanner's own tests and are skipped by the real scan.</summary>
    public const string SampleMarker = "TraceabilitySampleAttribute";

    public static List<TestTag> Scan(IEnumerable<Assembly> assemblies, bool includeSamples = false)
    {
        var tags = new List<TestTag>();
        foreach (var asm in assemblies)
            foreach (var type in SafeTypes(asm).Where(t => t.IsClass && (includeSamples || !IsSample(t))))
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!IsTest(m)) continue;
                    var tag = ReadTag(asm, type, m);
                    if (tag != null) tags.Add(tag);
                }
        return tags;
    }

    public static TestTag Read(MethodInfo m) => ReadTag(m.DeclaringType!.Assembly, m.DeclaringType, m) ?? new TestTag(Name(m.DeclaringType, m), CoverLayer.Unit, new string[0], new string[0], new string[0]);

    private static TestTag? ReadTag(Assembly asm, Type type, MethodInfo m)
    {
        var covers = new List<string>();
        var diverges = new List<string>();
        var portOf = new List<string>();
        var explicitLayer = CoverLayer.Auto;
        var isPort = false;
        foreach (var provider in new ICustomAttributeProvider[] { type, m })
        {
            foreach (var a in provider.GetCustomAttributes(typeof(CoversAttribute), true).Cast<CoversAttribute>())
            {
                covers.AddRange(a.Ids);
                if (a.Layer != CoverLayer.Auto) explicitLayer = a.Layer;
            }
            foreach (var a in provider.GetCustomAttributes(typeof(PortOfAttribute), true).Cast<PortOfAttribute>())
            {
                portOf.Add(a.Upstream);
                covers.AddRange(a.Ids);
                isPort = true;
            }
            foreach (var a in provider.GetCustomAttributes(typeof(DivergesAttribute), true).Cast<DivergesAttribute>())
                diverges.Add(a.Id);
        }
        if (covers.Count == 0 && diverges.Count == 0 && portOf.Count == 0) return null;
        var layer = isPort ? CoverLayer.Port : explicitLayer != CoverLayer.Auto ? explicitLayer : InferLayer(asm);
        return new TestTag(Name(type, m), layer, covers.Distinct().ToArray(), diverges.Distinct().ToArray(), portOf.Distinct().ToArray());
    }

    private static CoverLayer InferLayer(Assembly asm)
    {
        var n = asm.GetName().Name ?? "";
        return n.EndsWith(".Fuzz.Tests", StringComparison.Ordinal) ? CoverLayer.Fuzz
            : n.EndsWith(".Interop.Tests", StringComparison.Ordinal) ? CoverLayer.Differential
            : CoverLayer.Unit;
    }

    private static string Name(Type type, MethodInfo m) => $"{type.FullName}.{m.Name}";

    private static bool IsSample(Type t) => t.GetCustomAttributesData().Any(d => d.AttributeType.Name == SampleMarker);

    /// <summary>A test is a method with an attribute that derives from xUnit's FactAttribute (this covers Theory and FsCheck properties).</summary>
    private static bool IsTest(MethodInfo m) =>
        m.GetCustomAttributesData().Any(d =>
        {
            for (Type? t = d.AttributeType; t != null; t = t.BaseType)
                if (t.FullName == "Xunit.FactAttribute") return true;
            return d.AttributeType.GetInterfaces().Any(i => i.FullName == "Xunit.v3.IFactAttribute");
        });

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    /// <summary>
    /// The test assemblies of the solution: <paramref name="self"/> plus its siblings under tests/ built into the same
    /// configuration and framework folder. Siblings that are not built are returned in <c>missing</c>.
    /// </summary>
    public static (List<Assembly> Assemblies, List<string> Missing) FindSolutionTestAssemblies(Assembly self, string repoRoot)
    {
        var assemblies = new List<Assembly> { self };
        var missing = new List<string>();
        var selfName = self.GetName().Name!;
        var baseDir = AppContext.BaseDirectory;
        var selfProject = Path.Combine(repoRoot, "tests", selfName) + Path.DirectorySeparatorChar;
        var rel = baseDir.StartsWith(selfProject, StringComparison.OrdinalIgnoreCase) ? baseDir[selfProject.Length..] : null;
        foreach (var proj in new[] { "YSharp.Fuzz.Tests", "YSharp.Interop.Tests" })
        {
            if (proj == selfName) continue;
            var dll = rel == null ? null : Path.Combine(repoRoot, "tests", proj, rel, proj + ".dll");
            if (dll != null && File.Exists(dll)) assemblies.Add(Assembly.LoadFrom(dll));
            else missing.Add(proj);
        }
        return (assemblies, missing);
    }
}

/// <summary>Writes the traceability matrix (Markdown for people, JSON for tools) as a CI artifact.</summary>
public static class MatrixWriter
{
    public static string DefaultDir(string repoRoot) =>
        Environment.GetEnvironmentVariable("YSHARP_TRACEABILITY_DIR") is { Length: > 0 } d ? d : Path.Combine(repoRoot, "TestResults", "traceability");

    public static (string Markdown, string Json) Write(string dir, TraceabilityReport report, IEnumerable<string>? notes = null)
    {
        Directory.CreateDirectory(dir);
        var md = Path.Combine(dir, "traceability-matrix.md");
        var json = Path.Combine(dir, "traceability-matrix.json");
        File.WriteAllText(md, ToMarkdown(report, notes ?? Array.Empty<string>()), new UTF8Encoding(false));
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            pendingEntries = report.PendingEntries,
            pendingLedgerRows = report.PendingLedgerRows,
            violations = report.Violations,
            rows = report.Matrix,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), new UTF8Encoding(false));
        return (md, json);
    }

    public static string ToMarkdown(TraceabilityReport report, IEnumerable<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Traceability matrix").AppendLine();
        sb.AppendLine($"- Registry entries: {report.Matrix.Count} ({report.Matrix.Count(r => r.Status == "active")} active, {report.PendingEntries} pending, {report.Matrix.Count(r => r.Status == "excluded")} excluded, {report.Matrix.Count(r => r.Status == "retired")} retired)");
        sb.AppendLine($"- Pending ledger rows: {report.PendingLedgerRows}");
        sb.AppendLine($"- Violations: {report.Violations.Count}");
        foreach (var n in notes) sb.AppendLine($"- Note: {n}");
        sb.AppendLine().AppendLine("## By owner").AppendLine().AppendLine("| Spec | Entries | Active | Pending |").AppendLine("|---|---|---|---|");
        foreach (var g in report.Matrix.GroupBy(r => r.Owner).OrderBy(g => g.Key))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(r => r.Status == "active")} | {g.Count(r => r.Status == "pending")} |");
        if (report.Violations.Count > 0)
        {
            sb.AppendLine().AppendLine("## Violations").AppendLine();
            foreach (var v in report.Violations) sb.AppendLine($"- `{v.Code}` {v.Message}");
        }
        sb.AppendLine().AppendLine("## Entries").AppendLine().AppendLine("| ID | Status | Kind | Owner | Declared layers | Covered layers | Tests | Fixtures |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Matrix)
            sb.AppendLine($"| {r.Id} | {r.Status} | {r.Kind} | {r.Owner} | {string.Join(", ", r.Layers)} | {string.Join(", ", r.CoveredLayers)} | {r.Tests.Count} | {r.Fixtures.Count} |");
        return sb.ToString();
    }
}
