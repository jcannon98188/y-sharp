using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace YSharp.TestKit.Fixtures;

public class FixtureFormatException : Exception
{
    public FixtureFormatException(string message) : base(message) { }
}

/// <summary>The fixture was generated against different oracle versions than the pinned submodules (ADR 0011 drift check).</summary>
public sealed class FixtureVersionException : FixtureFormatException
{
    public FixtureVersionException(string message) : base(message) { }
}

/// <summary>Versions of the pinned oracle submodules, read from their <c>package.json</c>.</summary>
public sealed record PinnedVersions(string Yjs, string Lib0, string YProtocols)
{
    public static PinnedVersions Read(string repoRoot) => new(
        ReadVersion(Path.Combine(repoRoot, "yjs", "package.json")),
        ReadVersion(Path.Combine(repoRoot, "lib0", "package.json")),
        ReadVersion(Path.Combine(repoRoot, "y-protocols", "package.json")));

    private static string ReadVersion(string packageJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
        return doc.RootElement.GetProperty("version").GetString()!;
    }
}

/// <summary>Finds the repository root (the directory holding <c>global.json</c>).</summary>
public static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("global.json not found above " + AppContext.BaseDirectory);
    }
}

public sealed record FixtureHeader(int Schema, IReadOnlyList<string> Covers, string Yjs, string Lib0, string YProtocols, string Node, string Generator, long Seed);

/// <summary>A loaded fixture: header, the ops script it replays, and the oracle's named observations.</summary>
public sealed class Fixture
{
    public Fixture(string path, FixtureHeader header, JsonElement script, JsonElement observations)
    {
        Path = path;
        Header = header;
        Script = script;
        Observations = observations;
    }

    public string Path { get; }
    public FixtureHeader Header { get; }
    public JsonElement Script { get; }
    public JsonElement Observations { get; }
}

public static class FixtureLoader
{
    public const int SchemaVersion = 1;

    /// <summary>Every fixture file under <paramref name="fixturesRoot"/> (glob <c>**/*.json</c>), excluding the <c>index.json</c> manifest.</summary>
    public static IEnumerable<string> Discover(string fixturesRoot) =>
        Directory.EnumerateFiles(fixturesRoot, "*.json", SearchOption.AllDirectories)
            .Where(p => !string.Equals(System.IO.Path.GetFileName(p), "index.json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);

    /// <exception cref="FixtureVersionException">yjs, lib0 or y-protocols differ from the pinned submodules.</exception>
    /// <exception cref="FixtureFormatException">The file is not a schema 1 fixture.</exception>
    public static Fixture Load(string path, PinnedVersions pinned)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FixtureFormatException($"{path}: fixture must be a JSON object.");

        foreach (var p in root.EnumerateObject())
            if (p.Name is not ("header" or "script" or "observations"))
                throw new FixtureFormatException($"{path}: unknown top-level field '{p.Name}'.");
        var header = ReadHeader(path, Required(path, root, "header"));
        if (header.Schema != SchemaVersion) throw new FixtureFormatException($"{path}: unsupported schema {header.Schema} (expected {SchemaVersion}).");

        var mismatches = new List<string>();
        if (header.Yjs != pinned.Yjs) mismatches.Add($"yjs {header.Yjs} != pinned {pinned.Yjs}");
        if (header.Lib0 != pinned.Lib0) mismatches.Add($"lib0 {header.Lib0} != pinned {pinned.Lib0}");
        if (header.YProtocols != pinned.YProtocols) mismatches.Add($"y-protocols {header.YProtocols} != pinned {pinned.YProtocols}");
        if (mismatches.Count > 0)
            throw new FixtureVersionException($"{path}: fixture was generated against other oracle versions ({string.Join("; ", mismatches)}). Regenerate with `npm run gen`.");

        return new Fixture(path, header, Required(path, root, "script").Clone(), Required(path, root, "observations").Clone());
    }

    private static JsonElement Required(string path, JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v : throw new FixtureFormatException($"{path}: missing '{name}'.");

    private static FixtureHeader ReadHeader(string path, JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object) throw new FixtureFormatException($"{path}: header must be an object.");
        var allowed = new HashSet<string> { "schema", "covers", "versions", "node", "generator", "seed", "repro" };
        foreach (var p in h.EnumerateObject())
            if (!allowed.Contains(p.Name)) throw new FixtureFormatException($"{path}: unknown header field '{p.Name}'.");
        var versions = Required(path, h, "versions");
        return new FixtureHeader(
            Required(path, h, "schema").GetInt32(),
            Required(path, h, "covers").EnumerateArray().Select(c => c.GetString()!).ToArray(),
            Required(path, versions, "yjs").GetString()!,
            Required(path, versions, "lib0").GetString()!,
            Required(path, versions, "y-protocols").GetString()!,
            Required(path, h, "node").GetString()!,
            Required(path, h, "generator").GetString()!,
            Required(path, h, "seed").GetInt64());
    }
}
