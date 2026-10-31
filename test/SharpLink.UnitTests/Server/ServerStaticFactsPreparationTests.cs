using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class ServerStaticFactsPreparationTests
{
    [Test]
    public async Task BuilderReservesBeforeFactoriesAndSharesItsExactQuotaWithRuntime()
    {
        var manifest = new Manifest();
        var listener = new Listener();
        var builder = Builder(manifest, listener);
        using var plan = Compile(builder);
        Ensure(manifest.Factories == 0 && manifest.Stub.Resolutions == 0);
        Ensure(plan.DeclaredFactsQuota.Methods == 1 && plan.DeclaredFactsQuota.Bytes == 384);
        manifest.OnFactory = () => Ensure(plan.DeclaredFactsQuota.Bytes == 384);
        var server = Materialize(builder, plan);
        try
        {
            var quota = typeof(SharpLinkServer).GetField("_declaredFactsQuota", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server);
            Ensure(ReferenceEquals(quota, plan.DeclaredFactsQuota));
            Ensure(manifest.Factories == 1 && manifest.Stub.Resolutions == 1 && manifest.Stub.Reads == 1);
            Ensure(plan.DeclaredFactsQuota.Bytes == 384);
        }
        finally { await server.DisposeAsync(); }
        Ensure(plan.DeclaredFactsQuota.Bytes == 0 && listener.Disposals == 1);
    }

    [Test]
    public void AbandonedCompiledPlanReleasesHeadersWithoutInvokingFactories()
    {
        var manifest = new Manifest();
        var listener = new Listener();
        var builder = Builder(manifest, listener);
        var plan = Compile(builder);
        Ensure(plan.DeclaredFactsQuota.Bytes == 384);
        plan.Dispose();
        plan.Dispose();
        Ensure(plan.DeclaredFactsQuota.Bytes == 0 && listener.Disposals == 1 && manifest.Factories == 0);
        Expect<InvalidOperationException>(() => Materialize(builder, plan));
        Ensure(listener.Disposals == 1 && manifest.Factories == 0);
    }

    [Test]
    public void FailedFactsCallbackKeepsReservationThroughBuildRollback()
    {
        var manifest = new Manifest();
        var listener = new Listener();
        var builder = Builder(manifest, listener);
        using var plan = Compile(builder);
        var failure = new InvalidOperationException("inline facts failure");
        manifest.Stub.Failure = failure;
        var chargedDuringRollback = false;
        listener.OnDispose = () => chargedDuringRollback = plan.DeclaredFactsQuota.Bytes == 384;
        var error = Expect<ServerDeclaredMethodFactsException>(() => Materialize(builder, plan));
        Ensure(ReferenceEquals(error.InnerException, failure) && chargedDuringRollback);
        Ensure(plan.DeclaredFactsQuota.Bytes == 0 && manifest.Factories == 1 && listener.Disposals == 1);
        Expect<InvalidOperationException>(() => builder.Build());
        Ensure(manifest.Factories == 1);
    }

    [Test]
    public void UnservedContractsReleaseOnlyTheirDiscardedHeaderStaging()
    {
        var manifest = new Manifest(includeService: false);
        var listener = new Listener();
        var builder = Builder(manifest, listener);
        using var plan = Compile(builder);
        Ensure(plan.ServiceCount == 0 && plan.DeclaredFactsQuota.Bytes == 0);
        Ensure(plan.FactsPreparation.ContractCount == 0 && manifest.Factories == 0 && manifest.Stub.Resolutions == 0);
    }

    private static SharpLinkServerBuilder Builder(Manifest manifest, Listener listener)
        => SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new Source(manifest)).UseTransport(listener);

    private static ServerBuildPlan Compile(SharpLinkServerBuilder builder)
        => Invoke<ServerBuildPlan>(typeof(SharpLinkServerBuilder).GetMethod("CompileForBuild",
            BindingFlags.Instance | BindingFlags.NonPublic)!, builder, null);

    private static SharpLinkServer Materialize(SharpLinkServerBuilder builder, ServerBuildPlan plan)
        => Invoke<SharpLinkServer>(typeof(SharpLinkServerBuilder).GetMethod("Materialize",
            BindingFlags.Instance | BindingFlags.NonPublic)!, builder, [plan]);

    private static T Invoke<T>(MethodInfo method, object target, object?[]? arguments)
    {
        try { return (T)method.Invoke(target, arguments)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Static declared-facts ownership assertion failed."); }

    private interface IContract : IService { }
    private sealed class Service : IContract { }
    private sealed class Source(ISharpLinkGeneratedAssemblyManifest manifest) : IGeneratedManifestSource
    {
        public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot() => [manifest];
    }

    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal readonly Stub Stub = new();
        internal int Factories;
        internal Action? OnFactory;
        internal Manifest(bool includeService = true)
        {
            var fingerprint = new string('a', 64);
            Contracts = [new(typeof(IContract), "StaticFactsContract", 8, fingerprint,
                [new("Unary", 1, RpcMethodKind.Unary, true, "request", "response", fingerprint)],
                static (_, _) => throw new NotSupportedException(),
                _ => { Factories++; OnFactory?.Invoke(); return Stub; })];
            Services = includeService
                ? [new(typeof(IContract), typeof(Service), "StaticFactsContract", "StaticFactsService", 8,
                    fingerprint, SharpLinkServiceLifetime.Singleton, [], static _ => new Service())]
                : [];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "static-facts-source-test";
        public Assembly OwnerAssembly => typeof(ServerStaticFactsPreparationTests).Assembly;
        public RpcHash128 RpcAssemblyHash => new(1, 2);
        public string CompileTimeDescriptor => "static-facts-source-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; }
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class Stub : IRpcStub
    {
        private readonly RpcMethodFacts _facts = new(8, 1, RpcMethodKind.Unary, 0, true, true);
        internal int Reads;
        internal int Resolutions;
        internal Exception? Failure;
        public long InterfaceHash { get { Reads++; return 8; } }
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        { Resolutions++; if (Failure is { } failure) throw failure; return _facts; }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => throw new NotSupportedException();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => throw new NotSupportedException();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class Listener : IServerTransportListener
    {
        internal int Disposals;
        internal Action? OnDispose;
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposals++; OnDispose?.Invoke(); return ValueTask.CompletedTask; }
    }
}
