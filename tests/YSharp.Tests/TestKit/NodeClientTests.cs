using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Node;

namespace YSharp.Tests.TestKit;

[Trait("Needs", "Node")]
[Trait("Tier", "Smoke")]
public class NodeClientTests
{
    private static string ServerScript => Path.Combine(AppContext.BaseDirectory, "TestKit", "Node", "echo-server.mjs");

    private static NodeClient Create(List<string>? stderr = null, TimeSpan? timeout = null)
    {
        NodeAvailability.SkipUnlessAvailable(RepoRoot.Find(), matchNvmrc: false);
        return new NodeClient(new NodeClientOptions
        {
            ScriptPath = ServerScript,
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(10),
            OnStderr = line => stderr?.Add(line),
        });
    }

    [Fact]
    public async Task Request_Echo_RoundTripsPayloadAfterHandshake()
    {
        await using var client = Create();
        await client.StartAsync();
        using var response = await client.RequestAsync("echo", JsonDocument.Parse("{\"a\":[1,2,{\"$\":\"nan\"}]}").RootElement);
        Assert.Equal("{\"a\":[1,2,{\"$\":\"nan\"}]}", response.RootElement.GetRawText());
    }

    [Fact]
    public async Task Request_ConcurrentRequests_AreMatchedById()
    {
        await using var client = Create();
        await client.StartAsync();
        var tasks = new List<Task<int>>();
        for (var i = 0; i < 20; i++)
        {
            var n = i;
            tasks.Add(Task.Run(async () =>
            {
                using var r = await client.RequestAsync("echo", JsonDocument.Parse(n.ToString()).RootElement);
                return r.RootElement.GetInt32();
            }));
        }
        Assert.Equal(Enumerable_Range(20), await Task.WhenAll(tasks));
    }

    private static int[] Enumerable_Range(int n)
    {
        var a = new int[n];
        for (var i = 0; i < n; i++) a[i] = i;
        return a;
    }

    [Fact]
    public async Task Request_NodeReportsError_ThrowsWithMessage()
    {
        await using var client = Create();
        await client.StartAsync();
        var ex = await Assert.ThrowsAsync<NodeRequestException>(() => client.RequestAsync("fail", default));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Console_Output_GoesToStderrCallback_NotTheProtocol()
    {
        var lines = new List<string>();
        await using var client = Create(lines);
        await client.StartAsync();
        using var _ = await client.RequestAsync("log", default);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Contains("hello from node", lines);
    }

    [Fact]
    public async Task Request_Timeout_KillsAndRestartsTheChild()
    {
        await using var client = Create(timeout: TimeSpan.FromMilliseconds(500));
        await client.StartAsync();
        using var before = await client.RequestAsync("pid", default);
        await Assert.ThrowsAsync<NodeTimeoutException>(() => client.RequestAsync("hang", default));
        using var after = await client.RequestAsync("pid", default);
        Assert.NotEqual(before.RootElement.GetInt32(), after.RootElement.GetInt32());
        Assert.Equal(1, client.RestartCount);
    }

    [Fact]
    public void Availability_ReportsNodeMajorAgainstNvmrc()
    {
        var reason = NodeAvailability.Check(RepoRoot.Find());
        // Either available (null) or a visible reason; never an exception.
        Assert.True(reason is null || reason.Length > 0);
    }

    [Theory]
    [InlineData("22", "v22.11.0", true)]
    [InlineData("22", "v24.1.0", false)]
    [InlineData("v22.1.0", "v22.9.9", true)]
    [InlineData("lts/*", "v24.0.0", true)]
    public void NodeVersion_MatchesNvmrcMajor(string nvmrc, string running, bool expected)
    {
        Assert.Equal(expected, NodeAvailability.MajorMatches(nvmrc, running));
    }
}
