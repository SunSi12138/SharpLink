namespace SharpLink.Abstractions;

/// <summary>Dispatches encoded stream chunks to one typed stream consumer.</summary>
public interface IStreamDispatcher
{
    /// <summary>Decodes and queues one stream payload.</summary>
    /// <param name="payload">The complete encoded item payload.</param>
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload);
    /// <summary>Completes the consumer from a peer stream-completion frame.</summary>
    /// <param name="isError">Whether the peer reported an error.</param>
    /// <param name="errorMessage">The peer's error message, when present.</param>
    void Complete(bool isError, string? errorMessage);
    /// <summary>Completes the consumer because local processing terminated.</summary>
    /// <param name="exception">The terminal exception, or <see langword="null"/> for successful completion.</param>
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
