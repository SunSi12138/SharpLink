namespace SharpLink.UnitTests.Runtime;

public class PeerTerminalLateDataTests
{
    [Test]
    public async Task PeerTerminalShouldRejectLateDataBeforeReceiveCreditIsAccepted()
    {
        var acceptedBytes = 0;
        var terminalPublications = 0;
        var manager = new StreamManager(
            new RuntimeConcurrencyOptions(),
            (_, _, bytes) => acceptedBytes += bytes,
            bytesConsumed: null,
            (_, _) => terminalPublications++);
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        const long requestId = 81_000;
        const ushort streamId = 1;

        manager.ReservePreAdmissionStreams(
            requestId,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => { },
            retainUntilLocalCompletion: true);

        manager.CompletePeerStream(requestId, streamId, exception: null);
        Ensure(terminalPublications == 1,
            "peer terminal should publish receive completion exactly once");
        Ensure(manager.ActiveStreamCount == 1,
            "peer-first retained route should remain until local completion");

        var normalFailure = await CaptureSharpLinkFailureAsync(
            () => manager.DispatchChunkAsync(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[32])));
        Ensure(normalFailure?.Code == SharpLinkErrorCode.ProtocolViolation,
            "normal StreamData after peer terminal should be a protocol violation");
        Ensure(acceptedBytes == 0,
            "normal late data must be rejected before receive credit is accepted");

        SharpLinkException? compressedFailure = null;
        try
        {
            _ = manager.TryDispatchPreAdmissionCompressed(
                requestId,
                streamId,
                new ReadOnlySequence<byte>(new byte[16]),
                originalByteCount: 64,
                out _);
        }
        catch (SharpLinkException exception)
        {
            compressedFailure = exception;
        }

        Ensure(compressedFailure?.Code == SharpLinkErrorCode.ProtocolViolation,
            "compressed StreamData after peer terminal should be a protocol violation");
        Ensure(acceptedBytes == 0,
            "compressed late data must be rejected before receive credit is accepted");
        Ensure(terminalPublications == 1,
            "late data must not reopen or republish receive terminal state");

        manager.AbandonExistingRequestStreams(requestId, 1);
        Ensure(manager.ActiveStreamCount == 0,
            "local completion after peer terminal should retire the retained route");
        manager.AssertAccountingInvariant();
    }

    private static async Task<SharpLinkException?> CaptureSharpLinkFailureAsync(
        Func<ValueTask> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
