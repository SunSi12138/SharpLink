using System.Collections.Concurrent;
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
    public async Task RuntimeDynamicResolverRequiresExactPreparationAndPreservesEndpointAdmission()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = budget.Token;
        var seq = 0;
        void Record(string edge, string? endpoint = null)
            => Console.WriteLine("NMULTI " + JsonSerializer.Serialize(new { seq = ++seq, transport = "tcp", edge, endpoint }));
        await using var first = await ServerScope.StartAsync("resolver-first");
        await using var second = await ServerScope.StartAsync("resolver-second");
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(
            1,
            [Endpoint("resolver-first", first.Port), Endpoint("resolver-second", second.Port)]));
        var selector = new ObserveEndpointSelection("resolver-first");
        var admission = new ObservePreparedEndpointAdmission();
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "bootstrap",
                first.ConfigureClient,
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.StartAsync(ct);
        await client.WaitForReadyAsync("bootstrap", ct);

        var added = await client.AddClusterAsync(
            "runtime",
            child => child
                .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
                .UseEndpointSelector(selector)
                .UseEndpointAdmission(admission)
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = 2;
                    options.MaxConnections = 2;
                    options.MaxConnectionsPerEndpoint = 1;
                }), cancellationToken: ct);
        Ensure(added.Succeeded, $"the resolver-backed slot starts normally: {added.Message}");
        await client.WaitForReadyAsync("runtime", ct);
        await WaitUntilAsync(() => first.ActiveConnections == 2 && second.ActiveConnections == 1, ct);
        var proxy = client.CreateProxy<IConnectionBehaviorService>();
        Ensure(selector.Calls == 0 && admission.Attempts.IsEmpty && first.OpenBindings == 0 && second.OpenBindings == 0,
            "factory and Ready require neither endpoint selection nor Bind nor call admission");
        Record("ResolverStartsTwoLiveUnpreparedEndpoints");

        await proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        var alreadyReady = proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        Ensure(alreadyReady.IsCompletedSuccessfully && selector.Calls == 1 && selector.LastOptions == 2 &&
               admission.Attempts.IsEmpty && first.OpenBindings == 1 && second.OpenBindings == 0,
            "public Prepare uses one normal selector, no call policy and one actual binding on the chosen peer");
        await alreadyReady;
        Record("ExplicitPrepareSelectsAndBindsOnlyFirst", "resolver-first");

        admission.PauseFirst = true;
        var denied = await CaptureExceptionAsync(proxy.Service.GetEndpointIdAsync().AsTask());
        Ensure(denied is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } &&
               admission.Attempts.Count == 1 && admission.Attempts.Single().Id == "resolver-first" &&
               admission.Reports.IsEmpty && selector.Calls == 1 && second.OpenBindings == 0,
            "ordinary endpoint admission rejection does not try another Ready endpoint");
        admission.PauseFirst = false;
        Ensure(await proxy.Service.GetEndpointIdAsync() == "resolver-first", "the admitted call uses the same prepared Session");
        await WaitUntilAsync(() => admission.Reports.Count == 1, ct);
        Record("AttemptPolicyGovernsOnlyPreparedEndpoint", "resolver-first");

        resolver.Publish(new SharpLinkEndpointSnapshot(
            2,
            [Endpoint("resolver-second", second.Port)]));
        await WaitUntilAsync(() => first.ActiveConnections == 1, ct);
        var removed = await CaptureExceptionAsync(proxy.Service.GetEndpointIdAsync().AsTask());
        Ensure(removed is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } && selector.Calls == 1 &&
               admission.Attempts.Count == 2 && second.OpenBindings == 0,
            "a normally retired Session does not borrow a binding or apply its policy decision to another peer");
        Record("ResolverUpdateRetiresOldSessionWithoutReroutingCalls");

        await proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        Ensure(await proxy.Service.GetEndpointIdAsync() == "resolver-second", "the same public proxy explicitly prepares the replacement route");
        await WaitUntilAsync(() => admission.Reports.Count == 2, ct);
        var attempts = admission.Attempts.ToArray();
        var reports = admission.Reports.ToArray();
        Ensure(selector.Calls == 2 && selector.LastOptions == 1 && first.OpenBindings == 0 && second.OpenBindings == 1 &&
               attempts.Select(attempt => attempt.Id).SequenceEqual(["resolver-first", "resolver-first", "resolver-second"]) &&
               attempts[1].Generation != attempts[2].Generation &&
               reports.SequenceEqual([(attempts[1].Id, attempts[1].Generation, SharpLinkEndpointOutcomeKind.Success),
                                      (attempts[2].Id, attempts[2].Generation, SharpLinkEndpointOutcomeKind.Success)]),
            "success reports retain the actual prepared endpoint and topology generation");
        Record("ExplicitReprepareUsesSecondAndReportsItsOwnGeneration", "resolver-second");

        var removal = await client.RemoveClusterAsync("runtime", TimeSpan.FromSeconds(4), ct);
        Ensure(removal.ReferencesReleased && resolver.DisposeCount == 1 &&
               (await client.CheckHealthAsync("bootstrap", ct)).Status == SharpLinkHealthStatus.Ready,
            "removed dynamic-resolver slot must release its resolver exactly once");
        Record("NormalRemovalReleasesResolverAndLeavesBootstrapReady");
    }

    [Test]
    [NotInParallel]
    public async Task StaticEndpointPolicyUpdateDoesNotMoveThePreparedProxy()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        await using var first = await ServerScope.StartAsync("static-a");
        await using var second = await ServerScope.StartAsync("static-b");
        var initial = new ObserveEndpointSelection("static-a");
        var updated = new ObserveEndpointSelection("static-b");
        var admission = new ObservePreparedEndpointAdmission();
        await using var client = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(4))
            .UseEndpoints([Endpoint("static-a", first.Port), Endpoint("static-b", second.Port)], SharpLinkTransportFactories.Sockets())
            .UseEndpointSelector(initial).UseEndpointAdmission(admission)
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();
        var proxy = client.CreateProxy<IConnectionBehaviorService>();
        await client.ConnectAsync(ct);
        await client.WaitForReadinessAsync(2, ct);
        await proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        Ensure(initial.Calls == 1 && initial.LastOptions == 2 && first.OpenBindings == 1 && second.OpenBindings == 0 &&
               admission.Attempts.IsEmpty, "the static cluster uses its selector only for the initial Prepare");

        client.UpdateEndpointSelector(updated);
        await proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        Ensure(await proxy.Service.GetEndpointIdAsync() == "static-a", "a policy change does not migrate an already prepared proxy");
        await WaitUntilAsync(() => admission.Reports.Count == 1, ct);
        Ensure(initial.Calls == 1 && updated.Calls == 0 && first.OpenBindings == 1 && second.OpenBindings == 0 &&
               admission.Attempts.Single().Id == "static-a" && admission.Reports.Single().Id == "static-a",
            "the policy and terminal report observe only the originally prepared static endpoint");
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

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(5, ct);
    }

    private sealed class ObserveEndpointSelection(string preferred) : ISharpLinkEndpointSelector
    {
        internal int Calls { get; private set; }
        internal int LastOptions { get; private set; }

        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            Calls++;
            LastOptions = context.Count;
            var first = -1;
            for (var i = 0; i < context.Count; i++)
            {
                if ((context.ExcludedMask & (1UL << i)) != 0)
                    continue;
                if (context[i].Endpoint.Id == preferred)
                    return i;
                if (first < 0)
                    first = i;
            }
            return first;
        }
    }

    private sealed class ObservePreparedEndpointAdmission : ISharpLinkEndpointAdmissionPolicy
    {
        internal bool PauseFirst { get; set; }
        internal ConcurrentQueue<(string Id, long Generation)> Attempts { get; } = new();
        internal ConcurrentQueue<(string Id, long Generation, SharpLinkEndpointOutcomeKind Kind)> Reports { get; } = new();

        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Attempts.Enqueue((endpoint.Endpoint.Id, endpoint.Generation));
            return new(!PauseFirst || endpoint.Endpoint.Id != "resolver-first", endpoint.Generation, null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
            => Reports.Enqueue((outcome.Endpoint.Endpoint.Id, token, outcome.Kind));
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
        internal int ActiveConnections => ServerRegistryTestAccessor.ActiveConnectionCount((SharpLinkServer)_server);
        internal int OpenBindings => ServerRegistryTestAccessor.ActiveConnections((SharpLinkServer)_server).Values
            .Sum(connection => connection.NStudyBindings.Snapshot().Open);
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
