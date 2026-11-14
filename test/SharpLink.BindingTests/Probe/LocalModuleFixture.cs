using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private sealed class LocalModuleFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal ManualTimeProvider Clock { get; } = new();
        internal LocalDiagnosticMapper Mapper { get; } = new();
        internal ClientConnection Connection => Field<ClientConnection[]>(Client, "_readyConnections").Single();
        internal ClientAssemblyRegistry Registry => Field<ClientAssemblyRegistry>(Client, "_assemblyRegistry");
        internal LocalModuleFixture(string transport)
        {
            var probe = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(ISliceRpc).Assembly);
            var loaded = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IHotRpc).Assembly, out var hot);
            if (!loaded.Succeeded) throw new InvalidOperationException(loaded.Error?.Message);
            var sb = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([probe, hot!]))
                .UseExceptionMapper(Mapper);
            var cb = SharpClientBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([probe]))
                .UseRequestTimeout().UseTimeProvider(Clock).UseHeartbeat(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2))
                .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; });
            if (transport == "tcp")
            { sb.UseTcp(0, IPAddress.Loopback); cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port); }
            else
            { var name = "sharplink-775-local-" + Guid.NewGuid().ToString("N"); sb.UseTransport(new SharedMemoryServerTransportListener(name)); cb.UseSharedMemory(name); }
            Server = (SharpLinkServer)sb.Build(); Client = (SharpLinkClient)cb.Build();
        }
        internal async Task ConnectAsync(CancellationToken token)
        { await Server.StartAsync(token); await Client.ConnectAsync(token); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal SharpLinkDynamicModule Module(LoadedGroup group) => Registry.DynamicModules[group.Assembly];
        public async ValueTask DisposeAsync()
        {
            Client.BeforeBoundStreamRegistrationTestHook = null; Client.BeforeStreamProducerClaimTestHook = null;
            Server.BeforeStreamAcceptanceTestHook = null;
            try { await Client.StopAsync(); } finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private sealed class LocalDiagnosticMapper : IRpcExceptionMapper
    {
        private readonly DefaultRpcExceptionMapper _inner = new(false);
        internal readonly ConcurrentQueue<Exception> Failures = new();
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Console.WriteLine("LOCAL_SERVER_ERROR " + exception);
            Failures.Enqueue(exception);
            return _inner.Map(exception, context);
        }
    }

    private sealed class LocalDriver(LoadedGroup group)
    {
        private object? _proxy;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private object? Invoke(string name, params object?[] args)
        {
            try { return group.Assembly.GetType("SharpLink.GroupPlugin.LocalDriver")!.GetMethod(name)!.Invoke(null, args); }
            catch (TargetInvocationException failure) when (failure.InnerException is not null)
            { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
        }
        internal void Create(SharpLinkClient client, RpcProxyOptions? options = null) => _proxy = Invoke("Create", client, options);
        internal RpcLocalContract Local(SharpLinkClient client) => (RpcLocalContract)Invoke("Local", client)!;
        internal ValueTask Prepare(CancellationToken token) => (ValueTask)Invoke("Prepare", _proxy, token)!;
        internal ValueTask<int> Unary() => (ValueTask<int>)Invoke("Unary", _proxy)!;
        internal ValueTask<int> Echo() => (ValueTask<int>)Invoke("Echo", _proxy)!;
        internal ValueTask<int> Consume(IAsyncEnumerable<int> input, CancellationToken token) => (ValueTask<int>)Invoke("Consume", _proxy, input, token)!;
        internal ValueTask<int> ConsumeWithHeader(IAsyncEnumerable<int> input, CancellationToken token) => (ValueTask<int>)Invoke("ConsumeWithHeader", _proxy, input, token)!;
        internal ValueTask FireInput(IAsyncEnumerable<int> input, CancellationToken token) => (ValueTask)Invoke("FireInput", _proxy, input, token)!;
        internal IAsyncEnumerable<int> Produce(CancellationToken token) => (IAsyncEnumerable<int>)Invoke("Produce", _proxy, token)!;
        internal IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> input, CancellationToken token) => (IAsyncEnumerable<int>)Invoke("Duplex", _proxy, input, token)!;
        internal RpcMethodDescriptor Method(string name) => group.Manifest.Contracts.Single(c => c.ContractType.Name == nameof(IHotRpc)).Methods.Single(m => m.Name == name).Facts;
        internal void Drop() { _proxy = null; group.Drop(); }
    }
    private sealed class LocalPolicy : ISharpLinkClientInterceptor
    {
        internal int Calls;
        internal SharpLinkClientModuleCancellation? Cancellation;
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref Calls);
            if (SharpLinkClientStreamModuleLeaseContext.Current is { } owner)
                Cancellation = Field<IDisposable?>(owner, "_cancellation") as SharpLinkClientModuleCancellation;
            else if (SharpLinkClientModuleCallContext.Current is { } callOwner)
                Cancellation = callOwner.Cancellation;
            return next(context);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterLocal(LocalModuleFixture f, LoadedGroup group) => f.Client.RegisterAssembly(group.Assembly).Succeeded;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<SharpLinkAssemblyReplacementResult> ReplaceLocal(LocalModuleFixture f, LoadedGroup old, LoadedGroup next, TimeSpan? timeout = null)
        => f.Client.ReplaceAssemblyAsync(old.Assembly, next.Assembly, timeout ?? TimeSpan.FromSeconds(30));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterLocal(LocalModuleFixture f, LoadedGroup group, TimeSpan? timeout = null)
        => f.Client.UnregisterAssemblyAsync(group.Assembly, timeout ?? TimeSpan.FromSeconds(30));

    private static async Task LocalAlcAsync(string transport, string name, Func<LocalModuleFixture, CancellationToken, Task<(WeakReference Weak, IRpcContractChannel? Channel)>> body, CancellationToken token)
    {
        await using var f = new LocalModuleFixture(transport);
        // The case can finish inline on a collectible consumer's final MoveNext stack.
        // Observe from a queued continuation, after that completion frame can unwind;
        // otherwise the checker itself is executing inside the ALC it tries to unload.
        var result = await body(f, token).ContinueWith(static task => task.GetAwaiter().GetResult(),
            CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
        if (f.Mapper.Failures.FirstOrDefault(e => e is not OperationCanceledException) is { } unexpected)
            throw new InvalidOperationException("Unexpected mapped server failure cannot be hidden by a client terminal.", unexpected);
        GCheck(Collected(result.Weak), name, "old ALC collects with client server and scalar channel live");
        GC.KeepAlive(result.Channel); GC.KeepAlive(f.Client); GC.KeepAlive(f.Server);
    }
}
