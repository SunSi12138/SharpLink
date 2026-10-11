namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterOwnedStreamSchedulerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static async Task ExpectFailure(ValueTask operation)
    {
        var failed = false;
        try { await operation.AsTask().WaitAsync(Limit); }
        catch (Exception error) when (error is not TimeoutException) { failed = true; }
        Ensure(failed, "expected operation failure");
    }

    private static byte Marker(WriterReadyFrame frame) => ((Packet)frame.Packet).Marker;

    private static void Finish(WriterReadyFrame frame, Exception? error = null)
    {
        frame.Packet.Dispose();
        frame.Completion.Complete(frame.CreditBytes, error);
    }

    private sealed class Admission(bool accepted) : IWriterReadyAdmission
    {
        public bool TryReserve(int serializedBytes) => accepted;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly WriterOwnedStreamScheduler Owner;
        internal Fixture(int window = 64, int connection = 64, int maxStreams = 8,
            int slots = 8, int prepared = 4096, int maxPacket = 128, CancellationToken cancellation = default)
        {
            Owner = new WriterOwnedStreamScheduler(window, connection, maxStreams, slots,
                maxPacket, 64, prepared, static () => { }, static packet => packet.Dispose(), cancellation);
        }
        internal WriterReadyFrame Take()
        {
            Ensure(Owner.TryTake(new Admission(true), out var frame), "expected eligible frame");
            return frame;
        }
        internal WriterReadyFrame PeekAndFinish()
        {
            var frame = Take();
            Finish(frame);
            return frame;
        }
        internal void PumpChanges()
        {
            Ensure(!Owner.TryTake(new Admission(false), out _), "control-only drain admitted a frame");
        }
        public void Dispose() => Owner.Stopped(new IOException("fixture closed"));
    }

    private sealed class Packet : IRpcByteBufferWriter
    {
        private readonly byte[] _bytes;
        private int _written;
        internal readonly byte Marker;
        internal int Returns;
        internal Packet(byte marker, int bytes = 24)
        {
            Marker = marker;
            _bytes = new byte[bytes];
            _bytes[0] = marker;
            _written = bytes;
        }
        private void CheckLive()
        {
            if (Volatile.Read(ref Returns) != 0) throw new InvalidOperationException("packet was already returned");
        }
        public int WrittenCount { get { CheckLive(); return _written; } }
        public int Capacity => _bytes.Length;
        public ReadOnlyMemory<byte> WrittenMemory { get { CheckLive(); return _bytes.AsMemory(0, _written); } }
        public Span<byte> WrittenSpan { get { CheckLive(); return _bytes.AsSpan(0, _written); } }
        public void Clear() { CheckLive(); _written = 0; }
        public void Advance(int count) { CheckLive(); _written = checked(_written + count); }
        public Memory<byte> GetMemory(int sizeHint = 0) { CheckLive(); return _bytes.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) { CheckLive(); return _bytes.AsSpan(_written); }
        public void Dispose()
        {
            if (Interlocked.Increment(ref Returns) != 1) throw new InvalidOperationException("packet returned twice");
            _written = 0;
        }
    }
}
