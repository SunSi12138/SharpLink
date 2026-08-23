using System.Buffers;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ActivePreInvocationStreamRetentionTests
{
    [Test]
    public async Task BudgetShouldBoundRetainedBytesAndRecoverAfterRelease()
    {
        var retention = new ActivePreInvocationStreamRetention(8);

        await Assert.That(retention.TryReserve(6)).IsTrue();
        await Assert.That(retention.RetainedBytes).IsEqualTo(6);
        await Assert.That(retention.TryReserve(3)).IsFalse();
        await Assert.That(retention.RetainedBytes).IsEqualTo(6);

        retention.Release(4);

        await Assert.That(retention.TryReserve(3)).IsTrue();
        await Assert.That(retention.RetainedBytes).IsEqualTo(5);
        retention.Release(5);
        await Assert.That(retention.RetainedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task DeferredByteCapShouldCompleteRouteWithResourceExhausted()
    {
        const long requestId = 211;
        const ushort streamId = 1;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var retention = new ActivePreInvocationStreamRetention(8);
        manager.Register(
            requestId,
            streamId,
            new PreAdmissionStreamDispatcher(
                buffers,
                retention.TryReserve,
                retention.Release,
                () => manager.CompleteStream(
                    requestId,
                    streamId,
                    new SharpLinkException(
                        SharpLinkErrorCode.ResourceExhausted,
                        "deferred byte cap"))));

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[6]));
        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[3]));

        await Assert.That(retention.RetainedBytes).IsEqualTo(6);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(1);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(
            dispatcher.LastException is SharpLinkException
            {
                Code: SharpLinkErrorCode.ResourceExhausted
            }).IsTrue();
        await Assert.That(retention.RetainedBytes).IsEqualTo(0);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    private sealed class RecordingDispatcher : IStreamDispatcher
    {
        public int DispatchCount { get; private set; }
        public int CompleteCount { get; private set; }
        public Exception? LastException { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            DispatchCount++;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            CompleteCount++;
            LastException = isError
                ? new SharpLinkException(
                    SharpLinkErrorCode.RemoteError,
                    errorMessage ?? "Remote Error")
                : null;
        }

        public void Complete(Exception? exception)
        {
            CompleteCount++;
            LastException = exception;
        }
    }
}
