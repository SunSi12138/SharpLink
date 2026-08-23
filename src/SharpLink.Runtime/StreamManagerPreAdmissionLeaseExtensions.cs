namespace SharpLink.Runtime;

/// <summary>
/// Registers pre-admission stream dispatchers whose retained buffers own disposable byte leases.
/// The compatibility callback overload on <see cref="StreamManager"/> remains available for
/// existing Runtime callers, while server resource ownership can flow through without rebuilding
/// a second accounting lifetime.
/// </summary>
internal static class StreamManagerPreAdmissionLeaseExtensions
{
    internal static void ReservePreAdmissionStreams(
        this StreamManager manager,
        long requestId,
        int streamCount,
        SharpLinkBufferWriterPool buffers,
        Func<int, IDisposable?> reserveBytes,
        Action capacityExceeded,
        Func<ReadOnlySequence<byte>, PreAdmissionDecodedPayload>? decodeCompressed = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(reserveBytes);
        ArgumentNullException.ThrowIfNull(capacityExceeded);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamCount);

        for (var index = 1; index <= streamCount; index++)
        {
            manager.Register(
                requestId,
                checked((ushort)index),
                new PreAdmissionStreamDispatcher(
                    buffers,
                    reserveBytes,
                    capacityExceeded,
                    decodeCompressed));
        }
    }
}
