#nullable enable
using YSharp.Store;

namespace YSharp;

public sealed partial class Doc
{
    partial void HookFormatIntegrated(Transaction tx, ItemRef item) => item.Parent!.MarkFormatted();
}
