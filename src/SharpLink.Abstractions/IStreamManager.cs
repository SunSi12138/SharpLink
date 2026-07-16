namespace SharpLink.Abstractions;

public interface IStreamManager
{
    void Register(long requestId, IStreamDispatcher dispatcher);
    void Register(long requestId, ushort streamId, IStreamDispatcher dispatcher);
    void Unregister(long requestId);
    void Unregister(long requestId, ushort streamId);
    ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload);
    ValueTask DispatchChunkAsync(long requestId, ushort streamId, ReadOnlySequence<byte> payload);
    void CompleteStream(long requestId, bool isError, string? msg);
    void CompleteStream(long requestId, ushort streamId, bool isError, string? msg);
    void CompleteAll(bool isError, string? msg);
    void CompleteStream(long requestId, Exception? exception);
    void CompleteStream(long requestId, ushort streamId, Exception? exception);
    void CompleteAll(Exception? exception);
}
