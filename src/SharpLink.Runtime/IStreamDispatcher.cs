namespace SharpLink.Runtime;

/// <summary>
/// Routes one encoded stream item to the Runtime-owned receive-stream dispatcher.
/// This is an engine boundary shared only with Runtime friend assemblies.
/// </summary>
internal interface IStreamDispatcher
{
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload);

    void Complete(bool isError, string? errorMessage);

    void Complete(Exception? exception);
}

/// <summary>
/// Runtime dispatcher capability that returns flow-control credit only after a consumer takes an item.
/// </summary>
internal interface IStreamConsumptionAwareDispatcher : IStreamDispatcher
{
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount);

    void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId);
}
