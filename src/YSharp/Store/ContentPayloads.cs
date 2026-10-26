#nullable enable
using System.Collections.Generic;

namespace YSharp.Store;

// The payload objects of the single-element contents (ADR 0023: a struct holds an index into the store's payload table, the payload lives there).
// A binary content's payload is its byte[]; a nested type's is its Branch. The codecs create these when they decode and read them when they encode.

/// <summary>Yjs <c>ContentEmbed</c>: one embedded JSON value.</summary>
internal sealed class EmbedContent
{
    public EmbedContent(YValue value) => Value = value;

    public YValue Value { get; }
}

/// <summary>Yjs <c>ContentFormat</c>: one formatting attribute, a key and a JSON value (null ends a format).</summary>
internal sealed class FormatContent
{
    public FormatContent(string key, YValue value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public YValue Value { get; }
}

/// <summary>
/// Yjs <c>ContentDoc</c>: a subdocument. <see cref="Guid"/> and <see cref="Options"/> are what the wire carries: the document's guid and the
/// options Yjs rebuilds from the document (<c>gc: false</c>, <c>autoLoad: true</c> and <c>meta</c>, each only when it differs from the
/// default, in that order). <see cref="Doc"/> is the live document, created when the item integrates if the content was decoded.
/// </summary>
internal sealed class SubdocContent
{
    private SubdocContent(string guid, bool gc, bool autoLoad, YValue meta, bool shouldLoad, string? collectionId)
    {
        Guid = guid;
        Gc = gc;
        AutoLoad = autoLoad;
        Meta = meta;
        ShouldLoad = shouldLoad;
        CollectionId = collectionId;
        var members = new List<KeyValuePair<string, YValue>>(3);
        if (!gc) members.Add(new("gc", false));
        if (autoLoad) members.Add(new("autoLoad", true));
        if (meta.Kind is not (YValueKind.Undefined or YValueKind.Null)) members.Add(new("meta", meta));
        Options = YValue.Object(members.ToArray());
    }

    public string Guid { get; }
    public YValue Options { get; }
    public bool Gc { get; }
    public bool AutoLoad { get; }
    public YValue Meta { get; }

    /// <summary>The shouldLoad of a document made from this content: Yjs <c>opts.shouldLoad || opts.autoLoad || false</c>.</summary>
    public bool ShouldLoad { get; }

    /// <summary>A collection id the wire options carried; Yjs writes none, but <c>createDocFromOpts</c> takes one it reads.</summary>
    public string? CollectionId { get; }

    /// <summary>The subdocument; null until a decoded or copied content integrates. Read and written under the parent's lock.</summary>
    public Doc? Doc { get; set; }

    /// <summary>Yjs <c>new ContentDoc(doc)</c> for a local insert.</summary>
    public static SubdocContent Of(Doc doc) =>
        new(doc.Guid, doc.Gc, doc.AutoLoad, doc.Meta, doc.ShouldLoad, doc.CollectionId) { Doc = doc };

    /// <summary>
    /// Yjs <c>readContentDoc</c>: <c>createDocFromOpts(guid, opts)</c>, i.e. <c>new Doc({ guid, ...opts, shouldLoad: opts.shouldLoad ||
    /// opts.autoLoad || false })</c>. A <c>gc</c> that is present and falsy turns collection off, <c>autoLoad</c> is its truthiness, <c>meta</c>
    /// is kept as it is, a string <c>guid</c> or <c>collectionid</c> in the options wins, and options that are not an object count as none.
    /// </summary>
    public static SubdocContent FromWire(string guid, YValue options)
    {
        var gc = true;
        var autoLoad = false;
        var shouldLoad = false;
        YValue meta = YValue.Null;
        string? collectionId = null;
        if (options.TryGetObject(out var obj))
        {
            if (obj.TryGetValue("guid", out var g) && g.TryGetString(out var gs)) guid = gs;
            if (obj.TryGetValue("gc", out var gcValue) && gcValue.Kind != YValueKind.Undefined) gc = Truthy(gcValue);
            if (obj.TryGetValue("autoLoad", out var a)) autoLoad = Truthy(a);
            if (obj.TryGetValue("shouldLoad", out var sl)) shouldLoad = Truthy(sl);
            if (obj.TryGetValue("meta", out var m) && m.Kind != YValueKind.Undefined) meta = m;
            if (obj.TryGetValue("collectionid", out var c) && c.TryGetString(out var cs)) collectionId = cs;
        }

        return new SubdocContent(guid, gc, autoLoad, meta, shouldLoad || autoLoad, collectionId);
    }

    /// <summary>Yjs <c>ContentDoc.copy</c>: a new content for a new document with the same guid and options (SUB-10).</summary>
    public SubdocContent Copy() => new(Doc?.Guid ?? Guid, Gc, AutoLoad, Meta, AutoLoad, null);

    /// <summary>A new document for this content, with the parent's limits.</summary>
    public Doc CreateDoc(Doc parent) => Create(parent, Guid, ShouldLoad, CollectionId);

    /// <summary>The document a destroyed subdocument leaves behind: <c>new Doc({ guid, ...content.opts, shouldLoad: false })</c> (DOC-15).</summary>
    public Doc CreateUnloadedDoc(Doc parent, string guid) => Create(parent, guid, false, null);

    private Doc Create(Doc parent, string guid, bool shouldLoad, string? collectionId) => new(new DocOptions
    {
        Guid = guid,
        Gc = Gc,
        AutoLoad = AutoLoad,
        Meta = Meta,
        ShouldLoad = shouldLoad,
        CollectionId = collectionId,
        Limits = parent.Limits,
    });

    private static bool Truthy(in YValue v) => v.Kind switch
    {
        YValueKind.Undefined or YValueKind.Null => false,
        YValueKind.Bool => v.TryGetBool(out var b) && b,
        YValueKind.Number => v.TryGetNumber(out var d) && d != 0 && !double.IsNaN(d),
        YValueKind.BigInt => v.TryGetBigInt(out var l) && l != 0,
        YValueKind.String => v.TryGetString(out var s) && s.Length > 0,
        _ => true,
    };
}
