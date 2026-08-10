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
        RpcSessionFlushOptions? flushOptions = null,
        RpcSessionServiceExceptionMapper? serviceExceptionMapper = null)
        => new(
            RpcSessionRole.Server,
            runtimeContext ?? RuntimeContext,
            flushOptions,
            serviceExceptionMapper);

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
        RpcSessionCreationOptions creationOptions)
        => new(Transport(id, input, output), creationOptions);
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
