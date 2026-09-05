using System.IO.Pipelines;
using System.Net;
using System.Threading;

namespace SharpLink.UnitTests;

internal static class RpcSessionTestFixture
{
    internal static SharpLinkRuntimeContext RuntimeContext { get; } =
        new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);

    internal static RpcSessionCreationOptions ClientOptions(
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? flushOptions = null)
        => new(
            RpcSessionRole.Client,
            runtimeContext ?? RuntimeContext,
            flushOptions);

    internal static RpcSessionCreationOptions ServerOptions(
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? flushOptions = null)
        => new(
            RpcSessionRole.Server,
            runtimeContext ?? RuntimeContext,
            flushOptions);

    internal static RpcSessionTestTransport Transport(
        string id,
        PipeReader input,
        PipeWriter output,
        Func<ValueTask>? disposeAsync = null)
        => new(id, input, output, disposeAsync);

    internal static RpcSession CreateSessionOverTestTransport(
        string id,
        PipeReader input,
        PipeWriter output,
        RpcSessionCreationOptions creationOptions,
        bool completeHandshake = true)
    {
        var session = new RpcSession(Transport(id, input, output), creationOptions);
        if (completeHandshake)
            CompleteHandshake(session);
        return session;
    }

    internal static NegotiatedSessionOptions CompleteHandshake(
        RpcSession session,
        ProtocolV2Capabilities capabilities = ProtocolV2Capabilities.None,
        int? maxFramePayloadBytes = null,
        int? streamReceiveWindowBytes = null,
        int? connectionReceiveWindowBytes = null,
        SharpLinkCompressionProviderBinding? compressionBinding = null)
    {
        var context = session.RuntimeContext;
        var options = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            capabilities,
            maxFramePayloadBytes ?? context.Protocol.MaxFramePayloadBytes,
            streamReceiveWindowBytes ?? context.FlowControl.StreamReceiveWindowBytes,
            connectionReceiveWindowBytes ?? context.FlowControl.ConnectionReceiveWindowBytes,
            compressionBinding);
        if (!session.TryCompleteHandshake(options))
            throw new InvalidOperationException("The test Session handshake was already completed or terminated.");
        return session.NegotiatedOptions ??
            throw new InvalidOperationException("The completed test Session did not publish negotiated options.");
    }
}

/// <summary>A test transport that makes pipeline and disposal ownership explicit.</summary>
internal sealed class RpcSessionTestTransport(
    string id,
    PipeReader input,
    PipeWriter output,
    Func<ValueTask>? disposeAsync = null) : ITransportConnection
{
    private int _disposeCount;

    public string Id { get; } = id;

    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;

    public EndPoint? LocalEndPoint => null;

    public EndPoint? RemoteEndPoint => null;

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return disposeAsync is null
            ? CompletePipelinesAsync(Input, Output)
            : disposeAsync();
    }

    private static async ValueTask CompletePipelinesAsync(PipeReader input, PipeWriter output)
    {
        Exception? failure = null;
        try
        {
            await output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await input.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
