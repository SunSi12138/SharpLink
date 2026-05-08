namespace SharpLink.Abstractions;

public interface IStreamManager
{
    void Register(long requestId, IStreamDispatcher dispatcher);
    void Register(long requestId, sbyte streamId, IStreamDispatcher dispatcher);
    void Unregister(long requestId);
    void Unregister(long requestId, sbyte streamId);
    ValueTask DispatchChunkAsync(long requestId, ReadOnlySequence<byte> payload);
    ValueTask DispatchChunkAsync(long requestId, sbyte streamId, ReadOnlySequence<byte> payload);
    void CompleteStream(long requestId, bool isError, string? msg);
    void CompleteStream(long requestId, sbyte streamId, bool isError, string? msg);
    void CompleteAll(bool isError, string? msg);
    void CompleteStream(long requestId, Exception? exception);
    void CompleteStream(long requestId, sbyte streamId, Exception? exception);
    void CompleteAll(Exception? exception);
}
