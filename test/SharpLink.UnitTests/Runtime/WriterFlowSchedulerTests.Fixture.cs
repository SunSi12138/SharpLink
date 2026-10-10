namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterFlowSchedulerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private sealed class Admission(bool allowed) : IWriterReadyAdmission
    {
        public bool TryReserve(int serializedBytes) => allowed;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<WriterReadyFrame> _held = new();
        private readonly List<WriterFlowScheduler.StreamLease> _leases = new();
        private readonly int _window;
        private readonly int _connectionWindow;
        internal readonly WriterFlowScheduler Scheduler;

        internal Fixture(int streamWindow = 4, int connectionWindow = 4, int maxStreams = 8,
            long preparedBytes = 4096, int slots = 16, int quantum = 16,
            Action<IRpcByteBufferWriter>? returnPacket = null)
        {
            _window = streamWindow;
            _connectionWindow = connectionWindow;
            Scheduler = new WriterFlowScheduler(streamWindow, connectionWindow, 1024, maxStreams,
                preparedBytes, 1024, static () => { }, returnPacket ?? (static packet => packet.Dispose()),
                slots, quantum);
        }

        internal async ValueTask<WriterFlowScheduler.StreamLease> Open(long requestId,
            CancellationToken token = default, bool startAllowed = true)
        {
            var lease = await Scheduler.OpenAsync(requestId, 0, token, startAllowed);
            _leases.Add(lease);
            return lease;
        }

        internal WriterReadyFrame Take(byte expected)
        {
            Ensure(Scheduler.TryTake(new Admission(true), out var frame), "no eligible frame");
            _held.Add(frame);
            Ensure(((Packet)frame.Packet).Marker == expected, "unexpected scheduling order");
            return frame;
        }

        internal void PollEmpty()
        {
            var taken = Scheduler.TryTake(new Admission(true), out var frame);
            if (taken)
                _held.Add(frame);
            Ensure(!taken, "unexpected eligible DATA or terminal");
        }

        internal void Release(WriterReadyFrame frame)
        {
            Ensure(_held.Remove(frame), "fixture released an unknown frame");
            frame.Packet.Dispose();
            frame.Completion.Complete(frame.CreditBytes, null);
        }

        internal void AssertCredit()
        {
            var accounted = Scheduler.ConnectionCredit;
            foreach (var stream in _leases)
                accounted += _window - stream.Credit;
            Ensure(accounted == _connectionWindow, "connection and stream credit do not conserve bytes");
        }

        public async ValueTask DisposeAsync()
        {
            for (var index = _held.Count - 1; index >= 0; index--)
                Release(_held[index]);
            Scheduler.Stopped(new IOException("fixture stopped"));
            try
            {
                await Scheduler.StoppedTask.WaitAsync(Limit);
            }
            catch (IOException)
            {
            }
            catch (AggregateException)
            {
            }
        }
    }

    private sealed class Packet : IRpcByteBufferWriter
    {
        private readonly byte[] _bytes;
        private int _written;
        internal int Returns;
        internal byte Marker => _bytes[0];

        internal Packet(byte marker, int length = 24)
        {
            _bytes = new byte[length];
            _bytes[0] = marker;
            _written = length;
        }

        public int WrittenCount => _written;
        public int Capacity => _bytes.Length;
        public ReadOnlyMemory<byte> WrittenMemory => _bytes.AsMemory(0, _written);
        public Span<byte> WrittenSpan => _bytes.AsSpan(0, _written);
        public void Clear() => _written = 0;
        public void Advance(int count) => _written = checked(_written + count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _bytes.AsMemory(_written);
        public Span<byte> GetSpan(int sizeHint = 0) => _bytes.AsSpan(_written);
        public void Dispose()
        {
            if (Interlocked.Increment(ref Returns) != 1)
                throw new InvalidOperationException("packet returned twice");
        }
    }

    private static async ValueTask Expect<T>(Func<ValueTask> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
