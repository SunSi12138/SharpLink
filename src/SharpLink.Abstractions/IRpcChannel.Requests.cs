namespace SharpLink.Abstractions;

public partial interface IRpcChannel
{
    ValueTask<T> InvokeAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter);
    ValueTask<T> InvokeNoPayloadAsync<T>(long interfaceHash, long methodHash);

    ValueTask<T> InvokeNoReturnAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter);
    ValueTask<T> InvokeNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash);

    ValueTask<T> InvokeCancellableAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);

    ValueTask<T> InvokeCancellableNoReturnAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoReturnWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoReturnWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoReturnWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeCancellableNoReturnWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);

    ValueTask InvokeOneWayAsync(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter);
    ValueTask InvokeOneWayNoPayloadAsync(long interfaceHash, long methodHash);

    ValueTask InvokeCancellableOneWayAsync(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<ArrayBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask InvokeCancellableOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default);
}