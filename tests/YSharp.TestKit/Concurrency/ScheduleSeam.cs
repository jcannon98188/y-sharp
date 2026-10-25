using System;
using System.Reflection;

namespace YSharp.TestKit.Concurrency;

/// <summary>
/// The reflection adapter for the one internal seam Spec 12 needs. The TestKit cannot see the library's internals (ADR 0015: InternalsVisibleTo goes to
/// YSharp.Tests, YSharp.Fuzz.Tests and YSharp.Benchmarks only), so it sets the delegates by reflection; tests in YSharp.Tests assign
/// <c>YSharp.Transactions.ScheduleSeam</c> directly (<c>SeamInstall</c>). The seam itself: from the implementation (ADR 0006 "Seams", docs/research/schedule-exploration.md section 2).
/// <para>
/// Contract: an <c>internal static class YSharp.Transactions.ScheduleSeam</c> in the <c>YSharp</c> assembly (visible to the test assemblies) with two
/// settable static members, each a property or field:
/// </para>
/// <list type="bullet">
/// <item><c>Action&lt;string&gt;? Point</c>: called by the lock, the observer list and the lock-order checker at every state transition
/// (read acquire, write acquire, owner check, queued-writer check, release, upgrade rejection, handler-list snapshot, handler add and
/// remove). The string is a stable id such as <c>"lock.write.acquire"</c>. Null in production.</item>
/// <item><c>Action&lt;string, Func&lt;bool&gt;&gt;? Block</c>: called instead of a raw <c>Monitor.Wait</c> or spin when the caller cannot
/// proceed; the predicate becomes true when it can (it reads lock state and must not itself call a seam member). Null in production.</item>
/// </list>
/// Both are invoked with no internal <c>Monitor</c> held, so a parked thread never makes another thread block for real. Both must compile
/// to nothing, or to a null check of a static, on the hot paths (CC-7). The scheduler ignores calls from threads it does not control.
/// </summary>
public static class ScheduleSeam
{
    public const string TypeName = "YSharp.Transactions.ScheduleSeam";

    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type? SeamType = typeof(Doc).Assembly.GetType(TypeName);

    /// <summary>True when the implementation exposes the seam.</summary>
    public static bool IsPresent => SeamType != null;

    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>Routes the library's yield and block points to <paramref name="scheduler"/> until the result is disposed. Only one at a time (the hook is static), so callers run in the serial collection.</summary>
    public static IDisposable Install(CooperativeScheduler scheduler)
    {
        if (SeamType == null)
            throw new InvalidOperationException(
                $"{TypeName} is missing. Spec 12 needs `internal static class ScheduleSeam {{ internal static Action<string>? Point; internal static Action<string, Func<bool>>? Block; }}` " +
                "in the YSharp assembly, called by the lock and the observer list (see ScheduleSeam in tests/YSharp.TestKit/Concurrency).");
        lock (Gate)
        {
            if (_installed) throw new InvalidOperationException("A schedule is already installed; concurrency tests must run in the serial collection.");
            _installed = true;
        }
        Set("Point", (Action<string>)scheduler.Point);
        Set("Block", (Action<string, Func<bool>>)scheduler.Block);
        return new Uninstall();
    }

    private static void Set(string member, object? value)
    {
        var prop = SeamType!.GetProperty(member, Flags);
        if (prop != null) { prop.SetValue(null, value); return; }
        var field = SeamType.GetField(member, Flags)
            ?? throw new InvalidOperationException($"{TypeName}.{member} is missing (see ScheduleSeam in the TestKit).");
        field.SetValue(null, value);
    }

    private sealed class Uninstall : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Set("Point", null);
            Set("Block", null);
            lock (Gate) _installed = false;
        }
    }
}
