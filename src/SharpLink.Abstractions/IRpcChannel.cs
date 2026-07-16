namespace SharpLink.Abstractions;

/// <summary>
/// 客户端通道抽象
/// </summary>
public partial interface IRpcChannel
{
    /// <summary>Gets the runtime context owned by this channel.</summary>
    IRpcRuntimeContext RuntimeContext { get; }

    /// <summary>Invokes a unary, one-way, or client-streaming RPC with per-call controls.</summary>
    /// <typeparam name="T">The response payload type.</typeparam>
    /// <param name="interfaceHash">The generated contract identifier.</param>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <param name="payloadWriter">The generated business-payload writer, when a payload is present.</param>
    /// <param name="streamSender">The generated client-stream sender, when streams are present.</param>
    /// <param name="isOneWay">Whether the invocation expects no server response.</param>
    /// <param name="hasReturnPayload">Whether a successful response contains a business payload.</param>
    /// <param name="options">Per-call deadline, metadata, readiness, and compression controls.</param>
    /// <param name="hasMethodTimeout">Whether the generated method declares a timeout.</param>
    /// <param name="methodTimeout">The generated method timeout, when present.</param>
    /// <param name="cancellationToken">Cancels the local invocation.</param>
    /// <returns>The response payload, or the default value for response-less calls.</returns>
    ValueTask<T> InvokeWithCallOptionsAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<ArrayBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        bool hasReturnPayload,
        SharpLinkCallOptions options,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a server-streaming or duplex-streaming RPC with per-call controls.</summary>
    /// <typeparam name="T">The response stream item type.</typeparam>
    /// <param name="interfaceHash">The generated contract identifier.</param>
    /// <param name="methodHash">The generated method identifier.</param>
    /// <param name="payloadWriter">The generated business-payload writer, when a payload is present.</param>
    /// <param name="streamSender">The generated client-stream sender, when streams are present.</param>
    /// <param name="options">Per-call deadline, metadata, readiness, and compression controls.</param>
    /// <param name="hasMethodTimeout">Whether the generated method declares a timeout.</param>
    /// <param name="methodTimeout">The generated method timeout, when present.</param>
    /// <param name="cancellationToken">Cancels the local invocation and stream enumeration.</param>
    /// <returns>The asynchronous response stream.</returns>
    IAsyncEnumerable<T> InvokeStreamingWithCallOptionsAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<ArrayBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        SharpLinkCallOptions options,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout,
        CancellationToken cancellationToken = default);
}
