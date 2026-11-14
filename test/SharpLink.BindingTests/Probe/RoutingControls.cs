using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.BindingTests;

[RpcContract]
public interface IBoundRoutingRpc : IService
{
    [NonCancellable] ValueTask<string> EndpointAsync();
    [Idempotent, NonCancellable] ValueTask<string> RetryAsync();
    IAsyncEnumerable<string> HoldAsync(CancellationToken cancellationToken);
}

public sealed class BoundRoutingService(string id) : IBoundRoutingRpc
{
    internal bool RejectRetry;
    internal int RetryCalls;
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<string> EndpointAsync() => ValueTask.FromResult(id);
    public ValueTask<string> RetryAsync()
    {
        Interlocked.Increment(ref RetryCalls);
        return RejectRetry
            ? ValueTask.FromException<string>(new SharpLinkException(SharpLinkErrorCode.Unavailable, "routing retry control"))
            : ValueTask.FromResult(id);
    }
    public async IAsyncEnumerable<string> HoldAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return id;
        await Release.Task.WaitAsync(cancellationToken);
        yield return id;
    }
}

internal static partial class Program
{
    private static async Task RunRoutingAsync(string transport, CancellationToken token)
    {
        await using var first = new RoutingServer(transport, "first");
        await using var second = new RoutingServer(transport, "second");
        await using var third = new RoutingServer(transport, "third");
        await first.Server.StartAsync(token);
        await second.Server.StartAsync(token);
        await third.Server.StartAsync(token);
        var endpoints = new[] { first.Endpoint("first"), second.Endpoint("second") };
        var factory = transport == "tcp" ? SharpLinkTransportFactories.Sockets() : SharpLinkTransportFactories.SharedMemory();
        var dials = 0;
        IClientTransportFactory Counted(SharpLinkEndpoint endpoint)
            => new RoutingTransport(factory(endpoint), () => Interlocked.Increment(ref dials));
        SharpClientBuilder Configure(SharpClientBuilder builder) => builder.DisableRequestTimeout()
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseRetry(options =>
            {
                options.MaxAttempts = 2;
                options.InitialBackoff = TimeSpan.Zero;
                options.MaxBackoff = TimeSpan.Zero;
                options.JitterRatio = 0;
            });

        await using (var client = (SharpLinkClient)Configure(SharpClientBuilder.Create()
            .UseEndpoints(endpoints, Counted)).UseEndpointSelector(new RoutingSelector("first")).Build())
        {
            var proxy = client.CreateProxy<IBoundRoutingRpc>();
            var site = (StaticBindingCallSite)client.CreateContractChannel(client.GetLocalContract<IBoundRoutingRpc>());
            Check(site.Snapshot.Targets.Length == 0 && client.ReadyConnectionCount == 0, "routing static factory remains local");
            await client.ConnectAsync(token);
            await proxy.PrepareAsync(TimeSpan.FromSeconds(10), token);
            await WaitUntilAsync(client, () => site.Snapshot.Endpoints.Length == 2, token);
            Check(site.Snapshot.Targets.Length == 2 && site.Snapshot.Targets.Select(t => t.Connection.Session).Distinct().Count() == 2,
                "routing static publication retains two Session token pairs");
            Check(await proxy.Service.EndpointAsync() == "first", "routing static custom selector chooses first");
            client.UpdateEndpointSelector(new RoutingSelector("second"));
            var routed = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => proxy.Service.EndpointAsync().AsTask()));
            Check(routed.All(id => id == "second"), "routing static replacement policy selects second concurrently");
            Check(dials == 2, "routing static policy changes do not reconnect");
            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
            var ordered = new[] { await proxy.Service.EndpointAsync(), await proxy.Service.EndpointAsync(),
                await proxy.Service.EndpointAsync(), await proxy.Service.EndpointAsync() };
            Check(ordered[0] != ordered[1] && ordered[0] == ordered[2] && ordered[1] == ordered[3],
                "routing static RoundRobin uses endpoint cursor");
            foreach (var strategy in new[] { SharpLinkLoadBalancingStrategy.Random, SharpLinkLoadBalancingStrategy.LeastPending,
                SharpLinkLoadBalancingStrategy.PowerOfTwoChoices })
            {
                client.UpdateLoadBalancing(strategy);
                var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => proxy.Service.EndpointAsync().AsTask()));
                Check(ids.All(id => id is "first" or "second"), "routing static built-in " + strategy + " stays in bound candidates");
            }
            client.UpdateEndpointSelector(new InvalidRoutingSelector());
            await ExpectCode(() => proxy.Service.EndpointAsync().AsTask(), SharpLinkErrorCode.FailedPrecondition,
                "routing static invalid selector fails only the call");
            client.UpdateEndpointSelector(new ThrowingRoutingSelector());
            await ExpectCode(() => proxy.Service.EndpointAsync().AsTask(), SharpLinkErrorCode.FailedPrecondition,
                "routing static throwing selector preserves failure boundary");
            client.UpdateEndpointSelector(new RoutingSelector("first"));
            first.Service.RejectRetry = true;
            Check(await proxy.Service.RetryAsync() == "second" && first.Service.RetryCalls == 1 && second.Service.RetryCalls == 1,
                "routing static retry excludes previously selected endpoint");
            Check(client.State == SharpLinkConnectionState.Ready && await proxy.Service.EndpointAsync() == "first" && dials == 2,
                "routing static selector and retry failures preserve healthy Sessions");
            await client.StopAsync(token);
            Check(site.Snapshot.Targets.Length == 0 && client.BindingBudget.Operations == 0 && client.BindingBudget.Bindings == 0,
                "routing static Stop releases binding routes and quotas");
        }

        dials = 0;
        var resolver = new RoutingResolver(new SharpLinkEndpointSnapshot(1, endpoints));
        await using (var client = (SharpLinkClient)Configure(SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, Counted)).UseEndpointSelector(new RoutingSelector("second")).Build())
        {
            var proxy = client.CreateProxy<IBoundRoutingRpc>();
            var site = (StaticBindingCallSite)client.CreateContractChannel(client.GetLocalContract<IBoundRoutingRpc>());
            Check(site.Snapshot.Targets.Length == 0 && client.ReadyConnectionCount == 0, "routing dynamic factory remains local");
            await client.ConnectAsync(token);
            await proxy.PrepareAsync(TimeSpan.FromSeconds(10), token);
            await WaitUntilAsync(client, () => site.Snapshot.Endpoints.Length == 2, token);
            Check(await proxy.Service.EndpointAsync() == "second" && dials == 2, "routing dynamic selector uses confirmed initial topology");
            var old = site.Snapshot.Endpoints.Single(e => e.Candidate!.Value.Endpoint.Id == "second");
            await using var stream = proxy.Service.HoldAsync(token).GetAsyncEnumerator(token);
            Check(await stream.MoveNextAsync() && stream.Current == "second", "routing dynamic stream captures original endpoint");
            await resolver.PublishAsync(new SharpLinkEndpointSnapshot(2,
                [first.Endpoint("first"), third.Endpoint("second")]), token);
            await WaitUntilAsync(client, () => site.Snapshot.Endpoints.Length == 2 &&
                site.Snapshot.Endpoints.Any(e => e.Candidate!.Value.Endpoint.Id == "second" &&
                    e.Candidate.Value.Generation != old.Candidate!.Value.Generation), token);
            var replacement = site.Snapshot.Endpoints.Single(e => e.Candidate!.Value.Endpoint.Id == "second");
            Check(replacement.Candidate!.Value.Generation != old.Candidate!.Value.Generation &&
                !ReferenceEquals(replacement.Targets[0].Connection, old.Targets[0].Connection),
                "routing dynamic same-ID replacement publishes new generation and Session");
            Check(!old.Targets[0].Connection.CanAcceptCalls, "routing dynamic retired Session rejects new admission");
            Check(await proxy.Service.EndpointAsync() == "third", "routing dynamic new calls use replacement token");
            second.Service.Release.TrySetResult();
            Check(await stream.MoveNextAsync() && stream.Current == "second", "routing dynamic admitted stream remains on original Session");
            Check(!await stream.MoveNextAsync(), "routing dynamic original stream physically completes");
            Check(dials == 3, "routing dynamic replacement dials only one new Session");
            client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
            var ids = new[] { await proxy.Service.EndpointAsync(), await proxy.Service.EndpointAsync() };
            Check(ids.Distinct().Count() == 2 && ids.All(id => id is "first" or "third"),
                "routing dynamic RoundRobin excludes retired generation");
            await client.StopAsync(token);
            Check(site.Snapshot.Targets.Length == 0 && client.BindingBudget.Operations == 0 && client.BindingBudget.Bindings == 0,
                "routing dynamic Stop releases binding routes and quotas");
        }
    }

    private sealed class RoutingServer : IAsyncDisposable
    {
        internal readonly SharpLinkServer Server;
        internal readonly BoundRoutingService Service;
        private readonly SharpLinkTransportAddress _address;
        internal RoutingServer(string transport, string id)
        {
            Service = new(id);
            var builder = SharpLinkServerBuilder.Create().DisableAutomaticServiceRegistration()
                .ReplaceService<IBoundRoutingRpc>(Service);
            if (transport == "tcp")
            {
                builder.UseTcp(0, IPAddress.Loopback);
                _address = new SharpLinkTcpAddress("127.0.0.1", ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port);
            }
            else
            {
                var name = "sharplink-775-routing-" + Guid.NewGuid().ToString("N");
                builder.UseTransport(new SharedMemoryServerTransportListener(name));
                _address = new SharpLinkSharedMemoryAddress(name);
            }
            Server = (SharpLinkServer)builder.Build();
        }
        internal SharpLinkEndpoint Endpoint(string id) => new() { Id = id, Address = _address };
        public async ValueTask DisposeAsync()
        {
            Service.Release.TrySetResult();
            await Server.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class RoutingTransport(IClientTransportFactory inner, Action connected) : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            connected();
            return await inner.ConnectAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RoutingSelector(string preferred) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            var fallback = -1;
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) != 0) continue;
                if (context[index].Endpoint.Id == preferred) return index;
                fallback = index;
            }
            return fallback;
        }
    }
    private sealed class InvalidRoutingSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => int.MaxValue;
    }
    private sealed class ThrowingRoutingSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context) => throw new InvalidOperationException("routing selector control");
    }
    private sealed class RoutingResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<(SharpLinkEndpointSnapshot Snapshot, TaskCompletionSource Applied)> _updates =
            Channel.CreateUnbounded<(SharpLinkEndpointSnapshot, TaskCompletionSource)>();
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(initial);
        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update.Snapshot;
                update.Applied.TrySetResult();
            }
        }
        internal async Task PublishAsync(SharpLinkEndpointSnapshot snapshot, CancellationToken token)
        {
            var applied = Signal();
            await _updates.Writer.WriteAsync((snapshot, applied), token);
            await applied.Task.WaitAsync(token);
        }
        public ValueTask DisposeAsync() { _updates.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
