using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace YSharp.TestKit.Node;

public class NodeRequestException : Exception
{
    public NodeRequestException(string message) : base(message) { }
}

/// <summary>The per-request timeout elapsed; the child was killed and restarted (ADR 0012 "Hangs").</summary>
public sealed class NodeTimeoutException : NodeRequestException
{
    public NodeTimeoutException(string message) : base(message) { }
}

public sealed class NodeClientOptions
{
    /// <summary>The Node server script to run (it must speak the ADR 0012 protocol).</summary>
    public string ScriptPath { get; init; } = "";

    public string NodeExecutable { get; init; } = "node";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Receives each line the child writes to stderr (Node's <c>console</c> is redirected there); wire to the test log.</summary>
    public Action<string>? OnStderr { get; init; }

    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// Client for one long-lived Node child (ADR 0012): newline-delimited JSON on stdin/stdout, requests carry ids so a pool
/// can be added later, a <c>{"hello":true,"schema":1,...}</c> handshake line comes first, stderr goes to the test log.
/// Request: <c>{"id":n,"op":"...","payload":...}</c>. Response: <c>{"id":n,"ok":true,"result":...}</c> or
/// <c>{"id":n,"ok":false,"error":"..."}</c>. A request timeout kills and restarts the child.
/// </summary>
public sealed class NodeClient : IAsyncDisposable
{
    public const int ProtocolSchema = 1;

    private readonly NodeClientOptions _options;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private Process? _process;
    private long _nextId;
    private int _generation;

    public NodeClient(NodeClientOptions options) => _options = options;

    public string? NodeVersion { get; private set; }

    /// <summary>The raw handshake line of the current child (carries the oracle versions and op list in the differential harness).</summary>
    public string? HelloLine { get; private set; }
    public int RestartCount { get; private set; }

    public async Task StartAsync()
    {
        var psi = new ProcessStartInfo(_options.NodeExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (_options.WorkingDirectory != null) psi.WorkingDirectory = _options.WorkingDirectory;
        psi.ArgumentList.Add(_options.ScriptPath);

        var process = Process.Start(psi) ?? throw new NodeRequestException("Could not start node.");
        _process = process;
        var generation = Interlocked.Increment(ref _generation);

        // The pipes are read on dedicated threads with blocking reads. On Windows a child's stdio pipes are synchronous handles, so
        // ReadLineAsync blocks a pool thread; when the test run starves the pool, responses then wait behind other work until the
        // request timeout fires (the Windows gating flakes of TASK-4.44). A dedicated thread completes the request as soon as Node answers.
        var stderrLines = new System.Collections.Generic.List<string>();
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartReader("node-stderr", () =>
        {
            try
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    lock (stderrLines) stderrLines.Add(line);
                    _options.OnStderr?.Invoke(line);
                }
            }
            catch (Exception) { /* the child was killed or disposed */ }
            stderrDone.TrySetResult();
        });

        var helloRead = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartReader("node-stdout", () => ReadLoop(process, generation, helloRead));
        if (await Task.WhenAny(helloRead.Task, Task.Delay(_options.HandshakeTimeout)) != helloRead.Task)
        {
            Kill(process);
            throw new NodeRequestException("Timed out waiting for the node handshake.");
        }
        var hello = await helloRead.Task;
        if (hello == null)
        {
            await Task.WhenAny(stderrDone.Task, Task.Delay(2000));
            string tail;
            lock (stderrLines) tail = string.Join(" | ", stderrLines);
            throw new NodeRequestException("Node exited before the handshake." + (tail.Length == 0 ? "" : " stderr: " + tail));
        }
        HelloLine = hello;
        using (var h = JsonDocument.Parse(hello))
        {
            var root = h.RootElement;
            if (!root.TryGetProperty("hello", out _) || !root.TryGetProperty("schema", out var schema) || schema.GetInt32() != ProtocolSchema)
                throw new NodeRequestException($"Bad node handshake (expected schema {ProtocolSchema}): {hello}");
            NodeVersion = root.TryGetProperty("node", out var n) ? n.GetString() : null;
        }
    }

    private static void StartReader(string name, Action body) => new Thread(() => body()) { IsBackground = true, Name = name }.Start();

    /// <summary>The first line is the handshake (handed to <paramref name="hello"/>); every later line is a response.</summary>
    private void ReadLoop(Process process, int generation, TaskCompletionSource<string?> hello)
    {
        Exception? failure = null;
        try
        {
            var first = process.StandardOutput.ReadLine();
            hello.TrySetResult(first);
            string? line = first;
            while (line != null && (line = process.StandardOutput.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || !_pending.TryRemove(idEl.GetInt64(), out var tcs))
                {
                    doc.Dispose();
                    continue;
                }
                if (root.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                {
                    var msg = root.TryGetProperty("error", out var e) ? e.ToString() : "node request failed";
                    doc.Dispose();
                    tcs.TrySetException(new NodeRequestException(msg));
                }
                else
                {
                    using var _ = doc;
                    tcs.TrySetResult(root.TryGetProperty("result", out var r) ? JsonDocument.Parse(r.GetRawText()) : JsonDocument.Parse("null"));
                }
            }
        }
        catch (Exception e)
        {
            hello.TrySetException(e);
            failure = e;
        }

        // a killed or disposed child belongs to an older generation; a failure in the current one fails every pending request
        if (generation != Volatile.Read(ref _generation)) return;
        FailAll(failure is null || HasExited(process)
            ? new NodeRequestException("Node exited unexpectedly.")
            : new NodeRequestException("Could not read the node response: " + failure.Message));
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private void FailAll(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
    }

    /// <summary>Sends one request; the returned document is the response <c>result</c> and must be disposed.</summary>
    public async Task<JsonDocument> RequestAsync(string op, JsonElement payload)
    {
        var process = _process ?? throw new InvalidOperationException("Call StartAsync first.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var line = payload.ValueKind == JsonValueKind.Undefined
            ? $"{{\"id\":{id},\"op\":{JsonSerializer.Serialize(op)}}}"
            : $"{{\"id\":{id},\"op\":{JsonSerializer.Serialize(op)},\"payload\":{payload.GetRawText()}}}";
        await _writeLock.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
        }
        finally { _writeLock.Release(); }

        // ask whether the answer came, not which task WhenAny saw first: under a starved pool the timer's callback can run before
        // the continuation of an answer the reader thread already delivered
        await Task.WhenAny(tcs.Task, Task.Delay(_options.RequestTimeout));
        if (tcs.Task.IsCompleted) return await tcs.Task;

        _pending.TryRemove(id, out _);
        await RestartAsync(process);
        throw new NodeTimeoutException($"Node did not answer '{op}' within {_options.RequestTimeout.TotalMilliseconds:0} ms; the child was restarted.");
    }

    private async Task RestartAsync(Process stale)
    {
        await _restartLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(_process, stale)) return; // another timeout already restarted it
            Kill(stale);
            FailAll(new NodeRequestException("Node was restarted after a timeout."));
            RestartCount++;
            await StartAsync();
        }
        finally { _restartLock.Release(); }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var p = _process;
        if (p == null) return;
        try { p.StandardInput.Close(); } catch (Exception) { }
        if (!p.WaitForExit(500)) Kill(p);
        p.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>Is a usable Node present? Local runs skip with a visible reason; CI differential jobs set <c>YSHARP_REQUIRE_NODE=1</c> to fail instead.</summary>
public static class NodeAvailability
{
    /// <summary>Null when Node runs and matches <c>.nvmrc</c>'s major version; otherwise the reason.</summary>
    public static string? Check(string repoRoot, string nodeExecutable = "node", bool matchNvmrc = true)
    {
        string running;
        try
        {
            var psi = new ProcessStartInfo(nodeExecutable, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            running = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
        }
        catch (Exception ex)
        {
            return $"node is not available ({ex.Message})";
        }

        var nvmrc = Path.Combine(repoRoot, ".nvmrc");
        if (matchNvmrc && File.Exists(nvmrc) && !MajorMatches(File.ReadAllText(nvmrc).Trim(), running))
            return $"node {running} does not match .nvmrc ({File.ReadAllText(nvmrc).Trim()})";
        return null;
    }

    /// <summary>Compares the <c>.nvmrc</c> major version with the running <c>vX.Y.Z</c>; aliases such as <c>lts/*</c> match anything.</summary>
    public static bool MajorMatches(string nvmrc, string running)
    {
        var want = LeadingMajor(nvmrc);
        return want == null || want == LeadingMajor(running);
    }

    private static string? LeadingMajor(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < v.Length && char.IsDigit(v[end])) end++;
        return end == 0 ? null : v[..end];
    }

    /// <summary>Skips the calling test visibly when Node is missing or wrong, unless <c>YSHARP_REQUIRE_NODE=1</c>, which fails it.</summary>
    public static void SkipUnlessAvailable(string repoRoot, bool matchNvmrc = true)
    {
        var reason = Check(repoRoot, matchNvmrc: matchNvmrc);
        if (reason == null) return;
        if (Environment.GetEnvironmentVariable("YSHARP_REQUIRE_NODE") == "1") Assert.Fail("Node is required: " + reason);
        Assert.Skip(reason);
    }
}
