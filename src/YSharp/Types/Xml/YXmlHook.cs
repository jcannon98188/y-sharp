#nullable enable
using System;

namespace YSharp;

// Yjs YXmlHook: a YMap with a hook name, which is part of its wire encoding (X-H1, X-H3).
public sealed class YXmlHook : YMap
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlHook;

    public YXmlHook(string hookName)
    {
        ArgumentNullException.ThrowIfNull(hookName);
        Branch.TypeName = hookName;
    }

    public string HookName => Branch.TypeName ?? "";

    /// <summary>
    /// Yjs <c>clone</c>: a prelim hook with the same name and entries (X-H2). Nested types are cloned, as for YMap (JS sets the live instances,
    /// which cannot be integrated twice).
    /// </summary>
    public override YType Clone()
    {
        var copy = new YXmlHook(HookName);
        foreach (var (key, value) in this) copy.Set(key, value.Kind == YEntryKind.Type ? value.Type!.Clone() : value);
        return copy;
    }
}
