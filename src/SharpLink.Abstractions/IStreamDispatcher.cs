namespace SharpLink.Abstractions;

// 4. 流分发器抽象 (用于 StreamManager)
// 允许网络层将二进制数据推送到具体类型的 Channel 中
public interface IStreamDispatcher
{
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload);
    void Complete(bool isError, string? errorMessage);
    void Complete(Exception? exception);
}

/// <summary>
/// Optional dispatcher capability that accounts for encoded bytes only after the consumer takes an item.
/// </summary>
public interface IStreamConsumptionAwareDispatcher : IStreamDispatcher
{
    /// <summary>Dispatches one item together with the byte credit charged on the wire.</summary>
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount);

    /// <summary>Registers the callback used to return consumed byte credit.</summary>
    void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId);
}
