namespace SharpLink.Client;

// One client-owned budget shared by all physical Sessions, independent of pool/endpoint count.
internal sealed class ClientBindingBudget
{
    private int _operations;
    private int _trackingSlots;
    private int _candidateRecords;
    private readonly Lock _trackingGate = new();
    private readonly LinkedList<ClientBindingSession> _sessions = new();
    private LinkedListNode<ClientBindingSession>? _scanSession;
    private int _scanSlot;
    private bool _trackingSealed;
    private Action<Task, string>? _observeTask;

    internal int CandidateRecords => Volatile.Read(ref _candidateRecords);
    internal bool TrackingSealed => Volatile.Read(ref _trackingSealed);

    // Cold registration only. The scan gate is never held while entering a Session/site gate.
    internal LinkedListNode<ClientBindingSession> RegisterSession(ClientBindingSession session)
    {
        lock (_trackingGate)
        {
            if (_trackingSealed) throw new ObjectDisposedException(nameof(ClientBindingBudget));
            var node = _sessions.AddLast(session);
            _scanSession ??= node;
            return node;
        }
    }

    internal void UnregisterSession(LinkedListNode<ClientBindingSession> node)
    {
        lock (_trackingGate)
        {
            if (ReferenceEquals(_scanSession, node))
            {
                _scanSession = node.Next ?? _sessions.First;
                if (ReferenceEquals(_scanSession, node)) _scanSession = null;
                _scanSlot = 0;
            }
            _sessions.Remove(node);
        }
    }

    internal void AddCandidateRecord() => Interlocked.Increment(ref _candidateRecords);
    internal void RemoveCandidateRecord() => Release(ref _candidateRecords, 1);

    internal bool TryTakeScanSlot(out ClientBindingSession? session, out int slot)
    {
        lock (_trackingGate)
        {
            if (_scanSession is null || _trackingSealed)
            {
                session = null;
                slot = 0;
                return false;
            }
            session = _scanSession.Value;
            slot = _scanSlot++;
            if (_scanSlot == session.TrackingCapacity)
            {
                _scanSlot = 0;
                _scanSession = _scanSession.Next ?? _sessions.First;
            }
            return true;
        }
    }

    internal ClientBindingSession[] SealAndCaptureSessions()
    {
        lock (_trackingGate)
        {
            Volatile.Write(ref _trackingSealed, true);
            return _sessions.ToArray();
        }
    }

    internal void SetTaskObserver(Action<Task, string> observer)
    {
        lock (_trackingGate) _observeTask ??= observer;
    }

    internal void Observe(Task task, string operation)
    {
        Action<Task, string>? observer;
        lock (_trackingGate) observer = _observeTask;
        if (observer is not null)
        {
            observer(task, operation); // Framework supervisor, always outside Session/publication/site gates.
            return;
        }
        // Direct Session fixtures have no client supervisor. This does not end physical ownership.
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal const int MaximumOperations = 1024;
    internal const int MaximumTrackingSlots = 65536;
    internal int Operations => Volatile.Read(ref _operations);
    internal int TrackingSlots => Volatile.Read(ref _trackingSlots);

    internal bool TryReserveOperation() => TryReserve(ref _operations, 1, MaximumOperations);
    internal bool TryReserveTrackingSlots(int slots) => TryReserve(ref _trackingSlots, slots, MaximumTrackingSlots);
    internal void ReleaseOperation() => Release(ref _operations, 1);
    internal void ReleaseTrackingSlots(int slots) => Release(ref _trackingSlots, slots);

    private static bool TryReserve(ref int counter, int amount, int maximum)
    {
        if (amount <= 0 || amount > maximum)
            return false;
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current > maximum - amount)
                return false;
            if (Interlocked.CompareExchange(ref counter, current + amount, current) == current)
                return true;
        }
    }
    private static void Release(ref int counter, int amount)
    {
        if (Interlocked.Add(ref counter, -amount) < 0)
            throw new InvalidOperationException("Binding budget was released more than once.");
    }
}
