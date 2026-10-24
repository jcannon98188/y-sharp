#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public delegate void UpdateHandler(ReadOnlySpan<byte> update, object? origin, Doc doc, Transaction tx);   // span: zero-alloc, valid during the call only
public delegate void TransactionHandler(Transaction tx, Doc doc);
public delegate void AllTransactionsHandler(IReadOnlyList<Transaction> transactions, Doc doc);   // list is pooled: valid during dispatch only (A5)
public delegate void ObserverFaultHandler(Exception fault, object handlerOwner);

public delegate void YEventHandler<in TEvent>(TEvent e, Transaction tx) where TEvent : YEvent;
public delegate void YDeepEventHandler(IReadOnlyList<YEvent> events, Transaction tx);

