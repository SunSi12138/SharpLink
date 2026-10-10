using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

// Benchmark-only observation: ReadAsync returns its original ValueTask without an
// async wrapper, task conversion or per-read allocation. The underlying read must
// already be incomplete and its current data pulse pending before the next send.
internal sealed class AllocationReadProbe
{
    private ObservedReader? _reader;

    internal bool HasPendingRead => Volatile.Read(ref _reader)?.HasPendingRead ?? false;

    internal IServerTransportListener Wrap(IServerTransportListener listener) => new ObservedListener(listener, this);

    private sealed class ObservedListener(IServerTransportListener inner, AllocationReadProbe probe)
        : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.AcceptAsync(cancellationToken).ConfigureAwait(false);
            var reader = new ObservedReader((SharedMemoryPipeReader)connection.Input);
            Volatile.Write(ref probe._reader, reader);
            return new ObservedConnection(connection, reader);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        void IPerformanceProfileAwareTransport.BindPerformanceProfile(SharpLinkPerformanceProfile profile)
            => ((IPerformanceProfileAwareTransport)inner).BindPerformanceProfile(profile);
    }

    private sealed class ObservedConnection(ITransportConnection inner, PipeReader reader) : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input => reader;
        public PipeWriter Output => inner.Output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ObservedReader(SharedMemoryPipeReader inner) : PipeReader
    {
        private readonly Lock _gate = new();
        private ValueTask<ReadResult> _lastRead;
        private bool _closed;

        internal bool HasPendingRead
        {
            get
            {
                lock (_gate)
                    return !_closed && !_lastRead.IsCompleted && inner.HasPendingDataWait;
            }
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var read = inner.ReadAsync(cancellationToken);
            lock (_gate)
                _lastRead = read;
            return read;
        }

        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null)
        {
            lock (_gate)
                _closed = true;
            inner.Complete(exception);
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            lock (_gate)
                _closed = true;
            return inner.CompleteAsync(exception);
        }
    }
}
