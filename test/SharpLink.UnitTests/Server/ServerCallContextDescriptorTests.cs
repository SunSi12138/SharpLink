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
                "interceptor context must project the already-resolved shape exactly once");
            Ensure(context.Method == descriptor,
                "interceptor context must expose the exact previously resolved descriptor");
            Ensure(context.RequestId == 42 && context.ConnectionId == session.Id,
                "interceptor context must preserve request and connection identity");
            Ensure(context.InterceptorGeneration is not null,
                "interceptor context must retain its captured generation");

            var missing = new CallContextCountingStub(descriptor, found: false);
            try
            {
                _ = CreateInterceptedContext(server, connection, missing, requestId: 43);
                throw new Exception("unknown method must not produce an observer descriptor");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
            {
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
                    ResolveContextTestMethod(stub),
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
                ResolveContextTestMethod(stub),
                requestId,
                default(RpcDeadline),
                null,
                CancellationToken.None
            ]) ?? throw new Exception("CreateCallContext returned null"));

    private static ResolvedMethodCall ResolveContextTestMethod(IRpcStub stub)
        => new(ServiceRegistration.CreateSingleton(
            typeof(object), stub, new object(), ownsService: false),
            17L, stub.ResolveMethodShape(17L));

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

        public RpcMethodShape ResolveMethodShape(long methodHash)
            => found && methodHash == descriptor.MethodId
                ? new RpcMethodShape(descriptor.Kind, descriptor.ClientStreamCount,
                    supportsCancellation: true, descriptor.HasResponsePayload,
                    descriptor.ResponseNullable, descriptor.HasMethodTimeout,
                    descriptor.IsIdempotent, descriptor.MethodTimeout.HasValue)
                : RpcMethodShape.UnknownMethod;

        public void DescribeMethod(long methodHash, RpcMethodShape shape, out RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref _lookupCount);
            method = shape.IsKnown
                ? descriptor
                : RpcMethodDescriptor.FromShape(InterfaceHash, methodHash, shape);
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
