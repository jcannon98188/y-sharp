using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace YSharp.TestKit.Traceability;

/// <summary>Reads docs/inventory/: the area YAMLs, the ledger, the frozen-ID lock and the pending high-water mark.</summary>
public static class InventoryLoader
{
    public const string LedgerFile = "ledger.yaml";
    public const string HighWaterFile = "pending-high-water.json";
    public const string IdLockFile = "id-lock.txt";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public static string InventoryDir(string repoRoot) => Path.Combine(repoRoot, "docs", "inventory");

    /// <summary>Every <c>*.yaml</c> in the inventory directory except the ledger, in file name order.</summary>
    public static List<RegistryEntry> LoadEntries(string repoRoot)
    {
        var all = new List<RegistryEntry>();
        foreach (var file in Directory.EnumerateFiles(InventoryDir(repoRoot), "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) == LedgerFile) continue;
            all.AddRange(Yaml.Deserialize<List<RegistryEntry>>(File.ReadAllText(file)) ?? new());
        }
        return all;
    }

    public static List<LedgerRow> LoadLedger(string repoRoot) =>
        Yaml.Deserialize<List<LedgerRow>>(File.ReadAllText(Path.Combine(InventoryDir(repoRoot), LedgerFile))) ?? new();

    public static PendingMark? LoadHighWater(string repoRoot)
    {
        var p = Path.Combine(InventoryDir(repoRoot), HighWaterFile);
        if (!File.Exists(p)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(p));
        return new PendingMark(doc.RootElement.GetProperty("registry").GetInt32(), doc.RootElement.GetProperty("ledger").GetInt32());
    }

    public static HashSet<string>? LoadIdLock(string repoRoot)
    {
        var p = Path.Combine(InventoryDir(repoRoot), IdLockFile);
        if (!File.Exists(p)) return null;
        return File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Header <c>covers</c> lists of every fixture under <c>fixtures/</c> (none yet before spec 4).</summary>
    public static List<FixtureTag> LoadFixtureTags(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "fixtures");
        var tags = new List<FixtureTag>();
        if (!Directory.Exists(dir)) return tags;
        foreach (var path in Fixtures.FixtureLoader.Discover(dir))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("header", out var h) || !h.TryGetProperty("covers", out var c)) continue;
            tags.Add(new FixtureTag(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), c.EnumerateArray().Select(x => x.GetString()!).ToArray()));
        }
        return tags;
    }

    /// <summary>Ledger keys (<c>file:testName</c>) of every <c>export const testX</c> in the pinned upstream suites. Null when the submodules are not checked out.</summary>
    public static HashSet<string>? ScanUpstreamKeys(string repoRoot)
    {
        var yjsTests = Path.Combine(repoRoot, "yjs", "tests");
        var awareness = Path.Combine(repoRoot, "y-protocols", "awareness.test.js");
        if (!Directory.Exists(yjsTests) || !File.Exists(awareness)) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(yjsTests, "*.tests.js").Append(awareness);
        foreach (var f in files)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"^export const (test\w+)\s*=", RegexOptions.Multiline))
                keys.Add(Path.GetFileName(f) + ":" + m.Groups[1].Value);
        return keys;
    }

    public static Func<string, int> LineCounter(string repoRoot) => rel =>
    {
        var p = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? File.ReadAllText(p).Split('\n').Length : -1;
    };
}
