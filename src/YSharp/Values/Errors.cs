#nullable enable
using System;

namespace YSharp;

// ---------------------------------------------------------------- errors (ADR 0005)

/// <summary>
/// What went wrong with a non-throwing operation, as a code. JS error classes and messages are collapsed into these codes and never
/// compared (ADR 0005).
/// </summary>
public enum YError { None, Malformed, LimitExceeded, Rejected, UnknownMessage }

/// <summary>Result of a non-throwing operation that does not apply updates. <c>default</c> is success.</summary>
public readonly struct YStatus
{
    internal YStatus(YError error) => Error = error;

    public YError Error { get; }

    public bool Ok => Error == YError.None;

    public override string ToString() => Error.ToString();
}

/// <summary>The base of every exception y-sharp raises for programmer errors and for the throwing forms of decode operations.</summary>
public class YException : Exception
{
    public YException() { }

    public YException(string message) : base(message) { }

    public YException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The throwing form of a decode operation failed. It carries the code the non-throwing form returns; there is no JS message to compare.</summary>
public sealed class YDecodeException : YException
{
    internal YDecodeException(YError error) : base(Describe(error)) => Error = error;

    public YError Error { get; }

    private static string Describe(YError error) => error switch
    {
        YError.Malformed => "The input is malformed.",
        YError.LimitExceeded => "The input exceeds a configured limit.",
        YError.Rejected => "The input was rejected.",
        YError.UnknownMessage => "The message type is not known.",
        _ => "The input could not be decoded.",
    };
}

/// <summary>Lock-protocol misuse: read-to-write upgrade (CC-3), lock-order violation (CC-18).</summary>
public sealed class YConcurrencyException : YException
{
    internal YConcurrencyException() : base("The document lock was used incorrectly.") { }

    internal YConcurrencyException(string message) : base(message) { }
}

/// <summary>
/// Use of a pooled or live view after the transaction or dispatch that produced it ended (CC-9): a Transaction, YEvent, change collection or
/// enumerator taken inside an explicit transaction. Thrown by every member except IsValid; never returns old contents (ADR 0005, 0006).
/// </summary>
public sealed class YLifetimeException : YException
{
    internal YLifetimeException() : base("The object was used after the transaction or dispatch that produced it ended.") { }

    internal YLifetimeException(string message) : base(message) { }
}

/// <summary>A violated internal invariant. The document that raised it is faulted: readable, but closed to mutation (ADR 0005).</summary>
public sealed class YInternalException : YException
{
    internal YInternalException() : base("An internal invariant was violated.") { }

    internal YInternalException(string message) : base(message) { }

    internal YInternalException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary><c>doc.Get(name)</c> with a different concrete type than the one the name already has.</summary>
public sealed class YTypeConflictException : YException
{
    internal YTypeConflictException() : base("The shared type was already requested as a different type.") { }

    internal YTypeConflictException(string message) : base(message) { }
}
