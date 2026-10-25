#nullable enable

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

/// <summary>Yjs <c>ContentDoc</c>: a subdocument by guid with the options it was created with (an Any object: gc, autoLoad, meta).</summary>
internal sealed class SubdocContent
{
    public SubdocContent(string guid, YValue options)
    {
        Guid = guid;
        Options = options;
    }

    public string Guid { get; }

    public YValue Options { get; }
}
