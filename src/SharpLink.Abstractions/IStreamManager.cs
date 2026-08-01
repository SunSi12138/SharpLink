namespace SharpLink.Abstractions;

/// <summary>Routes stream frames to dispatchers scoped by request and stream identifiers.</summary>
public interface IStreamManager
{
    /// <summary>Registers the default stream dispatcher for a request.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="dispatcher">The stream consumer.</param>
    void Register(long requestId, IStreamDispatcher dispatcher);
    /// <summary>Registers one explicitly numbered stream dispatcher for a request.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="streamId">The request-local stream identifier.</param>
    /// <param name="dispatcher">The stream consumer.</param>
    void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher);
    /// <summary>Removes the default stream dispatcher for a request.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    void Unregister(long requestId);
    /// <summary>Removes an explicitly numbered stream dispatcher.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="streamId">The request-local stream identifier.</param>
    void Unregister(long requestId, ushort streamId);
    /// <summary>Dispatches a chunk to the default stream for a request.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="payload">The encoded item payload.</param>
    ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload);
    /// <summary>Dispatches a chunk to an explicitly numbered request stream.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="streamId">The request-local stream identifier.</param>
    /// <param name="payload">The encoded item payload.</param>
    ValueTask DispatchChunkAsync(long requestId, ushort streamId, ReadOnlySequence<byte> payload);
    /// <summary>Completes the default stream from a peer completion frame.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="isError">Whether the peer reported an error.</param>
    /// <param name="msg">The peer's error message, when present.</param>
    void CompleteStream(long requestId, bool isError, string? msg);
    /// <summary>Completes an explicitly numbered stream from a peer completion frame.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="streamId">The request-local stream identifier.</param>
    /// <param name="isError">Whether the peer reported an error.</param>
    /// <param name="msg">The peer's error message, when present.</param>
    void CompleteStream(long requestId, ushort streamId, bool isError, string? msg);
    /// <summary>Completes every registered stream from a peer completion state.</summary>
    /// <param name="isError">Whether completion represents an error.</param>
    /// <param name="msg">The peer's error message, when present.</param>
    void CompleteAll(bool isError, string? msg);
    /// <summary>Completes the default stream because local processing terminated.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="exception">The terminal exception, or <see langword="null"/> for success.</param>
    void CompleteStream(long requestId, Exception? exception);
    /// <summary>Completes an explicitly numbered stream because local processing terminated.</summary>
    /// <param name="requestId">The owning request identifier.</param>
    /// <param name="streamId">The request-local stream identifier.</param>
    /// <param name="exception">The terminal exception, or <see langword="null"/> for success.</param>
    void CompleteStream(long requestId, ushort streamId, Exception? exception);
    /// <summary>Completes every registered stream because local processing terminated.</summary>
    /// <param name="exception">The terminal exception, or <see langword="null"/> for success.</param>
    void CompleteAll(Exception? exception);
}
