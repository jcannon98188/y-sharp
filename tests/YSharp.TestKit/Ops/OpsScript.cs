using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using YSharp.TestKit.Values;

namespace YSharp.TestKit.Ops;

/// <summary>A problem in the script itself (bad vocabulary, unresolved path), never a y-sharp failure (ADR 0016).</summary>
public sealed class OpsScriptException : Exception
{
    public OpsScriptException(string message) : base(message) { }
}

/// <summary>One step kind: its allowed fields and what it does. Areas register their own kinds without touching the core.</summary>
public sealed class StepDefinition
{
    public StepDefinition(string kind, string[] required, string[] optional, Action<OpsContext, OpsStep> run, bool hasChildren = false)
    {
        Kind = kind;
        Required = required;
        Optional = optional;
        Run = run;
        HasChildren = hasChildren;
    }

    public string Kind { get; }
    public string[] Required { get; }
    public string[] Optional { get; }
    public Action<OpsContext, OpsStep> Run { get; }

    /// <summary>The step carries a nested <c>steps</c> list (<c>transact</c>).</summary>
    public bool HasChildren { get; }
}

public sealed class OpsRegistry
{
    private readonly Dictionary<string, StepDefinition> _kinds = new(StringComparer.Ordinal);

    public void Register(StepDefinition definition)
    {
        if (!_kinds.TryAdd(definition.Kind, definition))
            throw new InvalidOperationException($"Step kind '{definition.Kind}' is already registered.");
    }

    public bool TryGet(string kind, out StepDefinition definition) => _kinds.TryGetValue(kind, out definition!);

    public IEnumerable<string> Kinds => _kinds.Keys;
}

/// <summary>A validated step: its kind, raw fields and (for <c>transact</c>) child steps.</summary>
public sealed class OpsStep
{
    public OpsStep(StepDefinition definition, JsonElement json, IReadOnlyList<OpsStep>? children)
    {
        Definition = definition;
        Json = json;
        Children = children;
    }

    public StepDefinition Definition { get; }
    public string Kind => Definition.Kind;
    public JsonElement Json { get; }
    public IReadOnlyList<OpsStep>? Children { get; }

    public bool Has(string field) => Json.TryGetProperty(field, out _);

    public JsonElement Element(string field) =>
        Json.TryGetProperty(field, out var e) ? e : throw new OpsScriptException($"{Kind}: missing field '{field}'.");

    public string String(string field)
    {
        var e = Element(field);
        return e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new OpsScriptException($"{Kind}: '{field}' must be a string.");
    }

    public string? OptString(string field) => Has(field) ? String(field) : null;

    public int Int(string field)
    {
        var e = Element(field);
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : throw new OpsScriptException($"{Kind}: '{field}' must be an integer.");
    }

    public long Long(string field)
    {
        var e = Element(field);
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : throw new OpsScriptException($"{Kind}: '{field}' must be an integer.");
    }

    public bool Bool(string field, bool defaultValue)
    {
        if (!Has(field)) return defaultValue;
        var e = Element(field);
        return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new OpsScriptException($"{Kind}: '{field}' must be a boolean.");
    }

    public TaggedValue Tagged(string field)
    {
        try { return TaggedValue.Read(Element(field)); }
        catch (FormatException ex) { throw new OpsScriptException($"{Kind}: '{field}': {ex.Message}"); }
    }
}

/// <summary>
/// An ops script (ADR 0011/0016, <c>schema: 1</c>): declared replicas then steps. <c>replicas</c> entries become leading
/// <c>replica</c> steps. Parsing rejects unknown step kinds and unknown or missing fields.
/// </summary>
public sealed class OpsScript
{
    public const int SchemaVersion = 1;

    private OpsScript(IReadOnlyList<OpsStep> steps) => Steps = steps;

    public IReadOnlyList<OpsStep> Steps { get; }

    public static OpsScript Parse(string json, OpsRegistry registry)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement.Clone(), registry);
    }

    public static OpsScript Parse(JsonElement root, OpsRegistry registry)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new OpsScriptException("A script must be a JSON object.");
        foreach (var p in root.EnumerateObject())
            if (p.Name is not ("schema" or "replicas" or "steps"))
                throw new OpsScriptException($"Unknown script field '{p.Name}'.");
        if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != SchemaVersion)
            throw new OpsScriptException($"Unsupported or missing script schema (expected {SchemaVersion}).");

        var steps = new List<OpsStep>();
        if (root.TryGetProperty("replicas", out var replicas))
        {
            foreach (var r in replicas.EnumerateArray())
                steps.Add(ParseStep(ReplicaAsStep(r), registry));
        }
        if (root.TryGetProperty("steps", out var list))
            foreach (var s in list.EnumerateArray()) steps.Add(ParseStep(s, registry));
        return new OpsScript(steps);
    }

    private static JsonElement ReplicaAsStep(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new OpsScriptException("A replica must be an object.");
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("step", "replica");
            foreach (var p in r.EnumerateObject())
            {
                w.WritePropertyName(p.Name);
                p.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private static OpsStep ParseStep(JsonElement s, OpsRegistry registry)
    {
        if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("step", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            throw new OpsScriptException("Every step needs a string 'step' field.");
        var kind = kindEl.GetString()!;
        if (!registry.TryGet(kind, out var def)) throw new OpsScriptException($"Unknown step kind '{kind}'.");

        var present = s.EnumerateObject().Select(p => p.Name).Where(n => n != "step").ToList();
        var allowed = def.Required.Concat(def.Optional).ToHashSet(StringComparer.Ordinal);
        if (def.HasChildren) allowed.Add("steps");
        foreach (var name in present)
            if (!allowed.Contains(name)) throw new OpsScriptException($"{kind}: unknown field '{name}'.");
        foreach (var name in def.Required)
            if (!present.Contains(name)) throw new OpsScriptException($"{kind}: missing required field '{name}'.");

        List<OpsStep>? children = null;
        if (def.HasChildren)
        {
            if (!s.TryGetProperty("steps", out var kids) || kids.ValueKind != JsonValueKind.Array)
                throw new OpsScriptException($"{kind}: needs a 'steps' array.");
            children = kids.EnumerateArray().Select(k => ParseStep(k, registry)).ToList();
        }
        return new OpsStep(def, s, children);
    }
}
