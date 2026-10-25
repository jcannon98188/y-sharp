#nullable enable
using System.Collections.Generic;

namespace YSharp;

// M2 lifecycle hook points on the transaction path (ADR 0021, Seams). Each is a bodiless partial method, so an unimplemented hook compiles
// away. The owning specs implement a hook in their own file of this partial class, with the same signature: observers and the doc events
// (TASK-4.27), subdocs (TASK-4.33), merge and garbage collection (TASK-4.15), update emission (TASK-4.14, TASK-4.27).
//
// When they run (Doc.Transactions.cs, following Yjs transact and cleanupTransactions):
//   opening a transaction: HookBeforeAllTransactions (first of a round only), HookBeforeTransaction
//   cleanup of each transaction, in round order: HookBeforeObservers, HookObservers, HookAfterTransaction, HookFormattingCleanup (TASK-4.25),
//     then, even if one of those threw,
//     HookGarbageCollection (Yjs gc and struct merging), HookAfterTransactionCleanup (Yjs afterTransactionCleanup, then update and
//     updateV2), HookSubdocs
//   end of the round: HookAfterAllTransactions with the round's transactions
// The write lock is held throughout (CC-11). A write made from a hook runs in a follow-up transaction of the same round (CC-2).
public sealed partial class Doc
{
    partial void HookBeforeAllTransactions();
    partial void HookBeforeTransaction(Transaction tx);
    partial void HookBeforeObservers(Transaction tx);
    partial void HookObservers(Transaction tx);
    partial void HookAfterTransaction(Transaction tx);
    partial void HookFormattingCleanup(Transaction tx);
    partial void HookGarbageCollection(Transaction tx);
    partial void HookAfterTransactionCleanup(Transaction tx);
    partial void HookSubdocs(Transaction tx);
    partial void HookAfterAllTransactions(IReadOnlyList<Transaction> transactions);
}
