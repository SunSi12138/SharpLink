using System.Buffers;
using System.Net;

namespace SharpLink.Abstractions;

/// <summary>Describes the current outcome of an intercepted call.</summary>
public enum SharpLinkInvocationStatus : byte
{
    /// <summary>The call has not completed.</summary>
    Pending,
    /// <summary>The call completed successfully.</summary>
    Succeeded,
    /// <summary>The call failed.</summary>
    Failed,
    /// <summary>The call was cancelled locally or remotely.</summary>
    Cancelled
}

/// <summary>Contains mutable client-side control data and immutable method data for one intercepted call.</summary>
public sealed class SharpLinkClientInvocationContext
{
    internal SharpLinkClientInvocationContext(
        RpcMethodDescriptor method,
        object? request,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        Method = method;
        Request = request;
        Metadata = metadata;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets generated method metadata.</summary>
    public RpcMethodDescriptor Method { get; }
    /// <summary>Gets the generated request value. Value-type requests are boxed only when interceptors are enabled.</summary>
    public object? Request { get; }
    /// <summary>Gets or replaces call controls before the terminal invoker runs.</summary>
    public SharpLinkMetadata? Metadata { get; set; }
    /// <summary>Gets the caller cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>Gets the current completion status.</summary>
    public SharpLinkInvocationStatus Status { get; internal set; }
    /// <summary>Gets the structured error code after a failed call.</summary>
    public SharpLinkErrorCode? ErrorCode { get; internal set; }
    /// <summary>Gets the failure after a failed call.</summary>
    public Exception? Exception { get; internal set; }
    /// <summary>Gets elapsed time after the pipeline completes.</summary>
    public TimeSpan Elapsed { get; internal set; }

    internal object? InterceptorPipelineState { get; set; }
}

/// <summary>Represents the boxed terminal result used only by an enabled client interceptor pipeline.</summary>
/// <param name="Value">A unary response, stream, or <see langword="null"/> for a one-way call.</param>
public readonly record struct SharpLinkClientInvocationResult(object? Value)
{
    /// <summary>Returns the result as the requested response type.</summary>
    public T GetValue<T>()
    {
        if (Value is T value)
            return value;
        if (Value is null && default(T) is null)
            return default!;
        throw new InvalidCastException($"The intercepted result is not {typeof(T).FullName}.");
    }
}

/// <summary>
/// Continues a client interceptor pipeline. If used, invoke this continuation at most once and await or directly return
/// the resulting <see cref="ValueTask{TResult}"/>. Do not retain the continuation or invocation context after
/// <see cref="ISharpLinkClientInterceptor.InvokeAsync"/> returns. Violating these rules is an interceptor bug and is not
/// dynamically enforced by SharpLink.
/// </summary>
public delegate ValueTask<SharpLinkClientInvocationResult> SharpLinkClientInvocationDelegate(
    SharpLinkClientInvocationContext context);

/// <summary>Intercepts a generated client call and may mutate metadata, short-circuit, or observe the result.</summary>
public interface ISharpLinkClientInterceptor
{
    /// <summary>Invokes this interceptor.</summary>
    ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
        SharpLinkClientInvocationContext context,
        SharpLinkClientInvocationDelegate next);
}

/// <summary>Contains server-side call identity, peer data, authentication, and completion state.</summary>
public sealed class SharpLinkServerInvocationContext : SharpLinkCallContextSnapshot
{
    internal SharpLinkServerInvocationContext(
        RpcMethodDescriptor method,
        long requestId,
        string connectionId,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        SharpLinkAuthenticationContext? authentication,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken,
        object? interceptorGeneration = null)
        : base(connectionId, authentication, deadline, deadlineTimeProvider, metadata)
    {
        InterceptorGeneration = interceptorGeneration;
        Method = method;
        RequestId = requestId;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        CancellationToken = cancellationToken;
    }

    internal object? InterceptorGeneration { get; }
    internal bool InterceptorTerminalReached { get; set; }
    internal object? InterceptorStub { get; set; }
    internal object? InterceptorService { get; set; }
    internal object? InterceptorGeneratedBridge { get; set; }
    internal long InterceptorMethodId { get; set; }
    internal ReadOnlySequence<byte> InterceptorArguments { get; set; }
    internal object? InterceptorOutput { get; set; }
    internal TimeProvider? InterceptorTimeProvider { get; set; }
    internal long InterceptorStarted { get; set; }

    /// <summary>Gets generated method metadata.</summary>
    public RpcMethodDescriptor Method { get; }
    /// <summary>Gets the request identifier.</summary>
    public long RequestId { get; }
    /// <summary>Gets the transport connection identifier.</summary>
    public string ConnectionId => SessionId;
    /// <summary>Gets the local transport endpoint when available.</summary>
    public EndPoint? LocalEndPoint { get; }
    /// <summary>Gets the remote transport endpoint when available.</summary>
    public EndPoint? RemoteEndPoint { get; }
    /// <summary>Gets the server call cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>Gets the current completion status.</summary>
    public SharpLinkInvocationStatus Status { get; internal set; }
    /// <summary>Gets the structured error code after a failed call.</summary>
    public SharpLinkErrorCode? ErrorCode { get; internal set; }
    /// <summary>Gets the failure after a failed call.</summary>
    public Exception? Exception { get; internal set; }
    /// <summary>Gets elapsed time after the pipeline completes.</summary>
    public TimeSpan Elapsed { get; internal set; }
}

/// <summary>
/// Continues a server interceptor pipeline. If used, invoke this continuation at most once and await or directly return
/// the resulting <see cref="ValueTask"/>. Do not retain the continuation or invocation context after
/// <see cref="ISharpLinkServerInterceptor.InvokeAsync"/> returns. Violating these rules is an interceptor bug and is not
/// dynamically enforced by SharpLink.
/// </summary>
public delegate ValueTask SharpLinkServerInvocationDelegate(SharpLinkServerInvocationContext context);

/// <summary>Intercepts a server call for authorization, limiting, auditing, or exception policy.</summary>
public interface ISharpLinkServerInterceptor
{
    /// <summary>
    /// Invokes this interceptor. Response-bearing calls must invoke <paramref name="next"/>;
    /// throw a <see cref="SharpLinkException"/> to reject a call. One-way calls may return directly.
    /// </summary>
    ValueTask InvokeAsync(
        SharpLinkServerInvocationContext context,
        SharpLinkServerInvocationDelegate next);
}

/// <summary>Maps service exceptions to status and messages safe to return over the wire.</summary>
public interface IRpcExceptionMapper
{
    /// <summary>Maps one service exception for one server invocation.</summary>
    SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context);
}
