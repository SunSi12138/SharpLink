using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.PackageSmoke;

// This assembly consumes NuGet packages and has no InternalsVisibleTo access.
internal static class ExternalApiBoundaryProbe
{
    public static void Run()
    {
        VerifyOwnerResolverForwarding();
        VerifyOwnerResolverFailures();
        VerifyGeneratedSizingBoundary();
        VerifyExternalInheritance();
        Console.WriteLine("EXTERNAL_API_BOUNDARIES_PASS resolver+generated-sizing-boundary+inheritance; transport-constructors=compile-only");
    }

    // Intentionally never called: SocketServerTransportListener binds immediately.
    // C# still checks all eight public constructors and their configuration parameters.
    private static void CompileTransportConstructors(
        EndPoint endpoint,
        SocketTransportOptions socketOptions,
        SslClientAuthenticationOptions clientTls,
        SslServerAuthenticationOptions serverTls,
        SharedMemoryTransportOptions sharedMemoryOptions)
    {
        IClientTransportFactory socketClient = new SocketClientTransportFactory(
            endpoint, socketOptions, clientTls, TimeSpan.FromSeconds(2));
        IServerTransportListener socketServer = new SocketServerTransportListener(
            endpoint, backlog: 16, options: socketOptions, tlsOptions: serverTls,
            tlsHandshakeTimeout: TimeSpan.FromSeconds(2));
        IClientTransportFactory namedPipeClient = new NamedPipeClientTransportFactory(
            "external-api", serverName: ".", pipeOptions: PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        IServerTransportListener namedPipeServer = new NamedPipeServerTransportListener(
            "external-api", maxServerInstances: 2, transmissionMode: PipeTransmissionMode.Byte,
            pipeOptions: PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        IClientTransportFactory anonymousPipeClient = new AnonymousPipeClientTransportFactory("101", "102");
        IServerTransportListener anonymousPipeServer = new AnonymousPipeServerTransportListener(offerQueueCapacity: 7);
        IClientTransportFactory sharedMemoryClient = new SharedMemoryClientTransportFactory("external-api", sharedMemoryOptions);
        IServerTransportListener sharedMemoryServer = new SharedMemoryServerTransportListener("external-api", sharedMemoryOptions);
    }

    private static void VerifyOwnerResolverForwarding()
    {
        var owner = typeof(ExternalApiBoundaryProbe).Assembly;
        var otherOwner = typeof(SharpLinkException).Assembly;
        var expected = new UnusedCodecProvider();
        var otherExpected = new UnusedCodecProvider();
        var context = new ConsumerResolverContext(owner, expected, otherOwner, otherExpected);

        AssertForwarded(RpcGeneratedCodecResolver.GetProvider(context, owner), expected, context, owner, 1);
        AssertForwarded(RpcGeneratedCodecResolver.GetProvider(context, typeof(ExternalApiBoundaryProbe).Assembly), expected, context, owner, 2);
        AssertForwarded(RpcGeneratedCodecResolver.GetProvider(context, typeof(ConsumerAddress).Assembly), expected, context, owner, 3);
        AssertForwarded(RpcGeneratedCodecResolver.GetProvider(context, typeof(SharpLinkException).Assembly), otherExpected, context, otherOwner, 4);
    }

    private static void AssertForwarded(
        IRpcCodecProvider actual, IRpcCodecProvider expected, ConsumerResolverContext context,
        Assembly expectedOwner, int expectedCalls)
    {
        if (!ReferenceEquals(actual, expected) || !ReferenceEquals(context.LastOwner, expectedOwner) || context.Calls != expectedCalls)
            throw new InvalidOperationException("External Codec resolver lost owner identity or did not forward exactly once.");
    }

    private static void VerifyOwnerResolverFailures()
    {
        var context = new ContextWithoutResolver();
        var owner = typeof(ExternalApiBoundaryProbe).Assembly;
        ExpectNull(() => RpcGeneratedCodecResolver.GetProvider(null!, owner), "runtimeContext");
        ExpectNull(() => RpcGeneratedCodecResolver.GetProvider(context, (Assembly)null!), "ownerAssembly");
        ExpectNull(() => RpcGeneratedCodecResolver.GetProvider(null!, typeof(ExternalApiBoundaryProbe).Assembly), "runtimeContext");
        ExpectUnsupported(() => RpcGeneratedCodecResolver.GetProvider(context, owner));
        ExpectUnsupported(() => RpcGeneratedCodecResolver.GetProvider(context, typeof(ExternalApiBoundaryProbe).Assembly));
    }

    private static void ExpectNull(Action resolve, string parameter)
    {
        try
        {
            resolve();
        }
        catch (ArgumentNullException exception) when (exception.ParamName == parameter)
        {
            return;
        }
        throw new InvalidOperationException($"External Codec resolver did not reject null {parameter}.");
    }

    private static void ExpectUnsupported(Action resolve)
    {
        try
        {
            resolve();
        }
        catch (NotSupportedException exception) when (exception.Message.Contains(nameof(IRpcContractCodecProviderResolver), StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("A runtime without an owner resolver must be rejected explicitly.");
    }

    private static void VerifyGeneratedSizingBoundary()
    {
        // The real generated snapshot ABI remains public; the unused legacy scope is removed.
        var abstractions = typeof(IRpcSizedCodec<>).Assembly;
        if (abstractions != typeof(IRpcSizedCodecSnapshot).Assembly ||
            abstractions.GetType("SharpLink.Abstractions.RpcGeneratedCodecSizing", throwOnError: false) is not null)
            throw new InvalidOperationException("Generated sizing ABI or removed legacy helper boundary changed.");
    }

    private static void VerifyExternalInheritance()
    {
        var cause = new InvalidOperationException("local cause");
        SharpLinkException error = new ConsumerException(cause);
        if (error.Code != SharpLinkErrorCode.Unavailable || error.DetailCode != 73 ||
            error.Message != "consumer failure" || !ReferenceEquals(error.InnerException, cause))
            throw new InvalidOperationException("External exception inheritance lost its base error information.");

        SharpLinkTransportAddress address = new ConsumerAddress("orders");
        var routes = new Dictionary<SharpLinkTransportAddress, string> { [address] = "local-handler" };
        if (!routes.TryGetValue(new ConsumerAddress("orders"), out var route) || route != "local-handler" ||
            address.Equals(new ConsumerAddress("payments")))
            throw new InvalidOperationException("External address inheritance lost stable value identity.");
        var changed = ((ConsumerAddress)address) with { Route = "payments" };
        if (((ConsumerAddress)address).Route != "orders" || changed.Route != "payments")
            throw new InvalidOperationException("External address copy changed the original value.");
    }

    private class ContextWithoutResolver : IRpcRuntimeContext
    {
        public IRpcCodecProvider Codecs => throw new InvalidOperationException("Owner resolution must not read global Codecs.");
        public IRpcBufferWriterPool Buffers => throw new InvalidOperationException("Owner resolution must not use buffers.");
    }

    private sealed class ConsumerResolverContext(
        Assembly owner, IRpcCodecProvider provider, Assembly otherOwner, IRpcCodecProvider otherProvider)
        : ContextWithoutResolver, IRpcContractCodecProviderResolver
    {
        public Assembly? LastOwner { get; private set; }
        public int Calls { get; private set; }

        public IRpcCodecProvider GetContractCodecProvider(Assembly ownerAssembly)
        {
            LastOwner = ownerAssembly;
            Calls++;
            if (ReferenceEquals(ownerAssembly, owner))
                return provider;
            if (ReferenceEquals(ownerAssembly, otherOwner))
                return otherProvider;
            throw new InvalidOperationException("Unexpected owner in external resolver probe.");
        }
    }

    private sealed class UnusedCodecProvider : IRpcCodecProvider
    {
        public IRpcCodec<T> GetCodec<T>() => throw new InvalidOperationException("Forwarding a provider must not resolve codecs.");
    }

    private sealed class ConsumerException(Exception cause)
        : SharpLinkException(SharpLinkErrorCode.Unavailable, 73, "consumer failure", cause);

    private sealed record ConsumerAddress(string Route) : SharpLinkTransportAddress;
}
