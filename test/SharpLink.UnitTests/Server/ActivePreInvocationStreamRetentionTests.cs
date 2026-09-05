using System.Buffers;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Server;

public class ActivePreInvocationStreamRetentionTests
{
    [Test]
    public async Task StableMailboxBudgetShouldCompleteRouteWithResourceExhausted()
    {
        const long requestId = 211;
        const ushort streamId = 1;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers,
            static _ => true,
            static _ => { },
            () => manager.CompleteStream(
                requestId,
                streamId,
                new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "stable deferred byte cap")),
            maxRetainedBytes: 8);
        manager.Register(requestId, streamId, mailbox);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[6]));
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(6);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[3]));
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(6);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(1);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(
            dispatcher.LastException is SharpLinkException
            {
                Code: SharpLinkErrorCode.ResourceExhausted
            }).IsTrue();
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(0);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    [Test]
    public async Task NoFlowControlReconfigurationShouldCountExistingBytesAgainstStableActiveBudget()
    {
        const long requestId = 223;
        const ushort streamId = 1;
        var queuedBytes = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers,
            bytes =>
            {
                if (queuedBytes + bytes > 64)
                    return false;
                queuedBytes += bytes;
                return true;
            },
            bytes => queuedBytes -= bytes,
            static () => { });
        manager.Register(requestId, streamId, mailbox);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[6]));
        await Assert.That(queuedBytes).IsEqualTo(6);
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(6);

        manager.Register(
            requestId,
            streamId,
            new PreAdmissionStreamDispatcher(
                buffers,
                static _ => true,
                static _ => { },
                () => manager.CompleteStream(
                    requestId,
                    streamId,
                    new SharpLinkException(
                        SharpLinkErrorCode.ResourceExhausted,
                        "stable active deferred byte cap")),
                maxRetainedBytes: 8));

        // The physical mailbox owner is unchanged. Existing admission bytes remain charged to
        // their original global owner until replay, but they already count against the active
        // mailbox's stable eight-byte cap.
        await Assert.That(queuedBytes).IsEqualTo(6);
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(6);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[2]));
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(8);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[1]));
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(8);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(2);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(
            dispatcher.LastException is SharpLinkException
            {
                Code: SharpLinkErrorCode.ResourceExhausted
            }).IsTrue();
        await Assert.That(queuedBytes).IsEqualTo(0);
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(0);
        await Assert.That(manager.ActiveStreamCount).IsEqualTo(0);
    }

    [Test]
    public async Task NoFlowControlReconfigurationShouldRejectExistingBytesAboveActiveBudget()
    {
        const long requestId = 227;
        const ushort streamId = 1;
        var queuedBytes = 0;
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers,
            bytes =>
            {
                if (queuedBytes + bytes > 64)
                    return false;
                queuedBytes += bytes;
                return true;
            },
            bytes => queuedBytes -= bytes,
            static () => { });
        manager.Register(requestId, streamId, mailbox);

        await manager.DispatchChunkAsync(
            requestId,
            streamId,
            new ReadOnlySequence<byte>(new byte[9]));
        await Assert.That(queuedBytes).IsEqualTo(9);
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(9);

        manager.Register(
            requestId,
            streamId,
            new PreAdmissionStreamDispatcher(
                buffers,
                static _ => true,
                static _ => { },
                static () => { },
                maxRetainedBytes: 8));

        // No replacement policy re-reservation occurs. The stable mailbox can compare its own
        // retained-byte count with the new active limit and release the old owner exactly once.
        await Assert.That(queuedBytes).IsEqualTo(0);
        await Assert.That(mailbox.RetainedBytesForTests).IsEqualTo(0);

        var dispatcher = new RecordingDispatcher();
        manager.Register(requestId, streamId, dispatcher);

        await Assert.That(dispatcher.DispatchCount).IsEqualTo(0);
        await Assert.That(dispatcher.CompleteCount).IsEqualTo(1);
        await Assert.That(
            dispatcher.LastException is SharpLinkException
            {
                Code: SharpLinkErrorCode.ResourceExhausted
            }).IsTrue();
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
