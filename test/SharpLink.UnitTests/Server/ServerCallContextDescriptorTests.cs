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
    public async Task InterceptedCallContextShouldConsumeResolvedDescriptorWithoutLookup()
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
            var context = CreateInterceptedContext(server, connection, stub, descriptor, requestId: 42);

            Ensure(stub.LookupCount == 0,
                "interceptor context must consume the complete pre-resolved snapshot without a Stub query");
            Ensure(context.Method == descriptor,
                "interceptor context must expose the exact previously resolved descriptor");
            Ensure(context.RequestId == 42 && context.ConnectionId == session.Id,
                "interceptor context must preserve request and connection identity");
            Ensure(context.InterceptorGeneration is not null,
                "interceptor context must retain its captured generation");

            // Missing facts are an invalid internal invocation; they cannot fabricate a
            // Unary descriptor. Unknown wire methods are rejected by the bound front door.
            var missing = new CallContextCountingStub(descriptor, found: false);
            try
            {
                _ = CreateInterceptedContext(server, connection, missing, null, requestId: 43);
                throw new Exception("Missing bound facts must be rejected.");
            }
            catch (TargetInvocationException failure) when (failure.InnerException is InvalidOperationException)
            {
                Ensure(missing.LookupCount == 0, "missing facts must not trigger a fallback lookup");
            }
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
                    CancellationToken.None,
                    new RpcMethodDescriptor(81, 17, RpcMethodKind.Unary, true, false, false, null)
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
        RpcMethodDescriptor? descriptor,
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
                CancellationToken.None,
                descriptor
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

        public bool TryResolveMethod(long methodHash, out RpcMethodDescriptor method)
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
            in RpcMethodDescriptor method,
            long requestId,
            ReadOnlySequence<byte> args)
            => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            in RpcMethodDescriptor method,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            in RpcMethodDescriptor method,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            in RpcMethodDescriptor method,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
