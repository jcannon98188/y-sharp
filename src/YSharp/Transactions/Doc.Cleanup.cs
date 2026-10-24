#nullable enable
namespace YSharp;

// M2 lifecycle hook points on the transaction cleanup path (ADR 0021, Seams). Each is a bodiless partial method, so an unimplemented
// hook compiles away. The owning specs implement a hook in their own file of this partial class, with the same signature:
// observers (TASK-4.27), subdocs (TASK-4.33) and garbage collection (TASK-4.15). Cleanup (TASK-4.12) invokes them in this order.
public sealed partial class Doc
{
    partial void HookBeforeObservers(Transaction tx);
    partial void HookObservers(Transaction tx);
    partial void HookAfterTransaction(Transaction tx);
    partial void HookSubdocs(Transaction tx);
    partial void HookGarbageCollection(Transaction tx);
}
