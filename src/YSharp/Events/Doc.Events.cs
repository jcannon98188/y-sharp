#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public sealed partial class Doc
{
    // ---- events: registration order, snapshot dispatch (CC-12, CC-13)
    public event TransactionHandler? BeforeAllTransactions, BeforeTransaction, BeforeObserverCalls, AfterTransaction, AfterTransactionCleanup;
    public event AllTransactionsHandler? AfterAllTransactions;
    public event UpdateHandler? Update, UpdateV2;            // encoded only while subscribed and content changed (DOC-10)

    public event ObserverFaultHandler? ObserverFaulted;      // ADR 0005: handler exceptions are reported here, not thrown

}
