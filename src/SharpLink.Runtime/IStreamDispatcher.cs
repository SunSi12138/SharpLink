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


internal delegate void ResolvedStreamBytesCallback(
    in StreamFlowController.ResolvedReceiveCreditLease lease,
    int encodedByteCount);

/// <summary>
/// Optional runtime-only capability that carries a generation-bound receive-credit lease through
/// the existing dispatcher lifecycle without changing stream routing or consumer timing.
/// </summary>
internal interface IResolvedStreamConsumptionAwareDispatcher : IStreamConsumptionAwareDispatcher
{
    void SetResolvedBytesConsumedCallback(
        ResolvedStreamBytesCallback? callback,
        in StreamFlowController.ResolvedReceiveCreditLease lease);
}
