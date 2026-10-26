#nullable enable
using YSharp.Transactions;

namespace YSharp;

/// using-form of a transaction. ref struct so it cannot cross await; thread-affine (CC-15). Dispose is idempotent (CC-16).
public ref struct TransactionScope
{
    private readonly Doc? _doc;
    private readonly Transaction? _tx;
    private readonly LockEntry _entry;
    private readonly bool _initial;
    private bool _disposed;

    internal TransactionScope(Doc doc, Transaction tx, LockEntry entry, bool initial)
    {
        _doc = doc;
        _tx = tx;
        _entry = entry;
        _initial = initial;
        _disposed = false;
    }

    public Transaction Transaction => _tx ?? throw new YLifetimeException("The scope holds no transaction.");

    public void Dispose()
    {
        if (_disposed || _doc is null) return;
        _disposed = true;
        _doc.Leave(_tx!, _entry, _initial);
    }
}
