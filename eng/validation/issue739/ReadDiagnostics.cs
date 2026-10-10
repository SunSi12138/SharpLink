using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace Issue739;

// Separate diagnostic run only. Return the original ValueTask, never add an await wrapper.
internal sealed class ReadDiagnostics
{
    private long _sync, _incomplete;
    internal long Sync => Interlocked.Read(ref _sync);
    internal long Incomplete => Interlocked.Read(ref _incomplete);
    internal IServerTransportListener Wrap(IServerTransportListener inner) => new Listener(inner, this);
    internal IClientTransportFactory Wrap(IClientTransportFactory inner) => new Factory(inner, this);
    private sealed class Reader(PipeReader inner, ReadDiagnostics owner) : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var read = inner.ReadAsync(cancellationToken);
            if (read.IsCompleted) Interlocked.Increment(ref owner._sync); else Interlocked.Increment(ref owner._incomplete);
            return read;
        }
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
    private sealed class Connection(ITransportConnection inner, ReadDiagnostics owner) : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input { get; } = new Reader(inner.Input, owner);
        public PipeWriter Output => inner.Output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Listener(IServerTransportListener inner, ReadDiagnostics owner) : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default) => new Connection(await inner.AcceptAsync(cancellationToken).ConfigureAwait(false), owner);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile) { if (inner is IPerformanceProfileAwareTransport aware) aware.BindPerformanceProfile(profile); }
    }
    private sealed class Factory(IClientTransportFactory inner, ReadDiagnostics owner) : IClientTransportFactory, IPerformanceProfileAwareTransport
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default) => new Connection(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), owner);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile) { if (inner is IPerformanceProfileAwareTransport aware) aware.BindPerformanceProfile(profile); }
    }
}
