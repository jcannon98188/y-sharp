#nullable enable
using System;
using System.Buffers;
using YSharp.Lib0;

namespace YSharp;

/// <summary>y-protocols <c>auth.js</c> (AU-1..AU-7): one message type, <c>messagePermissionDenied = 0</c>, carrying a UTF-8 reason.</summary>
public static class YAuth
{
    private const ulong PermissionDenied = 0;

    /// <summary>Yjs <c>writePermissionDenied</c> (AU-2): <c>varUint(0) · varString(reason)</c>.</summary>
    public static void WritePermissionDenied(IBufferWriter<byte> writer, string reason)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reason);
        using var w = Lib0Writer.Rent(reason.Length * 3 + 8);
        w.WriteVarUint(PermissionDenied);
        w.WriteVarString(reason);
        w.CopyTo(writer);
    }

    /// <summary>
    /// Yjs <c>readAuthMessage</c> (AU-3..AU-6): for type 0 reads the reason and calls the handler with <paramref name="doc"/>, which is only passed
    /// through. An unknown type is ignored with only the type consumed (AU-4). A truncated or invalid reason is an error result (ADR 0005), and
    /// <paramref name="input"/> advances past what was read, as the JS decoder does.
    /// A null permissionDeniedHandler throws ArgumentNullException at this call, before any byte is read (AU-5; JS fails with a TypeError only when type 0 is read).
    /// </summary>
    public static YStatus TryReadMessage(ref ReadOnlySpan<byte> input, Doc doc, Action<Doc, string> permissionDeniedHandler)
    {
        ArgumentNullException.ThrowIfNull(permissionDeniedHandler);
        var reader = new Lib0Reader(input, doc?.Limits.MaxStringBytes ?? YLimits.Default.MaxStringBytes);
        string reason;
        try
        {
            if (!reader.TryReadVarUint(out var type)) return new YStatus(reader.Error);
            if (type != PermissionDenied) return default;
            if (!reader.TryReadVarString(out reason)) return new YStatus(reader.Error);
        }
        finally
        {
            input = input[reader.Position..];
        }

        permissionDeniedHandler(doc!, reason);
        return default;
    }
}
