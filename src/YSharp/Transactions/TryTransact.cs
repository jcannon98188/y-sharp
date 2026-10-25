#nullable enable
namespace YSharp;

/// Acquired: the body ran. Contended: another thread holds the lock. UpgradeRefused: the calling thread holds a read transaction, so a write cannot
/// be taken without deadlock risk (CC-3); returned at once, the body does not run and the read lock is untouched. The throwing forms throw YConcurrencyException.
public enum TryTransactStatus { Acquired, Contended, UpgradeRefused }

public readonly struct TryTransactResult
{
    internal TryTransactResult(TryTransactStatus status) => Status = status;

    public TryTransactStatus Status { get; }

    public bool Acquired => Status == TryTransactStatus.Acquired;
}
