using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    // A handwritten external consumer of the new required ABI, wrapping the real generated Stub.
    private sealed class CountingStub(IRpcStub inner) : IRpcStub
    {
        internal IRpcStub Inner => inner;
        internal int Resolutions;
        internal bool ForbidResolution;
        internal bool MutateResult;
        internal readonly ConcurrentQueue<RpcMethodDescriptor> Invocations = new();
        public long InterfaceHash => inner.InterfaceHash;
        public bool TryResolveMethod(long id, out RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref Resolutions);
            if (ForbidResolution) throw new InvalidOperationException("Facts were queried again after Bind.");
            var known = inner.TryResolveMethod(id, out method);
            if (known && MutateResult) method = method with { IsIdempotent = !method.IsIdempotent };
            return known;
        }
        public ValueTask InvokeNoReturnAsync(object s, IRpcGeneratedServerBridge b, in RpcMethodDescriptor m, long id, ReadOnlySequence<byte> a)
        { Invocations.Enqueue(m); return inner.InvokeNoReturnAsync(s, b, m, id, a); }
        public ValueTask InvokeNoReturnCancellableAsync(object s, IRpcGeneratedServerBridge b, in RpcMethodDescriptor m, long id, ReadOnlySequence<byte> a, CancellationToken ct)
        { Invocations.Enqueue(m); return inner.InvokeNoReturnCancellableAsync(s, b, m, id, a, ct); }
        public ValueTask InvokeAsync(object s, IRpcGeneratedServerBridge b, in RpcMethodDescriptor m, long id, ReadOnlySequence<byte> a, IBufferWriter<byte> o)
        { Invocations.Enqueue(m); return inner.InvokeAsync(s, b, m, id, a, o); }
        public ValueTask InvokeCancellableAsync(object s, IRpcGeneratedServerBridge b, in RpcMethodDescriptor m, long id, ReadOnlySequence<byte> a, IBufferWriter<byte> o, CancellationToken ct)
        { Invocations.Enqueue(m); return inner.InvokeCancellableAsync(s, b, m, id, a, o, ct); }
    }

    private sealed class FactsManifest(ISharpLinkGeneratedAssemblyManifest inner, Action<CountingStub> capture) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => inner.ApiVersion;
        public int ProtocolVersion => inner.ProtocolVersion;
        public string GeneratorVersion => inner.GeneratorVersion;
        public Assembly OwnerAssembly => inner.OwnerAssembly;
        public RpcHash128 RpcAssemblyHash => inner.RpcAssemblyHash;
        public string CompileTimeDescriptor => inner.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = inner.Contracts.Select(c => c.ContractType != typeof(IFactsRpc) ? c : c with
        { StubFactory = codecs => { var stub = new CountingStub(c.StubFactory(codecs)); capture(stub); return stub; } }).ToArray();
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => inner.Services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => inner.Codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => inner.ContractCodecs;
        public IReadOnlyList<string> Dependencies => inner.Dependencies;
        public IReadOnlyList<string> ContractDependencies => inner.ContractDependencies;
    }

    private sealed class FactsMapper : IRpcExceptionMapper
    {
        internal readonly ConcurrentQueue<(Exception Error, RpcMethodDescriptor Method)> Seen = new();
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        { Seen.Enqueue((exception, context.Method)); return new(SharpLinkErrorCode.FailedPrecondition, 321, "facts mapped"); }
    }
    private sealed class FactsInterceptor : ISharpLinkServerInterceptor
    {
        internal readonly ConcurrentQueue<RpcMethodDescriptor> Seen = new();
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        { Seen.Enqueue(context.Method); return next(context); }
    }

    private sealed class FactsFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal FactsService Service { get; } = new();
        internal FactsMapper Mapper { get; } = new();
        internal CountingStub Stub = null!;
        internal SharpLinkGeneratedContractDescriptor Contract { get; }
        internal readonly ConcurrentQueue<(long Contract, long Method, RpcMethodKind Kind)> Admissions = new();
        internal TaskCompletionSource Queued { get; } = Signal();
        internal FactsFixture(string transport)
        {
            var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(IFactsRpc).Assembly);
            var source = new FactsManifest(manifest, stub => Stub = stub);
            Contract = source.Contracts.Single(c => c.ContractType == typeof(IFactsRpc));
            var plain = Contract.Methods.Single(m => m.Name == nameof(IFactsRpc.PlainAsync)).MethodId;
            var sb = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([source]))
                .UseExceptionMapper(Mapper).UseAdmissionControl(o =>
                {
                    o.Global.UseConcurrency(1); o.MaxQueuedCalls = 8; o.MaxQueuedBytes = 1024 * 1024; o.MaxQueueDelay = TimeSpan.FromSeconds(10);
                    o.UsePartition(c => { Admissions.Enqueue((c.ContractId, c.MethodId, c.MethodKind)); if (c.MethodId == plain && Volatile.Read(ref Service.Holding) != 0) Queued.TrySetResult(); return "facts"; }, p => p.UseConcurrency(8));
                });
            sb.ReplaceService<IFactsRpc>(_ => Service, SharpLinkServiceLifetime.Call);
            var cb = SharpClientBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest]))
                .UseRequestTimeout().UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; });
            if (transport == "tcp")
            { sb.UseTcp(0, IPAddress.Loopback); cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port); }
            else
            { var name = "sharplink-775-facts-" + Guid.NewGuid().ToString("N"); sb.UseTransport(new SharedMemoryServerTransportListener(name)); cb.UseSharedMemory(name); }
            Server = (SharpLinkServer)sb.Build(); Client = (SharpLinkClient)cb.Build();
        }
        internal RpcMethodDescriptor Method(string name) => Contract.Methods.Single(m => m.Name == name).Facts;
        public async ValueTask DisposeAsync()
        { Service.Release.TrySetResult(); try { await Client.StopAsync(); } finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); } }
    }

    private sealed class FactsBridge : IRpcGeneratedServerBridge
    {
        internal readonly List<RpcMethodDescriptor> Methods = [];
        internal readonly List<int> Output = [];
        public void EnsureUserCodeEntry(long requestId) { }
        public IAsyncEnumerable<T> CreateInboundStream<T>(long requestId, ushort streamId, IRpcCodec<T> codec, bool nullable, CancellationToken ct)
            => Values<T>(ct);
        private static async IAsyncEnumerable<T> Values<T>([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return (T)(object)1; yield return (T)(object)2; }
        public async ValueTask PumpOutboundStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream, IRpcCodec<T> codec, RpcMethodDescriptor method, CancellationToken ct)
        { Methods.Add(method); await foreach (var value in stream.WithCancellation(ct)) Output.Add((int)(object)value!); }
    }
}
