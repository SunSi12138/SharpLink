using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    private static readonly MethodInfo CreateCallContextMethod = typeof(SharpLinkServer).GetMethod(
        "CreateCallContext", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(SharpLinkServer), "CreateCallContext");

    [Test]
    public async Task InterceptedCallContextShouldResolveDescriptorExactlyOnce()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .AddInterceptor(new CallContextTestInterceptor())
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "descriptor-with-interceptor",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");

        try
        {
            var descriptor = new RpcMethodDescriptor(
                ContractId: 81,
                MethodId: 17,
                Kind: RpcMethodKind.Unary,
                HasResponsePayload: true,
                HasClientStreams: false,
                HasMethodTimeout: true,
                MethodTimeout: TimeSpan.FromSeconds(3),
                IsIdempotent: true,
                ResponseNullable: true);
            var stub = new CallContextCountingStub(descriptor, found: true);
            var context = CreateInterceptedContext(server, connection, stub, requestId: 42);

            Ensure(stub.LookupCount == 1,
                "interceptor context must reuse the descriptor resolved for stream reservation");
            Ensure(context.Method == descriptor,
                "interceptor context must expose the exact previously resolved descriptor");
            Ensure(context.RequestId == 42 && context.ConnectionId == session.Id,
                "interceptor context must preserve request and connection identity");
            Ensure(context.InterceptorGeneration is not null,
                "interceptor context must retain its captured generation");

            // An unresolved custom stub still produces the established conservative
            // Unary descriptor; the refactor must not turn that into a second lookup.
            var missing = new CallContextCountingStub(descriptor, found: false);
            var fallback = CreateInterceptedContext(server, connection, missing, requestId: 43);
            Ensure(missing.LookupCount == 1,
                "missing-descriptor fallback must query the stub exactly once");
            Ensure(fallback.Method.Kind == RpcMethodKind.Unary &&
                fallback.Method.ContractId == missing.InterfaceHash &&
                fallback.Method.MethodId == descriptor.MethodId,
                "missing-descriptor fallback must preserve method identity and Unary kind");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Test]
    public async Task UninterceptedCallContextShouldKeepZeroDescriptorLookups()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "descriptor-without-interceptor",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");

        try
        {
            var stub = new CallContextCountingStub(
                new RpcMethodDescriptor(
                    81, 17, RpcMethodKind.Unary,
                    HasResponsePayload: true,
                    HasClientStreams: false,
                    HasMethodTimeout: false,
                    MethodTimeout: null),
                found: true);
            var context = (SharpLinkCallContextSnapshot)(CreateCallContextMethod.Invoke(
                server,
                [
                    connection,
                    stub,
                    17L,
                    44L,
                    default(RpcDeadline),
                    null,
                    CancellationToken.None
                ]) ?? throw new Exception("CreateCallContext returned null"));

            Ensure(stub.LookupCount == 0,
                "no-interceptor fast path must not query method metadata");
            Ensure(ReferenceEquals(context, connection.DefaultCallContext),
                "no-interceptor fast path must still reuse the default call context");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static SharpLinkServerInvocationContext CreateInterceptedContext(
        SharpLinkServer server,
        ServerConnectionState connection,
        IRpcStub stub,
        long requestId)
        => (SharpLinkServerInvocationContext)(CreateCallContextMethod.Invoke(
            server,
            [
                connection,
                stub,
                17L,
                requestId,
                default(RpcDeadline),
                null,
                CancellationToken.None
            ]) ?? throw new Exception("CreateCallContext returned null"));

    private sealed class CallContextTestInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }

    private sealed class CallContextCountingStub(RpcMethodDescriptor descriptor, bool found) : IRpcStub
    {
        private int _lookupCount;

        internal int LookupCount => Volatile.Read(ref _lookupCount);
        public long InterfaceHash => descriptor.ContractId;

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref _lookupCount);
            if (found && methodHash == descriptor.MethodId)
            {
                method = descriptor;
                return true;
            }
            method = default;
            return false;
        }

        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args)
            => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
