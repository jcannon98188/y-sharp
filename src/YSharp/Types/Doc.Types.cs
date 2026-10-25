#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    // ---- root types. Get(name) returns an untyped placeholder that a later typed Get upgrades.
    public YArray GetArray(string name = "") => GetRoot(name, static () => new YArray());
    public YMap GetMap(string name = "") => GetRoot(name, static () => new YMap());
    public YText GetText(string name = "") => GetRoot(name, static () => new YText());
    public YXmlFragment GetXmlFragment(string name = "") => GetRoot(name, static () => new YXmlFragment());
    public YXmlText GetXmlText(string name = "") => GetRoot(name, static () => new YXmlText());
    public YXmlElement GetXmlElement(string name = "") => GetRoot(name, static () => new YXmlElement());   // nodeName "UNDEFINED" (X-E8)

    public YType Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_rootGate)
        {
            var branch = RootBranch(name);
            if (branch.Owner is YType owner) return owner;
            var untyped = new YUntyped();
            untyped.AdoptRoot(branch, this);
            return untyped;
        }
    }

    /// <summary>
    /// Yjs <c>doc.get(name, Constructor)</c>: the root of that class, created on first use; an untyped root (named by an update or by
    /// <see cref="Get"/>) is upgraded in place. A root already defined with another class throws <see cref="YTypeConflictException"/>.
    /// </summary>
    private T GetRoot<T>(string name, Func<T> create) where T : YType
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_rootGate)
        {
            var branch = RootBranch(name);
            switch (branch.Owner)
            {
                case T typed when typed.GetType() == typeof(T):
                    return typed;
                case null:
                case YUntyped:
                    var type = create();
                    type.AdoptRoot(branch, this);
                    return type;
                default:
                    throw new YTypeConflictException($"Type with the name {name} has already been defined with a different constructor.");
            }
        }
    }
    /// <summary>Every root type by name, in creation order (Yjs <c>doc.share</c>); a root only named by an update is an untyped placeholder.</summary>
    public IReadOnlyDictionary<string, YType> Share   // snapshot
    {
        get
        {
            lock (_rootGate)
            {
                var names = new List<string>();
                foreach (var (name, _) in RootBranches) names.Add(name);
                var share = new Dictionary<string, YType>(names.Count, StringComparer.Ordinal);
                foreach (var name in names) share[name] = Get(name);
                return share;
            }
        }
    }

    /// <summary>Yjs <c>doc.toJSON()</c>: each root's <c>toJSON()</c> by name.</summary>
    public YValue ToJson()   // deprecated in JS, kept for DOC-07
    {
        var members = new List<KeyValuePair<string, YValue>>();
        foreach (var (name, type) in Share) members.Add(new(name, type.ToJson()));
        return YValue.Object(members.ToArray());
    }

    partial void HookTypeIntegrated(Transaction tx, Store.Branch type)
    {
        if (type.Owner is not YType owner) return;   // read from an update: wrapped on first read
        owner.AttachedDoc = this;
        owner.OnIntegrated(tx);
    }

}
