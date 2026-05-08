namespace SharpLink.Abstractions;

// 4. 流分发器抽象 (用于 StreamManager)
// 允许网络层将二进制数据推送到具体类型的 Channel 中
public interface IStreamDispatcher
{
    ValueTask DispatchAsync(ReadOnlySequence<byte> payload);
    void Complete(bool isError, string? errorMessage);
    void Complete(Exception? exception);
}
