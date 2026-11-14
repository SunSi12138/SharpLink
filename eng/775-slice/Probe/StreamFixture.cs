using System.Buffers;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    private sealed class StreamFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal FactsService Service { get; } = new();
        internal CountingStub Stub = null!;
        internal ManualTimeProvider Clock { get; } = new();
        internal TaskCompletionSource Queued { get; } = Signal();
        internal RpcProxy<IFactsRpc> Rpc { get; }
        internal StaticBindingCallSite Site { get; }
        internal SharpLinkGeneratedContractDescriptor Contract { get; }
        internal bool RejectActivation;
        internal ClientConnection Connection => Field<ClientConnection[]>(Client, "_readyConnections").Single();
        internal ServerConnectionState Peer => Field<ServerConnectionRegistry>(Server, "_connectionRegistry").SnapshotActive().Single();
        internal StreamFixture(string transport)
        {
            var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(IFactsRpc).Assembly);
            var source = new FactsManifest(manifest, stub => Stub = stub);
            Contract = source.Contracts.Single(c => c.ContractType == typeof(IFactsRpc));
            var sb = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([source]))
                .UseAdmissionControl(o =>
                {
                    o.Global.UseConcurrency(1); o.MaxQueuedCalls = 8; o.MaxQueuedBytes = 1024 * 1024;
                    o.MaxQueueDelay = TimeSpan.FromSeconds(10);
                    o.UsePartition(c => { if (Volatile.Read(ref Service.Holding) != 0 && c.MethodKind != RpcMethodKind.Unary) Queued.TrySetResult(); return "stream"; }, p => p.UseConcurrency(8));
                });
            sb.ReplaceService<IFactsRpc>(_ => RejectActivation ? throw new InvalidOperationException("activation rejected") : Service, SharpLinkServiceLifetime.Call);
            var cb = SharpClientBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest]))
                .UseRequestTimeout().UseTimeProvider(Clock)
                .UseHeartbeat(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2))
                .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; });
            if (transport == "tcp")
            { sb.UseTcp(0, IPAddress.Loopback); cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port); }
            else
            { var name = "sharplink-775-stream-" + Guid.NewGuid().ToString("N"); sb.UseTransport(new SharedMemoryServerTransportListener(name)); cb.UseSharedMemory(name); }
            Server = (SharpLinkServer)sb.Build(); Client = (SharpLinkClient)cb.Build();
            Rpc = Client.CreateProxy<IFactsRpc>();
            Site = (StaticBindingCallSite)Client.CreateContractChannel(Client.GetLocalContract<IFactsRpc>());
        }
        internal RpcMethodDescriptor Method(string name) => Contract.Methods.Single(m => m.Name == name).Facts;
        internal async Task ConnectAsync(CancellationToken token)
        {
            await Server.StartAsync(token); await Client.ConnectAsync(token); await Rpc.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Stub.ForbidResolution = true;
        }
        internal async Task QuiesceAsync(CancellationToken token)
        {
            await UntilAsync(() => Connection.PendingCalls.Count == 0 && Connection.PendingCalls.PhysicalStreamProducers == 0 &&
                Connection.Session.StreamManager.ActiveStreamCount == 0 && Peer.Session.StreamManager.ActiveStreamCount == 0, token);
        }
        public async ValueTask DisposeAsync()
        {
            Server.BeforeStreamAcceptanceTestHook = null; Client.BeforeStreamProducerClaimTestHook = null;
            Service.Release.TrySetResult();
            try { await Client.StopAsync(); } finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    { while (!predicate()) await Task.Delay(1, token); }
    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> stream, CancellationToken token)
    { var values = new List<T>(); await foreach (var value in stream.WithCancellation(token)) values.Add(value); return values; }

    private sealed class ProbeProducer : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int Starts;
        internal bool HoldMove, HoldDispose;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource ReleaseMove { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource ReleaseDispose { get; } = Signal();
        internal TaskCompletionSource Exited { get; } = Signal();
        internal CancellationToken Token;
        private int _index;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Starts); Token = cancellationToken; Entered.TrySetResult(); return this; }
        public int Current => _index;
        public async ValueTask<bool> MoveNextAsync()
        {
            if (HoldMove) await ReleaseMove.Task; // Deliberately ignore cancellation until the physical cut is released.
            Token.ThrowIfCancellationRequested();
            return ++_index <= 2;
        }
        public async ValueTask DisposeAsync()
        { DisposeEntered.TrySetResult(); if (HoldDispose) await ReleaseDispose.Task; Exited.TrySetResult(); }
        internal void Release() { ReleaseMove.TrySetResult(); ReleaseDispose.TrySetResult(); }
    }
    private sealed class RejectStreamInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
            => ValueTask.FromException(new SharpLinkException(SharpLinkErrorCode.PermissionDenied, "before terminal"));
    }
    private sealed class HeldIntCodec(IRpcCodec<int> inner, TaskCompletionSource entered, ManualResetEventSlim release) : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> writer) => inner.Serialize(value, writer);
        public int Deserialize(in ReadOnlySequence<byte> bytes)
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Consumer Codec cut was not released."); return inner.Deserialize(bytes); }
    }
}
