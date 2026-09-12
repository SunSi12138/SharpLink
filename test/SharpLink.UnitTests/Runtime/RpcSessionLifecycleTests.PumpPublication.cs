using System.IO.Pipelines;
using System.Net;

namespace SharpLink.UnitTests.Runtime;

public partial class RpcSessionLifecycleTests
{
    [Test]
    public async Task DisposeShouldJoinPumpCreationBeforeDisposingSessionCancellation()
    {
        var input = new Pipe();
        var output = new Pipe();
        var inner = RpcSessionTestFixture.Transport("pump-publication", input.Reader, output.Writer);
        using var transport = new PumpPublicationTransport(inner);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var terminal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDisconnected += exception => terminal.TrySetResult(exception);
        var packet = new BlockingPacketWriter();
        packet.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, requestId: 1);
        transport.Arm();
        var send = StartSendAsync(session, packet, "sync");
        Task? dispose = null;
        try
        {
            // Output is read inside the pump publication lock, after the initial terminal check.
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispose = LongRunningTestWorker.RunAsync(() => session.DisposeAsync().AsTask());
            var published = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposedBeforePublication = false;
            try
            {
                await dispose.WaitAsync(TimeSpan.FromMilliseconds(100));
                disposedBeforePublication = true;
            }
            catch (TimeoutException) { }
            transport.Release();
            var failure = await send.WaitAsync(TimeSpan.FromSeconds(5));
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!disposedBeforePublication,
                "disposal must join a pump constructor that already owns publication");
            Ensure(ReferenceEquals(failure, published) && failure is SharpLinkException,
                "the late sender observes the authoritative terminal exception, never a disposed CTS");
            Ensure(packet.DisposeCount == 1 && session.QueuedSendBytes == 0 && inner.DisposeCount == 1,
                "the rejected packet, send pump, and transport complete ownership exactly once");
        }
        finally
        {
            transport.Release();
            await CleanupSendRaceAsync(transport.Release, send, session);
            if (dispose is not null)
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    private sealed class PumpPublicationTransport(ITransportConnection inner) : ITransportConnection, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output
        {
            get
            {
                if (Interlocked.Exchange(ref _armed, 0) != 0)
                {
                    Entered.TrySetResult();
                    if (!_release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Pump publication was not released by the test.");
                }
                return inner.Output;
            }
        }
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Dispose() => _release.Dispose();
    }
}
