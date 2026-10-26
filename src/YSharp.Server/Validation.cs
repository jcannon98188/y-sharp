using System;
using System.Collections.Generic;
using System.Threading;

namespace YSharp.Server;

/// <summary>Who is on a connection, as the host authenticated them: an identity, its claims, and whether the connection may write (ADR 0024 SRV-14, SRV-17).</summary>
public sealed record ConnectionContext
{
    public static readonly ConnectionContext Anonymous = new();

    public string? Identity { get; init; }

    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// A read-only connection is answered when it syncs, and its sync <c>Update</c> and <c>Step2</c> messages are dropped at the protocol layer, with
    /// no <see cref="Doc"/> (SRV-17). Awareness is unaffected.
    /// </summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>What a validation hook is given for one incoming update (ADR 0024 SRV-14).</summary>
public readonly struct ValidationRequest
{
    internal ValidationRequest(string documentId, Doc document, ReadOnlyMemory<byte> update, DecodedUpdate decoded, ConnectionContext connection, CancellationToken deadline)
    {
        DocumentId = documentId;
        Document = document;
        Update = update;
        Decoded = decoded;
        Connection = connection;
        Deadline = deadline;
    }

    public string DocumentId { get; }

    /// <summary>
    /// The document as the server holds it, before the update. The hook runs inside a read transaction on it: it may read, and must not
    /// write or keep the reference.
    /// </summary>
    public Doc Document { get; }

    /// <summary>The update's V1 bytes, as the client sent them.</summary>
    public ReadOnlyMemory<byte> Update { get; }

    /// <summary>The update's structs and delete set.</summary>
    public DecodedUpdate Decoded { get; }

    public ConnectionContext Connection { get; }

    /// <summary>Fires at <see cref="YServerOptions.ValidationDeadline"/>. A hook that is still running then is rejected when it returns.</summary>
    public CancellationToken Deadline { get; }
}

/// <summary>A hook's verdict on one update: <see cref="Accept"/>, or <see cref="Reject"/> with a reason the client is sent.</summary>
public readonly record struct ValidationResult(bool Accepted, string? Reason)
{
    public static ValidationResult Accept => new(true, null);

    public static ValidationResult Reject(string reason) => new(false, reason ?? throw new ArgumentNullException(nameof(reason)));
}

/// <summary>
/// The semantic validation hook (ADR 0024 SRV-14): synchronous, run on the document's mailbox worker inside one read transaction, before the
/// update is persisted or broadcast (SRV-15). A hook that throws or overruns its deadline counts as a reject.
/// </summary>
public delegate ValidationResult ValidationHook(in ValidationRequest request);
