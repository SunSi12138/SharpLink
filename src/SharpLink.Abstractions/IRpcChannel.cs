namespace SharpLink.Abstractions;

/// <summary>
/// 客户端通道抽象
/// </summary>
public interface IRpcChannel
{
    ValueTask<T> InvokeAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    ValueTask<T> InvokeNoPayloadAsync<T>(long interfaceHash, long methodHash);
    ValueTask<T> InvokeWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    ValueTask<T> InvokeWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash);
    ValueTask<T> InvokeWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout);
    ValueTask<T> InvokeWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout);

    ValueTask<T> InvokeCancellableAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);

    ValueTask InvokeOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    ValueTask InvokeOneWayNoPayloadAsync(long interfaceHash, long methodHash);
    ValueTask InvokeOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    ValueTask InvokeOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash);
    ValueTask InvokeOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout);
    ValueTask InvokeOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout);
    ValueTask InvokeOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    ValueTask InvokeOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    ValueTask InvokeOneWayClientStreamWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    ValueTask InvokeOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    ValueTask InvokeOneWayClientStreamWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);
    ValueTask InvokeOneWayClientStreamWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);

    ValueTask InvokeCancellableOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayClientStreamWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);

    ValueTask<T> InvokeClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    ValueTask<T> InvokeClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    ValueTask<T> InvokeClientStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    ValueTask<T> InvokeClientStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    ValueTask<T> InvokeClientStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);
    ValueTask<T> InvokeClientStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);
    ValueTask<T> InvokeCancellableClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableClientStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableClientStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> InvokeServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    IAsyncEnumerable<T> InvokeServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash);
    IAsyncEnumerable<T> InvokeServerStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter);
    IAsyncEnumerable<T> InvokeServerStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash);
    IAsyncEnumerable<T> InvokeServerStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout);
    IAsyncEnumerable<T> InvokeServerStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout);
    IAsyncEnumerable<T> InvokeCancellableServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableServerStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableServerStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableServerStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableServerStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> InvokeDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    IAsyncEnumerable<T> InvokeDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    IAsyncEnumerable<T> InvokeDuplexStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender);
    IAsyncEnumerable<T> InvokeDuplexStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender);
    IAsyncEnumerable<T> InvokeDuplexStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);
    IAsyncEnumerable<T> InvokeDuplexStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default);

    Task SendClientStreamAsync<T>(long requestId, sbyte streamId, IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default);
}
