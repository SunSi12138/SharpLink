using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SharpLink.UnitTests.Runtime;

public sealed partial class WriterReadyPumpBridgeTests
{
    private enum SourceFault
    {
        None,
        ReserveThenFalse,
        ReserveThenThrow,
        ReserveTwice,
        WrongLength
    }

    private sealed class Source : IWriterReadySource, IWriterReadyCompletion
    {
        private readonly Queue<Packet> _frames;
        private readonly int _initialCount;
        internal SourceFault Fault;
        internal bool ForceFlushLast;
        internal int Releases;
        internal int Stops;
        internal int ReleasesAtStop;
        internal Exception? ReleaseError;
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Source(params Packet[] frames)
        {
            _frames = new Queue<Packet>(frames);
            _initialCount = frames.Length;
        }

        public bool HasWork => _frames.Count != 0;

        public bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame)
        {
            frame = default;
            if (!_frames.TryPeek(out var packet))
                return false;
            var reserve = packet.WrittenCount + (Fault == SourceFault.WrongLength ? 1 : 0);
            if (!admission.TryReserve(reserve))
                return false;
            if (Fault == SourceFault.ReserveThenFalse)
                return false;
            if (Fault == SourceFault.ReserveThenThrow)
                throw new IOException("injected source fault");
            if (Fault == SourceFault.ReserveTwice)
                admission.TryReserve(reserve);
            _frames.Dequeue();
            var forceFlush = ForceFlushLast && _frames.Count == 0;
            frame = new WriterReadyFrame(packet, forceFlush ? 0 : 1, this, forceFlush);
            return true;
        }

        public void Complete(int creditBytes, Exception? error)
        {
            ReleaseError ??= error;
            if (++Releases == _initialCount)
                Released.TrySetResult();
        }

        void IWriterReadySource.Stopped(Exception error)
        {
            ReleasesAtStop = Releases;
            Stops++;
            while (_frames.TryDequeue(out var packet))
                packet.Dispose();
            Stopped.TrySetResult();
        }
    }

    private sealed class Packet : IRpcByteBufferWriter
    {
        private readonly byte[] _bytes = new byte[24];
        private int _written = 24;
        internal int Returns;
        internal bool ThrowOnReturn;

        internal Packet(byte marker) => _bytes[0] = marker;

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
                throw new Exception("packet returned twice");
            if (ThrowOnReturn)
                throw new IOException("injected pool-return fault");
        }
    }

    private sealed class ControlledOutput : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly bool _holdFirst;
        private readonly TaskCompletionSource<FlushResult> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushes;
        internal readonly TaskCompletionSource FirstFlushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<byte> Markers = new();

        internal ControlledOutput(bool holdFirst) => _holdFirst = holdFirst;

        internal void ReleaseFirstFlush(Exception? error = null)
        {
            if (error is null)
                _first.TrySetResult(new FlushResult(false, false));
            else
                _first.TrySetException(error);
        }

        public override void Advance(int bytes)
        {
            _buffer.Advance(bytes);
            for (var offset = 0; offset < _buffer.WrittenCount; offset += 24)
                Markers.Enqueue(_buffer.WrittenSpan[offset]);
            _buffer.Clear();
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _flushes) == 1)
            {
                FirstFlushEntered.TrySetResult();
                if (_holdFirst)
                    return new ValueTask<FlushResult>(_first.Task);
            }
            return new ValueTask<FlushResult>(new FlushResult(false, false));
        }

        public override void CancelPendingFlush() => _first.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null)
        {
        }
    }

    private sealed class PumpFixture : IAsyncDisposable
    {
        private static readonly Type PumpType = typeof(RpcSession).GetNestedType("SendPump", BindingFlags.NonPublic)!;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly object _pump;
        internal ControlledOutput Output { get; }

        internal PumpFixture(
            bool holdFirstFlush = false,
            bool lowLatency = true,
            TimeProvider? clock = null,
            RpcSessionFlushOptions? flushOptions = null)
        {
            Output = new ControlledOutput(holdFirstFlush);
            var policy = RpcSessionFlushPolicyState.Create(flushOptions,
                lowLatency ? SharpLinkPerformanceProfile.LowLatency : SharpLinkPerformanceProfile.Throughput);
            _pump = Activator.CreateInstance(PumpType, Flags, binder: null,
                args: [Output, policy, 1024 * 1024, clock ?? TimeProvider.System, CancellationToken.None,
                    (Action<IRpcByteBufferWriter>)(packet => packet.Dispose()), (Action<Exception>)(_ => { })],
                culture: null)!;
        }

        private object? Call(string name, params object?[] args)
        {
            var method = PumpType.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
            try
            {
                return method.Invoke(_pump, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal void Attach(Source source) => Call("AttachWriterReadySource", source);
        internal void Stop() => Call("Stop");
        internal Task Join() => ((ValueTask)Call("WaitForStopAsync")!).AsTask();
        internal long QueuedBytes => (long)PumpType.GetProperty("QueuedBytes", Flags)!.GetValue(_pump)!;

        internal void EnqueueOrdinary(Packet packet)
        {
            var frame = new OwnedFrame(packet, false, null, false);
            var result = Call("TryEnqueue", frame);
            Ensure(result?.ToString() == "Accepted", "ordinary fixture frame was not accepted");
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            Output.ReleaseFirstFlush();
            try
            {
                await Join().WaitAsync(Limit);
            }
            catch (AggregateException)
            {
                // Fault tests assert settlement before fixture disposal.
            }
        }
    }
}
