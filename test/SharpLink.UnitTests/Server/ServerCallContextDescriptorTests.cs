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
            await using var registration = ServiceRegistration.CreateSingleton(
                typeof(object), stub, new object(), ownsService: false);
            var prepared = PrepareContextDescriptor(registration, descriptor);
            var context = (SharpLinkServerInvocationContext)CreateContext(server, connection, prepared, requestId: 42);

            Ensure(stub.LookupCount == 1,
                "interceptor context must reuse the descriptor resolved once at publication");
            Ensure(context.Method == descriptor,
                "interceptor context must expose the exact previously resolved descriptor");
            Ensure(context.RequestId == 42 && context.ConnectionId == session.Id,
                "interceptor context must preserve request and connection identity");
            Ensure(context.InterceptorGeneration is not null,
                "interceptor context must retain its captured generation");

            // A custom stub that lacks the declared method cannot publish a binding;
            // creating a conservative Unary fact here would restore the old fallback.
            var missing = new CallContextCountingStub(descriptor, found: false);
            await using var incomplete = ServiceRegistration.CreateSingleton(
                typeof(object), missing, new object(), ownsService: false);
            var rejected = false;
            try { PreparedServerDispatchFixture.PrepareRegistration(incomplete, descriptor); }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected && missing.LookupCount == 1 && !incomplete.TryGetNStudyMethod(descriptor.MethodId, out _),
                "an absent descriptor must stop publication without leaving a fallback method");
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
            var descriptor = new RpcMethodDescriptor(
                81, 17, RpcMethodKind.Unary,
                HasResponsePayload: true,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
            var stub = new CallContextCountingStub(descriptor, found: true);
            await using var registration = ServiceRegistration.CreateSingleton(
                typeof(object), stub, new object(), ownsService: false);
            var prepared = PrepareContextDescriptor(registration, descriptor);
            var context = CreateContext(server, connection, prepared, requestId: 44);

            Ensure(stub.LookupCount == 1,
                "no-interceptor fast path must not perform another lookup after publication");
            Ensure(ReferenceEquals(context, connection.DefaultCallContext),
                "no-interceptor fast path must still reuse the default call context");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static RpcMethodDescriptor PrepareContextDescriptor(ServiceRegistration registration, RpcMethodDescriptor descriptor)
    {
        PreparedServerDispatchFixture.PrepareRegistration(registration, descriptor);
        if (!registration.TryGetNStudyMethod(descriptor.MethodId, out var facts))
            throw new InvalidOperationException("the registration must publish its prepared method");
        return facts.Descriptor;
    }

    private static SharpLinkCallContextSnapshot CreateContext(
        SharpLinkServer server,
        ServerConnectionState connection,
        RpcMethodDescriptor method,
        long requestId)
        => (SharpLinkCallContextSnapshot)(CreateCallContextMethod.Invoke(
            server,
            [
                connection,
                method,
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

        public bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method)
        {
            Interlocked.Increment(ref _lookupCount);
            if (found && methodId == descriptor.MethodId)
            {
                method = new(descriptor, Execute);
                return true;
            }
            method = default;
            return false;
        }

        private static ValueTask Execute(object service, IRpcGeneratedServerBridge bridge, in RpcMethodDescriptor method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte>? output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
