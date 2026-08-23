namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Queued two-way admission keeps its copied compressed frame and retained-byte permit owned by
    /// the outer admission payload until this method returns from the synchronous dispatch prefix.
    /// Inline B therefore acquires decode concurrency with zero transferred retained bytes; the
    /// admission wrapper returns the physical copy and releases its retained budget immediately
    /// after this call returns. Persistent D will instead use the explicit retained-to-decode
    /// transfer primitive when ownership crosses into a decode worker.
    /// </summary>
    private ValueTask DispatchRpcAsync(
        ServerConnectionState connection,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        CancellationToken serverLoopToken,
        ServerCallCancellationState? admittedCallState,
        bool admissionGranted,
        ServerRetainedCompressedPermit? retainedCompressedPermit)
    {
        _ = retainedCompressedPermit;
        return DispatchRpcAsync(
            connection,
            requestId,
            flags,
            payload,
            requestCancellationMap,
            serverLoopToken,
            admittedCallState,
            admissionGranted,
            retainedAdmissionPayload: null);
    }
}
