using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.IntegrationTests;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private sealed class OneWayCompression : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        internal int Decodes, Compressed;
        internal Action? BeforeDecode;
        internal bool CompleteDecodeAfterCancellation;
        public string WireProfile => _inner.WireProfile;
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes, CancellationToken cancellationToken = default)
        { var result = _inner.TryCompress(input, output, maxOutputBytes, cancellationToken); if (result) Interlocked.Increment(ref Compressed); return result; }
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Decodes); BeforeDecode?.Invoke(); _inner.Decompress(input, output, maxOutputBytes, CompleteDecodeAfterCancellation ? CancellationToken.None : cancellationToken); }
    }
    private sealed class OneWayFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal OneWayInputService Service { get; } = new();
        internal OneWayCompression Compression { get; } = new();
        internal OneWayCompression ClientCompression { get; } = new();
        internal LocalDiagnosticMapper Mapper { get; } = new();
        internal ManualTimeProvider Clock { get; } = new();
        internal StaticBindingCallSite Site { get; }
        internal RpcProxy<IOneWayInputRpc> Rpc { get; }
        internal RpcMethodDescriptor Fire { get; }
        internal ClientConnection Connection => Field<ClientConnection[]>(Client, "_readyConnections").Single();
        internal ServerConnectionState Peer => Field<ServerConnectionRegistry>(Server, "_connectionRegistry").SnapshotActive().Single();
        internal OneWayFixture(string transport, bool compression = false, bool callQuota = false, bool manualClock = false)
        {
            var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(IOneWayInputRpc).Assembly);
            Fire = manifest.Contracts.Single(c => c.ContractType == typeof(IOneWayInputRpc)).Methods.Single(m => m.Name == nameof(IOneWayInputRpc.FireAsync)).Facts;
            var sb = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest])).UseExceptionMapper(Mapper)
                .UseRuntime(o => { Configure(o); if (callQuota) { o.FlowControl.MaxConcurrentCallsPerConnection = 1; o.FlowControl.MaxConcurrentCallsPerServer = 1; } if (compression) o.Compression.Providers.Add(Compression); });
            sb.ReplaceService<IOneWayInputRpc>(_ => Service, SharpLinkServiceLifetime.Call);
            var cb = SharpClientBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest])).UseRequestTimeout()
                .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; })
                .UseRuntime(o => { Configure(o); if (compression) o.Compression.Providers.Add(ClientCompression); });
            if (manualClock)
            {
                sb.UseTimeProvider(Clock).UseHeartbeat(TimeSpan.FromDays(1), TimeSpan.FromDays(2));
                cb.UseRequestTimeout(TimeSpan.FromSeconds(5));
            }
            if (compression) cb.UseRequestCompressionPolicy(new() { MinimumPayloadBytes = 1, MinimumSavingsBytes = 1, MinimumSavingsRatio = 0 });
            if (transport == "tcp") { sb.UseTcp(0, IPAddress.Loopback); cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port); }
            else { var name = "sharplink-775-oneway-" + Guid.NewGuid().ToString("N"); sb.UseTransport(new SharedMemoryServerTransportListener(name)); cb.UseSharedMemory(name); }
            Server = (SharpLinkServer)sb.Build(); Client = (SharpLinkClient)cb.Build();
            Rpc = Client.CreateProxy<IOneWayInputRpc>(); Site = (StaticBindingCallSite)Client.CreateContractChannel(Client.GetLocalContract<IOneWayInputRpc>());
        }
        private static void Configure(SharpLinkRuntimeOptions o)
        { o.FlowControl.StreamReceiveWindowBytes = 64; o.FlowControl.ConnectionReceiveWindowBytes = 128; o.Protocol.MaxConcurrentStreamsPerConnection = 4; }
        internal async Task ConnectAsync(CancellationToken ct)
        { await Server.StartAsync(ct); await Client.ConnectAsync(ct); await Rpc.PrepareAsync(TimeSpan.FromSeconds(5), ct); }
        internal async Task QuiesceAsync(CancellationToken ct)
        {
            await UntilAsync(() => Connection.ActiveCallCount == 0 && Peer.Session.StreamManager.ActiveStreamCount == 0 &&
                Flow(Connection.Session).RetainedSendStreamCount == 0 && Flow(Connection.Session).SendConnectionCredit == 128, ct);
        }
        internal static StreamFlowController Flow(RpcSession session) => Field<RpcSessionProtocolState>(session, "_protocolState").FlowController!;
        internal async Task<long> HeadAsync(ulong binding, long method, int count, bool compressed, CancellationToken ct)
        {
            var id = Connection.PendingCalls.AllocateRequestId();
            var w = Connection.Session.RentFrameWriter();
            using (w.BeginPacketScope(StaticBindingWire.OneWayInputRequest, ProtocolV2FrameFlags.OneWay | (compressed ? ProtocolV2FrameFlags.Compressed : 0), (ulong)id))
            {
                w.GetSpan(1)[0] = checked((byte)count); w.Advance(1);
                BinaryPrimitives.WriteUInt64LittleEndian(w.GetSpan(8), binding); w.Advance(8);
                BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(8), method); w.Advance(8);
                if (compressed) { BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), 256); w.Advance(4); w.Write(new byte[] { 0xFF }); } // Invalid provider data must never be decoded on refusal.
            }
            await Connection.Session.SendPacketAndFlushAsync(w, ct); return id;
        }
        internal async Task DataAsync(long id, int bytes, bool compressed, bool credit, CancellationToken ct)
        {
            var session = Connection.Session;
            if (credit) await session.AcquireStreamSendCreditAsync(id, 1, bytes, ct);
            var w = session.RentFrameWriter();
            using (w.BeginPacketScope(ProtocolV2FrameType.StreamData, compressed ? ProtocolV2FrameFlags.Compressed : 0, (ulong)id))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(w.GetSpan(2), 1); w.Advance(2);
                if (compressed) { BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), bytes); w.Advance(4); w.Write(new byte[] { 0xFF }); }
                else { w.GetSpan(bytes)[..bytes].Clear(); w.Advance(bytes); }
            }
            await session.SendPacketAndFlushAsync(w, ct);
        }
        internal async Task EndAsync(long id, int count, CancellationToken ct)
        {
            for (ushort stream = 1; stream <= count; stream++)
            {
                var w = Connection.Session.RentFrameWriter();
                using (w.BeginPacketScope(ProtocolV2FrameType.StreamComplete, 0, (ulong)id))
                { BinaryPrimitives.WriteUInt16LittleEndian(w.GetSpan(2), stream); w.Advance(2); }
                await Connection.Session.SendPacketAndFlushAsync(w, ct); Connection.Session.CompleteSendStream(id, stream);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Service.Release.TrySetResult();
            try { await Client.StopAsync(); } finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    private static async IAsyncEnumerable<OneWayValue> OneWayValues(int count = 4)
    { for (var i = 1; i <= count; i++) { await Task.Yield(); yield return new(i); } }
}
