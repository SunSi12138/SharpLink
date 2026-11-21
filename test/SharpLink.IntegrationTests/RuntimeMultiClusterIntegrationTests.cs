using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

[assembly: SharpLinkClusterContractAssembly(
    "runtime",
    typeof(SharpLink.IntegrationTests.IConnectionBehaviorService))]

namespace SharpLink.IntegrationTests;

public sealed class RuntimeMultiClusterIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task RuntimeSlotShouldKeepPublicProxyOnItsCapturedChild(string transport)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = budget.Token;
        var seq = 0;
        void Record(string edge, string? endpoint = null)
            => Console.WriteLine("NMULTI " + JsonSerializer.Serialize(new { seq = ++seq, transport, edge, endpoint }));
        bool sharedMemory = transport == "shared-memory";
        await using var first = await ServerScope.StartAsync("first", sharedMemory);
        await using var second = await ServerScope.StartAsync("second", sharedMemory);
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster("bootstrap", first.ConfigureClient, slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StartAsync(ct);
        await client.WaitForReadyAsync("bootstrap", ct);
        var added = await client.AddClusterAsync("runtime", first.ConfigureClient, cancellationToken: ct);
        Ensure(added.Succeeded, $"the ordinary add publishes the first child: {added.Message}");
        await client.WaitForReadyAsync("runtime", ct);

        var old = client.CreateProxy<IConnectionBehaviorService>();
        var retained = client.CreateProxy<IConnectionBehaviorService>();
        await old.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        var original = await old.Service.GetEndpointIdAsync();
        Ensure(original == "first",
            "the published runtime route must become usable after its explicit readiness wait");
        Record("OldPublicProxyPreparesAndCallsFirstChild", original);

        var replacement = await client.ReplaceClusterAsync("runtime", second.ConfigureClient, TimeSpan.FromSeconds(4), ct);
        Ensure(replacement is { Succeeded: true, Published: true, ReferencesReleased: true, ForcedStop: false },
            $"the normal whole-slot replacement publishes then drains the old child: {replacement.Message}");

        var fresh = client.CreateProxy<IConnectionBehaviorService>();
        await fresh.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        var current = await fresh.Service.GetEndpointIdAsync();
        Ensure(current == "second",
            "a proxy created after replacement must bind to the new child");
        Record("NewPublicProxyPreparesAndCallsReplacementChild", current);
        var stalePrepare = await CaptureExceptionAsync(retained.PrepareAsync(TimeSpan.FromSeconds(4), ct).AsTask());
        var staleCall = await CaptureExceptionAsync(old.Service.GetEndpointIdAsync().AsTask());
        Ensure(stalePrepare is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } &&
               staleCall is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } &&
               await fresh.Service.GetEndpointIdAsync() == "second" &&
               (await client.CheckHealthAsync("bootstrap", ct)).Status == SharpLinkHealthStatus.Ready,
            "old handles never use the replacement, even when their first explicit Prepare comes after the swap");
        Record("SavedHandlesCannotBorrowReplacementAndBootstrapStaysReady");

        var removal = await client.RemoveClusterAsync("runtime", TimeSpan.FromSeconds(4), ct);
        Ensure(removal is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "a completed child stop must be reported as a graceful removal");
        var missingRoute = CaptureException(() => client.CreateProxy<IConnectionBehaviorService>());
        Ensure(missingRoute is InvalidOperationException &&
               (await client.CheckHealthAsync("bootstrap", ct)).Status == SharpLinkHealthStatus.Ready,
            "new proxy creation must fail immediately after the slot is removed");
        Record("NormalRemovalUnpublishesOnlyTheSelectedChild");
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeDynamicResolverShouldUpdateEndpointsWithoutReplacingTheSlot()
    {
        await using var first = await ServerScope.StartAsync("resolver-first");
        await using var second = await ServerScope.StartAsync("resolver-second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
            1,
            [Endpoint("resolver-first", first.Port)]));
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "bootstrap",
                child => child.UseTcp(IPAddress.Loopback.ToString(), first.Port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync();
        await client.WaitForReadyAsync("bootstrap").AsTask().WaitAsync(TimeSpan.FromSeconds(4));

        await client.AddClusterAsync(
            "runtime",
            child => child
                .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = 1;
                    options.MaxConnections = 1;
                    options.MaxConnectionsPerEndpoint = 1;
                }));
        await client.WaitForReadyAsync("runtime").AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        var proxy = client.Get<IConnectionBehaviorService>();
        Ensure(await proxy.GetEndpointIdAsync() == "resolver-first",
            "runtime resolver slot must use its initial endpoint");

        resolver.Publish(new SharpLinkEndpointSnapshot(
            2,
            [Endpoint("resolver-second", second.Port)]));
        await WaitUntilAsync(
            async () => await proxy.GetEndpointIdAsync() == "resolver-second",
            TimeSpan.FromSeconds(4));

        Ensure(await proxy.GetEndpointIdAsync() == "resolver-second",
            "resolver publication must update the existing slot without ReplaceClusterAsync");
        var removal = await client.RemoveClusterAsync("runtime", TimeSpan.FromSeconds(2));
        Ensure(removal.ReferencesReleased && resolver.DisposeCount == 1,
            "removed dynamic-resolver slot must release its resolver exactly once");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
        };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                    return;
            }
            catch (SharpLinkException)
            {
            }
            await Task.Delay(20);
        }

        Ensure(await condition(), "condition did not become true before the timeout");
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots =
            Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        internal void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
                _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ServerScope : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _runTask;

        private ServerScope(
            int port,
            string? memoryName,
            ISharpLinkServer server,
            CancellationTokenSource shutdown,
            Task runTask)
        {
            Port = port;
            MemoryName = memoryName;
            _server = server;
            _shutdown = shutdown;
            _runTask = runTask;
        }

        internal int Port { get; }
        private string? MemoryName { get; }

        internal void ConfigureClient(SharpClientBuilder client)
        {
            if (MemoryName is { } name)
                client.UseSharedMemory(name);
            else
                client.UseTcp(IPAddress.Loopback.ToString(), Port);
        }

        internal static Task<ServerScope> StartAsync(string endpointId, bool sharedMemory = false)
        {
            var builder = SharpLinkServerBuilder.Create()
                .ReplaceService<IConnectionBehaviorService>(
                    new ConnectionBehaviorService { EndpointId = endpointId });
            var memoryName = sharedMemory ? $"sl999-slot-{Guid.NewGuid():N}" : null;
            if (memoryName is null)
                builder.UseTcp(0, IPAddress.Loopback);
            else
                builder.UseSharedMemory(memoryName);
            var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            var server = builder.Build();
            var shutdown = new CancellationTokenSource();
            var runTask = server.RunUntilStoppedAsync(shutdown.Token).AsTask();
            return Task.FromResult(new ServerScope(port, memoryName, server, shutdown, runTask));
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            await _server.DisposeAsync();
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
    }
}
