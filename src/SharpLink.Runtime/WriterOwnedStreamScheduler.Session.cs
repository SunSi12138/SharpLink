namespace SharpLink.Runtime;

internal sealed partial class WriterOwnedStreamScheduler
{
    // Preparation cleanup is independent of a writer blocked in transport FlushAsync.
    // The existing writer still owns all admitted frames and all credit mutations.
    internal void RequestStop(Exception error) => StopPreparing(error);

    internal void AbortStream(long requestId, ushort streamId, Exception error)
    {
        StreamHandle? stream;
        lock (_mapGate) _streams.TryGetValue(new StreamKey(requestId, streamId), out stream);
        if (stream is not null) Abort(stream, error);
    }
}
