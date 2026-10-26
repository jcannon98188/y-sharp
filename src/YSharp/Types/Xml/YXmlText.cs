#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace YSharp;

// Yjs YXmlText: a YText that is an XML node, with siblings, and whose toString renders the formatting as nested tags (X-T1, X-T2).
public sealed class YXmlText : YText
{
    internal override byte WireTypeRef => Store.TypeRefs.XmlText;

    public YXmlText(string? initial = null) : base(initial) { }
    private protected override YText CreateEmpty() => new YXmlText();

    /// <summary>Yjs <c>nextSibling</c>: the next undeleted node in the parent, or null (X-E4).</summary>
    public YType? NextSibling => XmlStrings.Sibling(this, next: true);

    /// <summary>Yjs <c>prevSibling</c>: the previous undeleted node in the parent, or null (X-E4).</summary>
    public YType? PrevSibling => XmlStrings.Sibling(this, next: false);

    /// <summary>
    /// Yjs <c>YXmlText.toString</c> (X-T2): each delta insert wrapped in one tag per format, the tags sorted by format name, outermost first. A
    /// tag's attributes are what JS <c>for...in</c> finds in the format's value (an object's members; nothing for <c>true</c>), sorted by key.
    /// Values and embeds are converted as JS <c>String()</c> does, and nothing is escaped.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var op in ToDelta())
        {
            var tags = new List<(string Name, List<KeyValuePair<string, string>> Attrs)>();
            if (op.Attributes.TryGetObject(out var formats))
            {
                for (var i = 0; i < formats.Count; i++)
                {
                    var attrs = XmlStrings.ForIn(formats[i].Value);
                    attrs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    tags.Add((formats[i].Key, attrs));
                }
            }

            tags.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var (name, attrs) in tags)
            {
                sb.Append('<').Append(name);
                foreach (var (key, value) in attrs) sb.Append(' ').Append(key).Append("=\"").Append(value).Append('"');
                sb.Append('>');
            }

            sb.Append(op.Text ?? XmlStrings.Of(op.Embed));
            for (var i = tags.Count - 1; i >= 0; i--) sb.Append("</").Append(tags[i].Name).Append('>');
        }

        return sb.ToString();
    }

    /// <summary>Yjs <c>toJSON</c>: the tagged string, unlike YText (X-T3).</summary>
    public override YValue ToJson() => ToString();
}
