#nullable enable
using System;
using System.Threading;

namespace YSharp.Transactions;

/// <summary>How a thread entered the document lock, so leaving it undoes exactly that.</summary>
internal enum LockEntry : byte
{
    None,
    /// <summary>The thread took the write lock.</summary>
    WriteFresh,
    /// <summary>The thread already held the write lock (CC-2): the write depth went up.</summary>
    WriteJoined,
    /// <summary>The thread took a shared read lock.</summary>
    ReadFresh,
    /// <summary>The thread already held a read lock: its read depth went up.</summary>
    ReadJoined,
    /// <summary>The thread holds the write lock and asked for a read: it reads through the write transaction, nothing to undo.</summary>
    ReadInWrite,
}

/// <summary>
/// The document lock (ADR 0006 CC-1..CC-5): exclusive writers, shared readers, re-entrant per thread with owner tracking, and a waiting writer
/// blocks new readers. Built only from <see cref="Monitor"/> and explicit state, so the schedule explorer controls it: every transition
/// reports a <see cref="ScheduleSeam.Point"/> and every wait goes through <see cref="ScheduleSeam.Block"/> when a scheduler is installed. Seam
/// calls are made with no monitor held. An uncontended enter and exit allocates nothing (CC-7).
/// </summary>
internal sealed class DocLock
{
    private struct Reader
    {
        public int Thread;
        public int Depth;
        public Transaction Tx;
    }

    private readonly object _sync = new();
    private readonly Doc _doc;
    private readonly Func<bool> _canWrite;
    private readonly Func<bool> _canRead;

    private int _writer;            // managed thread id of the write owner, 0 when there is none
    private int _writeDepth;
    private int _waitingWriters;
    private Reader[] _readers = new Reader[4];
    private int _readerCount;       // distinct reader threads, the first _readerCount entries of _readers
    private Transaction[] _readPool = new Transaction[4];
    private int _readPoolCount;

    internal DocLock(Doc doc)
    {
        _doc = doc;
        _canWrite = () => Volatile.Read(ref _writer) == 0 && Volatile.Read(ref _readerCount) == 0;
        _canRead = () => Volatile.Read(ref _writer) == 0 && Volatile.Read(ref _waitingWriters) == 0;
    }

    /// <summary>True when the calling thread holds the write lock.</summary>
    internal bool IsWriteOwner => Volatile.Read(ref _writer) == Environment.CurrentManagedThreadId;

    /// <summary>For tests and invariants: no writer, no reader and no waiting writer.</summary>
    internal bool IsFree
    {
        get { lock (_sync) return _writer == 0 && _readerCount == 0 && _waitingWriters == 0; }
    }

    private static void Point(string id) => ScheduleSeam.Point?.Invoke(id);

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Takes the write lock, waiting at most <paramref name="timeoutMs"/> (-1: no limit). Throws <see cref="YConcurrencyException"/> when the
    /// calling thread holds a read lock (CC-3), <see cref="TimeoutException"/> or <see cref="OperationCanceledException"/> when it gives up (CC-6).
    /// </summary>
    internal LockEntry EnterWrite(int timeoutMs, CancellationToken ct)
    {
        var me = Environment.CurrentManagedThreadId;
        Point("lock.write.acquire");
        ct.ThrowIfCancellationRequested();
        var deadline = timeoutMs < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMs;
        var waiting = false;
        var seamIneffective = false;
        CancellationTokenRegistration registration = default;
        try
        {
            while (true)
            {
                var useSeam = false;
                lock (_sync)
                {
                    if (_writer == me)
                    {
                        _writeDepth++;
                        return LockEntry.WriteJoined;
                    }

                    if (FindReader(me) < 0)
                    {
                        if (_writer == 0 && _readerCount == 0)
                        {
                            if (waiting)
                            {
                                waiting = false;
                                _waitingWriters--;
                            }

                            _writer = me;
                            _writeDepth = 1;
                            return LockEntry.WriteFresh;
                        }

                        if (!waiting)
                        {
                            waiting = true;
                            _waitingWriters++;
                            if (ct.CanBeCanceled) registration = ct.UnsafeRegister(static s => ((DocLock)s!).PulseAll(), this);
                        }

                        ct.ThrowIfCancellationRequested();
                        var remaining = Remaining(deadline);
                        if (remaining == 0) throw new TimeoutException("The document lock was not acquired within the timeout.");
                        if (ScheduleSeam.Block != null && !seamIneffective) useSeam = true;
                        else
                        {
                            Monitor.Wait(_sync, remaining);
                            continue;
                        }
                    }
                }

                if (!useSeam)
                {
                    // the calling thread holds a read lock: an upgrade is not atomic, so it is refused (CC-3)
                    Point("lock.upgrade.reject");
                    throw new YConcurrencyException("A read transaction cannot be upgraded to a write transaction on the same thread (CC-3).");
                }

                Seam("lock.write.wait", _canWrite, ref seamIneffective);
            }
        }
        finally
        {
            if (waiting)
            {
                // gave up: no longer a waiting writer, so readers held back by it may go in
                lock (_sync)
                {
                    _waitingWriters--;
                    Monitor.PulseAll(_sync);
                }
            }

            registration.Dispose();
        }
    }

