using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace YSharp.Fuzz;

public enum CanaryVerdict { Passed, Failed, StackOverflow, Crashed, TimedOut, UnknownCase }

/// <summary>What a canary run did, classified from its exit code and output.</summary>
internal sealed record CanaryRun(CanaryVerdict Verdict, int ExitCode, string Stdout, string Stderr)
{
    public override string ToString() => $"{Verdict} (exit {ExitCode}) stdout: {Stdout.Trim()} stderr: {Short(Stderr)}";

    private static string Short(string s) => s.Length <= 600 ? s.Trim() : s[..600].Trim() + "...";
}

/// <summary>
/// Starts the out-of-process canary (ADR 0013) so a stack overflow in a HOS-DEEP document-depth case is a named test failure and not a
/// dead test host. The canary is a sibling project built into the same configuration and framework folder.
/// </summary>
internal static class CanaryHost
{
    public static string DllPath()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parts = baseDir.Split(Path.DirectorySeparatorChar).ToList();
        var i = parts.LastIndexOf("YSharp.Fuzz.Tests");
        Assert.True(i >= 0, $"cannot locate the canary: {baseDir} is not under tests/YSharp.Fuzz.Tests");
        parts[i] = "YSharp.Fuzz.Canary";
        var dll = Path.Combine(string.Join(Path.DirectorySeparatorChar, parts), "YSharp.Fuzz.Canary.dll");
        Assert.True(File.Exists(dll), $"the canary is not built: {dll} (build YSharp.slnx)");
        return dll;
    }

    public static CanaryRun Run(string caseName, int? depth = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(DllPath());
        psi.ArgumentList.Add(caseName);
        if (depth is { } d) psi.ArgumentList.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.Environment["DOTNET_gcServer"] = "0";
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";      // a stack overflow must not write a dump into the test results
        psi.Environment["DOTNET_EnableCrashReport"] = "0";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? TimeSpan.FromSeconds(120);
        if (!process.WaitForExit(limit))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            return new CanaryRun(CanaryVerdict.TimedOut, -1, stdout.ToString(), stderr.ToString());
        }
        process.WaitForExit();   // flush the async readers
        return Classify(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static CanaryRun Classify(int exitCode, string stdout, string stderr)
    {
        // .NET prints "Stack overflow." to stderr and aborts: STATUS_STACK_OVERFLOW on Windows, SIGABRT (134) or SIGSEGV (139) elsewhere
        // On Windows an overflow that hits the guard page while the runtime cannot handle it dies silently with STATUS_ACCESS_VIOLATION;
        // managed code reports any other access violation as an exception on stderr, so a silent one is the overflow (seen on CI)
        var overflow = stderr.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase) || exitCode == unchecked((int)0xC00000FD) ||
                       (exitCode == unchecked((int)0xC0000005) && stderr.Length == 0);
        if (overflow) return new CanaryRun(CanaryVerdict.StackOverflow, exitCode, stdout, stderr);
        return exitCode switch
        {
            0 when stdout.Contains("OK", StringComparison.Ordinal) => new CanaryRun(CanaryVerdict.Passed, exitCode, stdout, stderr),
            1 => new CanaryRun(CanaryVerdict.Failed, exitCode, stdout, stderr),
            2 => new CanaryRun(CanaryVerdict.UnknownCase, exitCode, stdout, stderr),
            _ => new CanaryRun(CanaryVerdict.Crashed, exitCode, stdout, stderr),
        };
    }

    /// <summary>Asserts the canary case passed, naming a stack overflow or crash as such.</summary>
    public static void AssertPasses(string caseName, int? depth = null, TimeSpan? timeout = null)
    {
        var run = Run(caseName, depth, timeout);
        Assert.True(run.Verdict == CanaryVerdict.Passed, $"HOS-DEEP canary '{caseName}' (depth {depth?.ToString() ?? "default"}): {run}");
    }
}
