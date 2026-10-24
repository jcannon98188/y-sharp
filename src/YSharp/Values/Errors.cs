#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- errors (ADR 0005)

public enum YError { None, Malformed, LimitExceeded, Rejected, UnknownMessage }

/// Result of a non-throwing operation that does not apply updates.
public readonly struct YStatus { public YError Error { get; } public bool Ok => Error == YError.None; }


public class YException : Exception { }
public sealed class YDecodeException : YException { public YError Error { get; } }
public sealed class YConcurrencyException : YException { }   // lock-protocol misuse: read-to-write upgrade (CC-3), lock-order violation (CC-18)
/// Use of a pooled or live view after the transaction or dispatch that produced it ended (CC-9): a Transaction, YEvent, change collection or
/// enumerator taken inside an explicit transaction. Thrown by every member except IsValid; never returns old contents (ADR 0005, 0006).
public sealed class YLifetimeException : YException { }
public sealed class YInternalException : YException { }
public sealed class YTypeConflictException : YException { }   // doc.Get(name) with a different concrete type

