using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.ClientStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.Unary, true)]
    public async Task InvocationShouldReuseCapturedContextDescriptor(RpcMethodKind kind, bool missing)
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
            "invocation-descriptor", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        try
        {
            var descriptor = new RpcMethodDescriptor(
                81, 17, kind, HasResponsePayload: false, HasClientStreams: false,
                HasMethodTimeout: false, MethodTimeout: null);
            var stub = new CallContextCountingStub(descriptor, found: !missing);
            var context = CreateInterceptedContext(server, connection, stub, requestId: 81);
            var registration = ServiceRegistration.CreateSingleton(
                typeof(object), stub, new object(), ownsService: false);
            await InvokeWithDescriptorContext(server, registration, connection, context);

            Ensure(stub.LookupCount == 1,
                "invocation telemetry must reuse the context descriptor, including conservative fallback");
            Ensure(context.Method == (missing ? descriptor with { Kind = RpcMethodKind.Unary } : descriptor),
                "invocation must keep the exact immutable context descriptor");
            Ensure(context.Status == SharpLinkInvocationStatus.Succeeded,
                "descriptor reuse must still run the captured interceptor pipeline");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Test]
    public async Task InvocationWithoutInterceptorShouldResolveDescriptorOnce()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "invocation-no-interceptor", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        try
        {
            var stub = new CallContextCountingStub(
                new RpcMethodDescriptor(81, 17, RpcMethodKind.Unary, false, false, false, null), found: true);
            var registration = ServiceRegistration.CreateSingleton(
                typeof(object), stub, new object(), ownsService: false);
            await InvokeWithDescriptorContext(server, registration, connection, connection.GetCallContextSnapshot(default, null));
            Ensure(stub.LookupCount == 1,
                "plain invocation must retain its single descriptor query without allocating an interceptor context");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DynamicInvocationShouldReuseContextAcrossAcquisition(bool failAcquisition)
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .AddInterceptor(new CallContextTestInterceptor())
            .Build();
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new DescriptorReuseManifest();
        using var codecs = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, codecs);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "dynamic-invocation-descriptor", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        try
        {
            var stub = new CallContextCountingStub(
                new RpcMethodDescriptor(81, 17, RpcMethodKind.Unary, false, false, false, null), found: true);
            var context = CreateInterceptedContext(server, connection, stub, requestId: 81);
            var registration = failAcquisition
                ? ServiceRegistration.CreatePerCall(typeof(object), stub, new DescriptorReuseFailingScopeFactory(),
                    static _ => new object(), disposeService: false, module)
                : ServiceRegistration.CreateSingleton(typeof(object), stub, new object(), ownsService: false, module);
            try
            {
                await InvokeWithDescriptorContext(server, registration, connection, context);
                Ensure(!failAcquisition, "failed acquisition must preserve its exception");
            }
            catch (InvalidOperationException exception) when (failAcquisition)
            {
                Ensure(exception.Message == "descriptor acquisition failure", "acquisition failure preserved");
            }
            Ensure(stub.LookupCount == 1, "module classification and telemetry must reuse the captured descriptor");
            Ensure(module.RemainingCalls == 0, "descriptor reuse must not leak module ownership");
            await registration.DisposeAsync();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private sealed class DescriptorReuseFailingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("descriptor acquisition failure");
    }

    private sealed class DescriptorReuseManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(DescriptorReuseManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x6465736372697074UL, 0x6f722d7265757365UL);
        public string CompileTimeDescriptor => "descriptor-reuse-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private static ValueTask InvokeWithDescriptorContext(
        SharpLinkServer server,
        ServiceRegistration registration,
        ServerConnectionState connection,
        SharpLinkCallContextSnapshot context)
        => (ValueTask)typeof(SharpLinkServer).GetMethod(
            "InvokeServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server,
            [registration, connection, connection.Session, 17L, 81L,
                default(ReadOnlySequence<byte>), null, CancellationToken.None, context])!;
}
