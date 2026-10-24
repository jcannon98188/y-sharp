using System;
using System.IO;
using System.Linq;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

[LibraryIndependent]
public sealed class FixtureLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ysharp-fixtures-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Json(string yjs = "13.6.33", string lib0 = "0.2.99", string yp = "1.0.7", string extra = "") =>
        "{\"header\":{\"schema\":1,\"covers\":[\"TX-1\",\"ENC-02\"],\"versions\":{\"yjs\":\"" + yjs + "\",\"lib0\":\"" + lib0 +
        "\",\"y-protocols\":\"" + yp + "\"},\"node\":\"v22.0.0\",\"generator\":\"fixture-gen 0.0.0\",\"seed\":7" + extra + "}," +
        "\"script\":{\"schema\":1,\"steps\":[]},\"observations\":{\"sv\":{\"$\":\"bytes\",\"hex\":\"00\"}}}";

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void PinnedVersions_FromRepo_ReadSubmodulePackageJson()
    {
        var pinned = PinnedVersions.Read(RepoRoot.Find());
        Assert.Equal("13.6.33", pinned.Yjs);
        Assert.Equal("0.2.99", pinned.Lib0);
        Assert.Equal("1.0.7", pinned.YProtocols);
    }

    [Fact]
    public void Load_ValidFixture_ParsesHeaderScriptAndObservations()
    {
        var f = FixtureLoader.Load(Write("text/basic.json", Json()), PinnedVersions.Read(RepoRoot.Find()));
        Assert.Equal(1, f.Header.Schema);
        Assert.Equal(new[] { "TX-1", "ENC-02" }, f.Header.Covers);
        Assert.Equal(7L, f.Header.Seed);
        Assert.Equal("13.6.33", f.Header.Yjs);
        Assert.True(f.Observations.TryGetProperty("sv", out _));
        Assert.Equal(JsonKind.Object, JsonKindOf(f.Script));
    }

    private static JsonKind JsonKindOf(System.Text.Json.JsonElement e) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object ? JsonKind.Object : JsonKind.Other;

    private enum JsonKind { Object, Other }

    [Theory]
    [InlineData("13.6.32", "0.2.99", "1.0.7", "yjs")]
    [InlineData("13.6.33", "0.2.98", "1.0.7", "lib0")]
    [InlineData("13.6.33", "0.2.99", "1.0.6", "y-protocols")]
    public void Load_VersionMismatch_FailsNamingTheLibrary(string yjs, string lib0, string yp, string library)
    {
        var path = Write("a.json", Json(yjs, lib0, yp));
        var ex = Assert.Throws<FixtureVersionException>(() => FixtureLoader.Load(path, PinnedVersions.Read(RepoRoot.Find())));
        Assert.Contains(library, ex.Message);
        Assert.Contains("npm run gen", ex.Message);
    }

    [Fact]
    public void Load_UnsupportedSchema_Fails()
    {
        var path = Write("a.json", Json().Replace("\"schema\":1,\"covers\"", "\"schema\":2,\"covers\""));
        Assert.Throws<FixtureFormatException>(() => FixtureLoader.Load(path, PinnedVersions.Read(RepoRoot.Find())));
    }

    [Fact]
    public void Load_MissingHeaderField_Fails()
    {
        var path = Write("a.json", Json().Replace("\"seed\":7", "\"sed\":7"));
        Assert.Throws<FixtureFormatException>(() => FixtureLoader.Load(path, PinnedVersions.Read(RepoRoot.Find())));
    }

    [Fact]
    public void Load_UnknownTopLevelField_Fails()
    {
        var path = Write("a.json", Json().Replace("\"observations\"", "\"surprise\":1,\"observations\""));
        Assert.Throws<FixtureFormatException>(() => FixtureLoader.Load(path, PinnedVersions.Read(RepoRoot.Find())));
    }

    [Fact]
    public void Discover_FindsFixturesByGlob_SkippingTheManifest()
    {
        Write("text/a.json", Json());
        Write("map/b.json", Json());
        Write("index.json", "{}");
        var found = FixtureLoader.Discover(_dir).Select(p => Path.GetRelativePath(_dir, p).Replace('\\', '/')).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "map/b.json", "text/a.json" }, found);
    }
}
