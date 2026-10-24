#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- permanent user data

public sealed class PermanentUserDataOptions
{
    public YMap? Store { get; init; }                      // default doc.GetMap("users")
    public Action<Action>? Defer { get; init; }            // seam for the setTimeout(0) in PUD-03/04; tests pass a synchronous flush
}

public sealed class PermanentUserData
{
    public PermanentUserData(Doc doc, PermanentUserDataOptions? options = null) { }
    public void SetUserMapping(Doc doc, ulong clientId, string description, Func<Transaction, DeleteSet, bool>? filter = null) => throw Sketch.NI;
    public string? GetUserByClientId(ulong clientId) => throw Sketch.NI;
    public string? GetUserByDeletedId(ItemId id) => throw Sketch.NI;
}

