#if SHARPLINK_ALLOCATION_PATH_OBSERVATION
namespace SharpLink.Runtime;

// Diagnostic build only. Synchronous call boundaries are disjoint by thread-local
// nesting; allocations after a suspension/on another thread remain unattributed.
internal static class AllocationPathObservation
{
    internal enum Path { ClientRead, ServerRead, ControlPipeRead, RpcStart, CalibrationOuter, CalibrationInner }
    private static readonly Counter[] Counters = [new(), new(), new(), new(), new(), new()];
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static bool _seen;
    private static long _firstThreadTouches;
    private static long _sequence;
    private static int _active;

    internal static Scope Enter(Path path)
    {
        if (!_seen)
        {
            _seen = true;
            Interlocked.Increment(ref _firstThreadTouches);
            Interlocked.Increment(ref _sequence);
        }
        Interlocked.Increment(ref _active);
        var root = _depth++ == 0;
        return new Scope(path, root, root ? GC.GetAllocatedBytesForCurrentThread() : 0);
    }

    internal static ValueTask<ReadResult> Read(SharedMemoryPipeReader reader, CancellationToken cancellationToken, Path path)
    {
        var scope = Enter(path);
        try
        {
            var result = reader.ReadAsync(cancellationToken);
            scope.Observe(result.IsCompleted);
            return result;
        }
        finally { scope.Dispose(); }
    }

    internal static PipeReader Wrap(SharedMemoryPipeReader reader, bool isClient)
        => new ObservedReader(reader, isClient ? Path.ClientRead : Path.ServerRead);

    internal static SharedMemoryPipeReader Unwrap(PipeReader reader)
        => reader is ObservedReader observed ? observed.Inner : (SharedMemoryPipeReader)reader;

    private sealed class ObservedReader(SharedMemoryPipeReader inner, Path path) : PipeReader
    {
        internal SharedMemoryPipeReader Inner => inner;
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => Read(inner, cancellationToken, path);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    internal static ValueTask<int> ReadControl(PipeStream stream, Memory<byte> buffer)
    {
        var scope = Enter(Path.ControlPipeRead);
        try
        {
            var result = stream.ReadAsync(buffer);
            scope.Observe(result.IsCompleted);
            return result;
        }
        finally { scope.Dispose(); }
    }

    internal static ValueTask StartRpc(Func<ValueTask> operation)
    {
        var scope = Enter(Path.RpcStart);
        try
        {
            var result = operation();
            scope.Observe(result.IsCompleted);
            return result;
        }
        finally { scope.Dispose(); }
    }

    internal static Snapshot Capture()
    {
        var sequenceBefore = Volatile.Read(ref _sequence);
        var activeBefore = Volatile.Read(ref _active);
        var touches = Interlocked.Read(ref _firstThreadTouches);
        var rows = new Row[Counters.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var counter = Counters[i];
            rows[i] = new Row(((Path)i).ToString(), Interlocked.Read(ref counter.Calls),
                Interlocked.Read(ref counter.Completed), Interlocked.Read(ref counter.Incomplete),
                Interlocked.Read(ref counter.Throws), Interlocked.Read(ref counter.RootCalls),
                Interlocked.Read(ref counter.RootBytes));
        }
        var activeAfter = Volatile.Read(ref _active);
        var sequenceAfter = Volatile.Read(ref _sequence);
        return new Snapshot(sequenceBefore == sequenceAfter && activeBefore == 0 && activeAfter == 0,
            activeBefore, activeAfter, touches, rows);
    }

    internal struct Scope(Path path, bool root, long before) : IDisposable
    {
        private int _outcome;
        internal void Observe(bool completed) => _outcome = completed ? 1 : 2;
        public void Dispose()
        {
            var bytes = root ? GC.GetAllocatedBytesForCurrentThread() - before : 0;
            _depth--;
            var counter = Counters[(int)path];
            Interlocked.Increment(ref counter.Calls);
            if (_outcome == 1) Interlocked.Increment(ref counter.Completed);
            else if (_outcome == 2) Interlocked.Increment(ref counter.Incomplete);
            else Interlocked.Increment(ref counter.Throws);
            if (root)
            {
                Interlocked.Increment(ref counter.RootCalls);
                Interlocked.Add(ref counter.RootBytes, bytes);
            }
            Interlocked.Increment(ref _sequence);
            Interlocked.Decrement(ref _active);
        }
    }

    internal sealed record Snapshot(bool Stable, int ActiveBefore, int ActiveAfter, long FirstThreadTouches, Row[] Paths);
    internal sealed record Row(string Name, long Calls, long Completed, long Incomplete, long Throws, long RootCalls, long RootBytes);
    private sealed class Counter
    {
        internal long Calls, Completed, Incomplete, Throws, RootCalls, RootBytes;
    }
}
#endif
