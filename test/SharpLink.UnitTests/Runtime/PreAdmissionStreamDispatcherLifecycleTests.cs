using System.Buffers;

namespace SharpLink.UnitTests.Runtime;

public class PreAdmissionStreamDispatcherLifecycleTests
{
    [Test]
    public async Task MissingCompressedDecoderShouldReleaseAcquiredChildBeforeThrowing()
    {
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers,
            static _ => true,
            static _ => { },
            static () => { });
        var dispatcher = new LeaseCapturingDispatcher();

        await Assert.That(mailbox.TryBeginAttach(dispatcher, out var alreadyCompleted)).IsTrue();
        mailbox.FinishAttach(dispatcher);
        await Assert.That(alreadyCompleted).IsFalse();

        var threw = false;
        try
        {
            await mailbox.DispatchCompressedAsync(
                new ReadOnlySequence<byte>(new byte[] { 1 }),
                originalByteCount: 1);
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "The inbound stream mailbox has no compressed-frame decoder.")
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();

        var state = (IStreamDispatchState)mailbox;
        await Assert.That(state.HasActiveDispatches).IsFalse();
        await state.WaitForDispatchesDrainedAsync();

        var detached = state.WaitForDetachedAsync(CancellationToken.None);
        await Assert.That(detached.IsCompletedSuccessfully).IsFalse();

        mailbox.Abandon(out _);
        await detached;
        await Assert.That(state.IsDetached).IsTrue();
    }

    private sealed class LeaseCapturingDispatcher : IStreamDispatcher, IStreamDispatchLease
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception)
            => _ = exception;

        void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
            => ArgumentNullException.ThrowIfNull(state);

        ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
            ReadOnlySequence<byte> payload,
            int encodedByteCount)
        {
            _ = encodedByteCount;
            return DispatchAsync(payload);
        }

        void IStreamDispatchLease.OnDispatchesDrained()
        {
        }
    }
}
