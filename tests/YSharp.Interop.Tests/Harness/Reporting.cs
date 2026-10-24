using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Tiers;

namespace YSharp.Interop;

public enum FailureReason
{
    /// <summary>Both ran and the observations differ.</summary>
    Mismatch,

    /// <summary>y-sharp threw, hung or disagreed about the script's validity.</summary>
    SharpFault,

    /// <summary>The oracle threw or hung on a valid generated script (counted and saved, not a failure unless y-sharp also fails).</summary>
    OracleMisbehaved,

    /// <summary>A mutated update fell into a class that no recorded divergence covers.</summary>
    OutcomeClass,
}

/// <summary>Everything a report needs about one failing case.</summary>
public sealed record FailureData(FailureReason Reason, string Property, string? ReplaySeed, long CaseSeed, string Label, string ScriptJson, JsonObject? Oracle, JsonObject? Sharp, Difference? First, string? Message);

/// <summary>What the harness saved for a failing or oracle-misbehaved script (ADR 0012 "Reporting").</summary>
public sealed record Saved(string Path, string Replay);

/// <summary>
/// Writes fixture candidates and renders failure reports. A candidate is a fixture-shaped file (<c>header</c> with a <c>repro</c> member,
/// <c>script</c>, the oracle's <c>observations</c>) in the CI artifacts directory, so a failing script can be reviewed and promoted to a
/// golden fixture; <c>YSHARP_REPLAY=&lt;path&gt;</c> re-runs it with no FsCheck and no regeneration.
/// </summary>
public static class Reporting
{
    /// <summary><c>YSHARP_ARTIFACTS_DIR</c>, or a <c>failing-seeds</c> folder beside the test binaries (the nightly job uploads <c>**/failing-seeds/**</c>).</summary>
    public static string ArtifactsDirectory =>
        Environment.GetEnvironmentVariable("YSHARP_ARTIFACTS_DIR") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "failing-seeds");

    public static Saved Save(string directory, string scriptJson, JsonObject? oracleObservations, FailureReason reason, string property, string? replaySeed, long caseSeed, string message)
    {
        var script = JsonNode.Parse(scriptJson)!;
        var root = YSharp.TestKit.Fixtures.RepoRoot.Find();
        var pinned = PinnedVersions.Read(root);
        var node = File.Exists(Path.Combine(root, ".nvmrc")) ? File.ReadAllText(Path.Combine(root, ".nvmrc")).Trim().TrimStart('v').Split('.')[0] : "0";

        var repro = new JsonObject
        {
            ["reason"] = reason switch
            {
                FailureReason.Mismatch => "mismatch",
                FailureReason.SharpFault => "ysharp-fault",
                FailureReason.OracleMisbehaved => "oracle-misbehaved",
                _ => "outcome-class",
            },
            ["tier"] = YSharpEnv.CurrentTier.ToString(),
            ["property"] = property,
            ["message"] = message.Length > 2000 ? message[..2000] : message,
        };
        if (replaySeed != null) repro["seed"] = replaySeed;

        var candidate = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["schema"] = 1,
                ["covers"] = new JsonArray("DIFF-01"),
                ["versions"] = new JsonObject { ["yjs"] = pinned.Yjs, ["lib0"] = pinned.Lib0, ["y-protocols"] = pinned.YProtocols },
                ["node"] = node,
                ["generator"] = "ysharp-interop-tests (live differential)",
                ["seed"] = Math.Max(0, caseSeed),
                ["repro"] = repro,
            },
            ["script"] = script,
            ["observations"] = oracleObservations?.DeepClone() ?? new JsonObject(),
        };

        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptJson)))[..10].ToLowerInvariant();
        var file = Path.Combine(directory, $"{repro["reason"]!.GetValue<string>()}-{Sanitize(property)}-{hash}.json");
        File.WriteAllText(file, candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new Saved(file, $"YSHARP_REPLAY={file}");
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        return sb.ToString();
    }

    /// <summary>Loads a saved candidate or a bare ops script from <paramref name="path"/> (the <c>YSHARP_REPLAY</c> input).</summary>
    public static string LoadScript(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty.");
        var script = node["script"] ?? node;
        return script.ToJsonString();
    }

    public static string Render(FailureData d, Saved? saved)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Differential failure ({d.Reason}) in {d.Property}.");
        sb.AppendLine($"  case: {d.Label}");
        sb.AppendLine($"  case seed: {d.CaseSeed}{(d.ReplaySeed != null ? $", FsCheck replay: {d.ReplaySeed}" : "")}");
        if (d.Message != null) sb.AppendLine($"  {d.Message}");
        if (d.First != null) sb.AppendLine($"  first difference: {d.First}");
        sb.AppendLine("  shrunk script:");
        sb.AppendLine("    " + d.ScriptJson);
        if (d.Oracle != null) sb.AppendLine("  oracle observations:" + Environment.NewLine + "    " + Truncate(d.Oracle.ToJsonString()));
        if (d.Sharp != null) sb.AppendLine("  y-sharp observations:" + Environment.NewLine + "    " + Truncate(d.Sharp.ToJsonString()));
        if (saved != null)
        {
            sb.AppendLine($"  fixture candidate: {saved.Path}");
            sb.AppendLine($"  re-run it: {saved.Replay} (the case is a real bug until proven otherwise: no retry, no quarantine; ADR 0006)");
        }
        return sb.ToString();
    }

    private static string Truncate(string s) => s.Length <= 6000 ? s : s[..6000] + $"... ({s.Length - 6000} more characters)";
}
