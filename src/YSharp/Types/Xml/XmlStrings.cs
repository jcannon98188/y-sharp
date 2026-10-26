#nullable enable
using System.Collections.Generic;
using System.Text;
using YSharp.Store;

namespace YSharp;

// The JS string conversions the XML toString methods rely on: template literals and `+` call String(value), and typeListMap calls each child's
// toString. Types without a toString of their own (YArray, YMap, YXmlHook) print as Object.prototype.toString does.
internal static class XmlStrings
{
    /// <summary>JS <c>String(value)</c> of a list or map element.</summary>
    public static string Of(in YEntry entry) => entry.Kind == YEntryKind.Type ? Of(entry.Type!) : Of(entry.Value);

    /// <summary>A shared type's JS <c>toString()</c>.</summary>
    public static string Of(YType type) => type switch
    {
        YText or YXmlFragment => type.ToString()!,
        _ => "[object Object]",
    };

    /// <summary>JS <c>String(value)</c>: arrays and byte arrays join their elements with commas, null and undefined elements as empty.</summary>
    public static string Of(in YValue value)
    {
        var sb = new StringBuilder();
        Append(sb, value);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, in YValue value)
    {
        switch (value.Kind)
        {
            case YValueKind.Undefined: sb.Append("undefined"); break;
            case YValueKind.Null: sb.Append("null"); break;
            case YValueKind.Bool: sb.Append(value.AsBool() ? "true" : "false"); break;
            case YValueKind.Number: sb.Append(JsNumberFormatter.ToString(value.AsNumber())); break;
            case YValueKind.BigInt:
                value.TryGetBigInt(out var big);
                sb.Append(big.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case YValueKind.String: sb.Append(value.AsString()); break;
            case YValueKind.Bytes:
                value.TryGetBytes(out var bytes);
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(bytes[i]);
                }

                break;
            case YValueKind.Array:
                value.TryGetArray(out var items);
                for (var i = 0; i < items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (items[i].Kind is not (YValueKind.Undefined or YValueKind.Null)) Append(sb, items[i]);
                }

                break;
            default: sb.Append("[object Object]"); break;
        }
    }

    /// <summary>
    /// JS <c>for (const key in value)</c> with each <c>value[key]</c>: an object's members, a string's UTF-16 units and an array's or byte array's
    /// elements by index; other values have none.
    /// </summary>
    public static List<KeyValuePair<string, string>> ForIn(in YValue value)
    {
        var result = new List<KeyValuePair<string, string>>();
        switch (value.Kind)
        {
            case YValueKind.Object:
                value.TryGetObject(out var obj);
                for (var i = 0; i < obj.Count; i++) result.Add(new(obj[i].Key, Of(obj[i].Value)));
                break;
            case YValueKind.String:
                var s = value.AsString();
                for (var i = 0; i < s.Length; i++) result.Add(new(Index(i), s[i].ToString()));
                break;
            case YValueKind.Array:
                value.TryGetArray(out var items);
                for (var i = 0; i < items.Length; i++) result.Add(new(Index(i), Of(items[i])));
                break;
            case YValueKind.Bytes:
                value.TryGetBytes(out var bytes);
                for (var i = 0; i < bytes.Length; i++) result.Add(new(Index(i), Index(bytes[i])));
                break;
        }

        return result;
    }

    private static string Index(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Yjs <c>Item.next</c> / <c>Item.prev</c> of a node's item: the nearest undeleted neighbour's type, or null (X-E4).</summary>
    public static YType? Sibling(YType node, bool next)
    {
        if (node.AttachedDoc is not { } doc || node.Branch.Item is not { } item) return null;
        var h = doc.EnterRead();
        try
        {
            var n = new ItemRef(item);
            do n = next ? n.Right : n.Left;
            while (!n.IsNull && n.IsDeleted);
            return !n.IsNull && n.Kind == ContentKind.Type ? YType.Of((Branch)doc.Store.GetPayload(n), doc) : null;
        }
        finally
        {
            doc.Leave(h);
        }
    }
}
