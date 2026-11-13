using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private sealed class ControlFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal SliceService Service { get; } = new();
        internal GateTransportFactory Transport { get; }
        internal SharpLinkGeneratedContractDescriptor Contract { get; }
        internal RpcHash128 Hash { get; }
        internal ManualTimeProvider Clock { get; } = new();
        internal ClientConnection[] Connections => Field<ClientConnection[]>(Client, "_readyConnections");
        internal ServerConnectionState[] ServerConnections
            => Field<ServerConnectionRegistry>(Server, "_connectionRegistry").SnapshotActive();

        internal ControlFixture(string transport, int connections = 1)
        {
            var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(ISliceRpc).Assembly);
            Contract = manifest.Contracts.Single(c => c.ContractType == typeof(ISliceRpc));
            Hash = manifest.RpcAssemblyHash;
            var sb = SharpLinkServerBuilder.Create();
            var cb = SharpClientBuilder.Create().UseRequestTimeout().UseTimeProvider(Clock)
                .UseHeartbeat(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2))
                .UseRuntime(o => { o.FlowControl.MaxSendQueueBytes = 1024; o.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency; })
                .UseConnectionPool(o => { o.MinConnections = connections; o.MaxConnections = connections; });
            if (transport == "tcp")
            {
                sb.UseTcp(0, IPAddress.Loopback);
                cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port);
            }
            else
            {
                var name = "sharplink-775-control-" + Guid.NewGuid().ToString("N");
                sb.UseTransport(new SharedMemoryServerTransportListener(name));
                cb.UseSharedMemory(name);
            }
            Transport = new GateTransportFactory(cb.FixedTransportFactory!);
            cb.UseTransport(Transport);
            sb.ReplaceService<ISliceRpc>(_ => { Interlocked.Increment(ref Service.Activations); return Service; }, SharpLinkServiceLifetime.Call);
            Server = (SharpLinkServer)sb.Build();
            Client = (SharpLinkClient)cb.Build();
        }

        internal async Task ConnectAsync(CancellationToken token)
        {
            await Server.StartAsync(token);
            await Client.ConnectAsync(token);
            // A normal-FIFO flush joins handshake/control sends before arming a transport cut.
            foreach (var connection in Connections) await connection.Session.FlushSendQueueAsync(token);
        }

        internal async Task FillQueueAsync(CancellationToken token)
        {
            var session = Connections.Single().Session;
            var gate = Transport.Outputs.Single();
            gate.Arm();
            session.SendHealthCheck(10001);
            await gate.Entered.Task.WaitAsync(token);
            var id = 10002L;
            while (session.QueuedSendBytes <= 1024 - ProtocolV2Constants.HeaderBytes)
                session.SendHealthCheck(id++);
            if (session.QueuedSendBytes != 1020) throw new InvalidOperationException("Queue filler must own exactly 1020 bytes.");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var output in Transport.Outputs) output.Release();
            try { await Client.StopAsync(); }
            finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private sealed class GateTransportFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        internal List<GateWriter> Outputs { get; } = [];
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.ConnectAsync(cancellationToken);
            var output = new GateWriter(connection.Output);
            Outputs.Add(output);
            return new GateConnection(connection, output);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class GateConnection(ITransportConnection inner, PipeWriter output) : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // A wrapper around the *real* TCP/SharedMemory output. The existing send pump keeps
    // exact byte reservations until Flush returns, making genuine queue pressure stable.
    private sealed class GateWriter(PipeWriter inner) : PipeWriter
    {
        private TaskCompletionSource? _release;
        internal TaskCompletionSource Entered { get; private set; } = Signal();
        internal void Arm() { Entered = Signal(); _release = Signal(); }
        internal void Release() => _release?.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override void CancelPendingFlush() { Release(); inner.CancelPendingFlush(); }
        public override void Complete(Exception? exception = null) { Release(); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { Release(); return inner.CompleteAsync(exception); }
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_release is { } release)
            {
                Entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await inner.FlushAsync(cancellationToken);
        }
    }
}