    /// <summary>Never waits (CC-6). Joins the calling thread's write lock, refuses an upgrade (CC-3), or reports contention.</summary>
    internal TryTransactStatus TryEnterWrite(out LockEntry entry)
    {
        var me = Environment.CurrentManagedThreadId;
        Point("lock.write.try");
        lock (_sync)
        {
            if (_writer == me)
            {
                _writeDepth++;
                entry = LockEntry.WriteJoined;
                return TryTransactStatus.Acquired;
            }

            entry = LockEntry.None;
            if (FindReader(me) >= 0) return TryTransactStatus.UpgradeRefused;
            if (_writer != 0 || _readerCount != 0) return TryTransactStatus.Contended;
            _writer = me;
            _writeDepth = 1;
            entry = LockEntry.WriteFresh;
            return TryTransactStatus.Acquired;
        }
    }

    /// <summary>Undoes one <see cref="EnterWrite"/> or successful <see cref="TryEnterWrite"/>. Leaving on another thread is refused (CC-15).</summary>
    internal void ExitWrite()
    {
        var me = Environment.CurrentManagedThreadId;
        var released = false;
        lock (_sync)
        {
            if (_writer != me) throw new YConcurrencyException("A write transaction was ended on a thread that does not hold it (CC-15).");
            if (--_writeDepth == 0)
            {
                _writer = 0;
                released = true;
                Monitor.PulseAll(_sync);
            }
        }

        if (released) Point("lock.write.release");
    }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Takes a shared read lock, or joins the calling thread's read or write lock. <paramref name="tx"/> is the read transaction for
    /// <see cref="LockEntry.ReadFresh"/> and <see cref="LockEntry.ReadJoined"/>, null for <see cref="LockEntry.ReadInWrite"/>.
    /// </summary>
    internal LockEntry EnterRead(out Transaction? tx)
    {
        var me = Environment.CurrentManagedThreadId;
        Point("lock.read.acquire");
        var seamIneffective = false;
        while (true)
        {
            lock (_sync)
            {
                if (_writer == me)
                {
                    tx = null;
                    return LockEntry.ReadInWrite;
                }

                var at = FindReader(me);
                if (at >= 0)
                {
                    _readers[at].Depth++;
                    tx = _readers[at].Tx;
                    return LockEntry.ReadJoined;
                }

                if (_writer == 0 && _waitingWriters == 0)
                {
                    tx = RentRead();
                    if (_readerCount == _readers.Length) Array.Resize(ref _readers, _readers.Length * 2);
                    _readers[_readerCount++] = new Reader { Thread = me, Depth = 1, Tx = tx };
                    return LockEntry.ReadFresh;
                }

                if (ScheduleSeam.Block == null || seamIneffective)
                {
                    Monitor.Wait(_sync);
                    continue;
                }
            }

            Seam("lock.read.wait", _canRead, ref seamIneffective);
        }
    }

    /// <summary>Undoes one <see cref="EnterRead"/> that returned <see cref="LockEntry.ReadFresh"/> or <see cref="LockEntry.ReadJoined"/>.</summary>
    internal void ExitRead()
    {
        var me = Environment.CurrentManagedThreadId;
        var released = false;
        lock (_sync)
        {
            var at = FindReader(me);
            if (at < 0) throw new YConcurrencyException("A read transaction was ended on a thread that does not hold it (CC-15).");
            if (--_readers[at].Depth == 0)
            {
                var tx = _readers[at].Tx;
                _readers[at] = _readers[--_readerCount];
                _readers[_readerCount] = default;
                tx.End();
                ReturnRead(tx);
                released = true;
                if (_readerCount == 0) Monitor.PulseAll(_sync);
            }
        }

        if (released) Point("lock.read.release");
    }

    /// <summary>True when the calling thread holds a read lock (not counting reads made through its write lock).</summary>
    internal bool HoldsRead
    {
        get
        {
            var me = Environment.CurrentManagedThreadId;
            lock (_sync) return FindReader(me) >= 0;
        }
    }

    /// <summary>The calling thread's read transaction, or null.</summary>
    internal Transaction? CurrentRead
    {
        get
        {
            var me = Environment.CurrentManagedThreadId;
            lock (_sync)
            {
                var at = FindReader(me);
                return at < 0 ? null : _readers[at].Tx;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private int FindReader(int thread)
    {
        for (var i = 0; i < _readerCount; i++)
            if (_readers[i].Thread == thread) return i;
        return -1;
    }

    private Transaction RentRead()
    {
        var tx = _readPoolCount > 0 ? _readPool[--_readPoolCount] : new Transaction(_doc, write: false);
        tx.Begin(null, local: true);
        return tx;
    }

    private void ReturnRead(Transaction tx)
    {
        if (_readPoolCount == _readPool.Length) Array.Resize(ref _readPool, _readPool.Length * 2);
        _readPool[_readPoolCount++] = tx;
    }

    private void PulseAll()
    {
        lock (_sync) Monitor.PulseAll(_sync);
    }

    /// <summary>Parks on the scheduler. A thread the scheduler does not control returns at once; it then waits on the monitor instead of spinning.</summary>
    private static void Seam(string id, Func<bool> ready, ref bool ineffective)
    {
        var block = ScheduleSeam.Block;
        if (block == null)
        {
            ineffective = true;
            return;
        }

        block(id, ready);
        if (!ready()) ineffective = true;
    }

    private static int Remaining(long deadline)
    {
        if (deadline == long.MaxValue) return Timeout.Infinite;
        var left = deadline - Environment.TickCount64;
        return left <= 0 ? 0 : (int)Math.Min(left, int.MaxValue);
    }
}
