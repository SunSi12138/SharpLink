using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private sealed class GroupSignals
    {
        internal ConcurrentDictionary<string, int> Events { get; } = new();
        internal TaskCompletionSource BodyEntered { get; } = Signal();
        internal TaskCompletionSource CleanupEntered { get; private set; } = Signal();
        internal void ResetCleanup() => CleanupEntered = Signal();
        internal TaskCompletionSource ReleaseBody { get; } = Signal();
        internal TaskCompletionSource ReleaseCleanup { get; } = Signal();
        internal void Observe(string name)
        {
            Events.AddOrUpdate(name, 1, (_, value) => value + 1);
            if (name == "body") BodyEntered.TrySetResult();
            if (name == "cleanup") CleanupEntered.TrySetResult();
        }
        internal int Count(string name) => Events.TryGetValue(name, out var value) ? value : 0;
    }

    private sealed class LoadedGroup
    {
        private AssemblyLoadContext? _context;
        private Assembly? _assembly;
        private ISharpLinkGeneratedAssemblyManifest? _manifest;
        internal WeakReference Weak { get; }
        internal Assembly Assembly => _assembly!;
        internal ISharpLinkGeneratedAssemblyManifest Manifest => _manifest!;
        internal LoadedGroup(string path, int tag, GroupSignals signals)
        {
            _context = new GroupLoadContext();
            Weak = new WeakReference(_context);
            _assembly = _context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var result = SharpLinkAssemblyManifestLoader.TryLoad(_assembly, out _manifest);
            if (!result.Succeeded) throw new InvalidOperationException(result.Error?.Message);
            Set("Tag", tag); Set("Observe", (Action<string>)signals.Observe);
        }
        internal void Set(string name, object? value)
            => _assembly!.GetType("SharpLink.GroupPlugin.Signals")!.GetField(name)!.SetValue(null, value);
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Drop()
        {
            _manifest = null; _assembly = null;
            _context!.Unload(); _context = null;
        }
    }
    private sealed class GroupLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => null; // Share framework/SharpLink, isolate plugin.
    }

    private sealed class GroupFixture : IAsyncDisposable
    {
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal SliceService ControlService { get; } = new();
        internal ISharpLinkGeneratedAssemblyManifest LocalManifest { get; }
        internal ISharpLinkGeneratedAssemblyManifest ProbeManifest { get; }
        private readonly Func<SharpClientBuilder, SharpClientBuilder> _endpoint;
        private readonly List<SharpLinkClient> _additionalClients = [];
        internal TaskCompletionSource Queued { get; } = Signal();
        internal ClientConnection[] Connections => Field<ClientConnection[]>(Client, "_readyConnections");
        internal ServerServiceModuleRegistry Registry => Field<ServerServiceModuleRegistry>(Server, "_serviceModuleRegistry");
        internal GroupFixture(string transport, bool queue = false)
        {
            var local = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IHotRpc).Assembly, out var manifest);
            if (!local.Succeeded) throw new InvalidOperationException(local.Error?.Message);
            LocalManifest = manifest!;
            var probeManifest = ProbeManifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(ISliceRpc).Assembly);
            var sb = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([probeManifest]));
            sb.ReplaceService<ISliceRpc>(_ => ControlService, SharpLinkServiceLifetime.Call);
            if (queue)
            {
                var queuedMethod = LocalManifest.Contracts.Single(c => c.ContractType == typeof(ISecondHotRpc))
                    .Methods.Single().MethodId;
                sb.UseAdmissionControl(o =>
                {
                    o.Global.UseConcurrency(1); o.MaxQueuedCalls = 8; o.MaxQueuedBytes = 1024 * 1024;
                    o.MaxQueueDelay = TimeSpan.FromSeconds(10);
                    o.UsePartition(context => { if (context.MethodId == queuedMethod) Queued.TrySetResult(); return "group"; },
                        partition => partition.UseConcurrency(8));
                });
            }
            var cb = SharpClientBuilder.Create().UseGeneratedManifestSource(new FixedGeneratedManifestSource([LocalManifest, probeManifest]))
                .UseRequestTimeout().UseConnectionPool(o => { o.MinConnections = 2; o.MaxConnections = 2; });
            if (transport == "tcp")
            {
                sb.UseTcp(0, IPAddress.Loopback);
                cb.UseTcp("127.0.0.1", ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port);
                var port = ((IPEndPoint)sb.Transport.LocalEndPoint!).Port;
                _endpoint = builder => builder.UseTcp("127.0.0.1", port);
            }
            else
            {
                var name = "sharplink-775-group-" + Guid.NewGuid().ToString("N");
                sb.UseTransport(new SharedMemoryServerTransportListener(name)); cb.UseSharedMemory(name);
                _endpoint = builder => builder.UseSharedMemory(name);
            }
            Server = (SharpLinkServer)sb.Build(); Client = (SharpLinkClient)cb.Build();
        }
        internal async Task ConnectAsync(CancellationToken token)
        { await Server.StartAsync(token); await Client.ConnectAsync(token); }

        internal SharpLinkClient CreateClient(ISharpLinkGeneratedAssemblyManifest manifest)
        {
            var client = (SharpLinkClient)_endpoint(SharpClientBuilder.Create().UseGeneratedManifestSource(
                new FixedGeneratedManifestSource([manifest])).UseRequestTimeout()
                .UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; })).Build();
            _additionalClients.Add(client);
            return client;
        }

        internal SharpLinkGeneratedContractDescriptor Contract(bool second = false)
            => LocalManifest.Contracts.Single(c => c.ContractType == (second ? typeof(ISecondHotRpc) : typeof(IHotRpc)));
        internal SharpLinkDynamicModule Module(LoadedGroup group) => Registry.DynamicModules.TryGetValue(group.Assembly, out var module)
            ? module : throw new InvalidOperationException("Module not found.");
        internal async Task<StaticBindingAck[][]> BindAllAsync(CancellationToken token)
        {
            var result = new StaticBindingAck[2][];
            for (var i = 0; i < 2; i++)
            {
                result[i] = new StaticBindingAck[2];
                for (var c = 0; c < 2; c++)
                    result[i][c] = await Connections[i].StaticBindings.BindAsync(Contract(c == 1).ContractId,
                        LocalManifest.RpcAssemblyHash).WaitAsync(token);
            }
            return result;
        }
        internal ValueTask<int> CallAsync(int connection, StaticBindingAck ack, string methodName, CancellationToken token)
        {
            var second = ack.ContractId == Contract(true).ContractId;
            if (second) return ((ISecondHotRpc)Proxy(connection, ack, typeof(ISecondHotRpc))).GetSecondAsync();
            var proxy = (IHotRpc)Proxy(connection, ack, typeof(IHotRpc));
            return methodName == nameof(IHotRpc.HoldAsync) ? proxy.HoldAsync(token) : proxy.GetAsync();
        }
        internal ValueTask<int> EchoAsync(int connection, StaticBindingAck ack, CancellationToken token)
            => ((IHotRpc)Proxy(connection, ack, typeof(IHotRpc))).EchoAsync(new HotPayload(7));

        private object Proxy(int connection, StaticBindingAck ack, Type contractType)
        {
            var contract = LocalManifest.Contracts.Single(c => c.ContractType == contractType);
            var local = Client.GetLocalContract(typeof(IHotRpc).Assembly, contract.ContractId);
            var scope = new StaticBindingCallSite(Client, local)
            {
                Snapshot = StaticBindingSnapshot.Create(Field<BindingTopologySnapshot>(Client, "_bindingTopology"),
                    [new StaticBoundTarget(Connections[connection], ack.Token)])
            };
            var registration = Field<ClientAssemblyRegistry>(Client, "_assemblyRegistry").CaptureProxySnapshot()[contractType];
            // Keep generated argument framing and codec ownership, including per-argument lengths.
            return contract.ProxyFactory(scope, registration.Codecs);
        }
        public async ValueTask DisposeAsync()
        {
            Server.AfterBindingGroupCutTestHook = null; Server.BeforeBindingAckTestHook = null;
            Server.ContractManifestPublishBarrierForTesting = null;
            foreach (var client in _additionalClients) await client.StopAsync();
            try { await Client.StopAsync(); } finally { await Server.StopAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SharpLinkAssemblyUnregisterResult> UnregisterAndDropGroupAsync(
        GroupFixture fixture, LoadedGroup group, CancellationToken token)
    {
        // Keep the Assembly argument/JIT temporary outside the frame that checks Weak.IsAlive.
        var result = await fixture.Server.UnregisterAssemblyAsync(group.Assembly, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        group.Drop();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collected(WeakReference reference)
    {
        for (var i = 0; i < 10; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (!reference.IsAlive) return true;
        }
        return false;
    }
}

