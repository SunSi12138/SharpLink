using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class ContractScopedRouteGenerationControls
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolverGenerationAndAttributes(bool attributesOnly)
    {
        var fixture = new ContractScopedRouteHandoffFixture();
        var admission = new GenerationAdmission();
        Exception? primary = null;
        try
        {
            await fixture.InitializeDynamicAsync(admission);
            var before = fixture.View;
            var original = fixture.Source;
            var generation = before.Candidates[0].Generation;
            if (attributesOnly)
            {
                await fixture.ApplyAttributesOnlyAsync();
                var after = fixture.View;
                Ensure(after.Topology.Revision > before.Topology.Revision &&
                       !ReferenceEquals(after.Topology.IndexMap, before.Topology.IndexMap) &&
                       after.Candidates[0].Generation == generation &&
                       ReferenceEquals(after.Topology.Endpoints[0].Connections.Single(), original) &&
                       ReferenceEquals(after.Targets.Single(), fixture.OriginalTarget) &&
                       !ReferenceEquals(after.Candidates[0].Endpoint, before.Candidates[0].Endpoint) &&
                       after.Candidates[0].Endpoint.Attributes["revision"] == "after" &&
                       before.Candidates[0].Endpoint.Attributes["revision"] == "before" && fixture.GenerationFactoryCount == 2,
                    "attribute-only publication changes real endpoint/index identity but preserves generation, physical owner and binding");
                await fixture.CompleteCallAsync(fixture.StartCall("attribute-only actual call"), 0, original, 1700);
                Ensure(!fixture.SourceDisposalStarted, "attribute-only identity change cannot retire the same physical generation");
            }
            else
            {
                fixture.PinOriginalGeneration();
                await fixture.RemoveAndReaddSameEndpointAsync();
                var current = fixture.View;
                Ensure(!ReferenceEquals(current.Topology.IndexMap, before.Topology.IndexMap) &&
                       current.Candidates[0].Generation != generation && current.Candidates[0].Generation == fixture.Replacement.EndpointGeneration &&
                       current.Candidates[0].Endpoint.Id == before.Candidates[0].Endpoint.Id &&
                       Equals(current.Candidates[0].Endpoint.Address, before.Candidates[0].Endpoint.Address) &&
                       current.Candidates[0].Endpoint.Attributes["revision"] == "after" &&
                       original.EndpointGeneration == generation && original.State == ClientConnectionState.Draining &&
                       original.ActiveCallCount == 1 && original.Session.IsConnected && !fixture.SourceDisposalStarted &&
                       current.Topology.Endpoints.All(endpoint => endpoint.Connections.All(owner => !ReferenceEquals(owner, original))) &&
                       current.Targets.All(target => !ReferenceEquals(target.Connection, original)),
                    "same textual endpoint's new generation owns new authority; live old bound physical owner is never reclassified");
                var reservations = fixture.ReservationCount;
                await fixture.ExpectCallFailureAsync(fixture.StartCall("new generation still unbound"), error => error is SharpLinkException
                {
                    Code: SharpLinkErrorCode.Unavailable,
                    DetailCode: SharpLinkErrorDetails.Unavailable.NoBoundRoute
                });
                Ensure(fixture.ReservationCount == reservations && admission.Candidates.Length == 0,
                    "old generation's token cannot provide admission or physical reservation while new generation Bind stays held");
                await fixture.ConfirmReplacementAsync();
                await fixture.CompleteCallAsync(fixture.StartCall("own new-generation binding"), 2, fixture.Replacement, 1701);
                Ensure(ReferenceEquals(fixture.ReplacementTarget.Connection, fixture.Replacement) &&
                       fixture.ReplacementTarget.IsEligible && !fixture.OriginalTarget.IsEligible,
                    "new generation becomes callable only through its own exact confirmed physical target");
                await fixture.ReleaseRetiredGenerationAsync();
            }
            var candidates = admission.Candidates;
            Ensure(candidates.Length == 1 && candidates[0].Endpoint.Attributes["revision"] == "after" &&
                   candidates[0].Generation == fixture.View.Candidates[0].Generation && admission.Successes == 1,
                "the actual admission and terminal outcome observe the current frozen endpoint attributes/generation");
        }
        catch (Exception error) { primary = error; }
        await fixture.CleanupGenerationAsync(primary);
    }

    private sealed class GenerationAdmission : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly object _gate = new();
        private readonly List<SharpLinkEndpointCandidate> _candidates = [];
        private int _successes;
        internal SharpLinkEndpointCandidate[] Candidates { get { lock (_gate) return _candidates.ToArray(); } }
        internal int Successes { get { lock (_gate) return _successes; } }
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            lock (_gate) { _candidates.Add(endpoint); return new(true, 401, null); }
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            lock (_gate)
                if (outcome.Kind == SharpLinkEndpointOutcomeKind.Success && outcome.ResponseObserved && token == 401) _successes++;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed partial class ContractScopedRouteHandoffFixture
{
    private GenerationFactory[] _generationFactories = [];
    private int _generationFactoryCount;
    private bool _generationPinned;
    private bool _generationRemoved;
    internal int GenerationFactoryCount => Volatile.Read(ref _generationFactoryCount);

    private static SharpLinkEndpoint GenerationEndpoint(string id, string revision) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", id == "route-a" ? 19001 : 19002),
        Attributes = new Dictionary<string, string> { ["revision"] = revision }
    };

    internal async Task InitializeDynamicAsync(ISharpLinkEndpointAdmissionPolicy admission)
    {
        _peers = [new(_transports[0].Connection, holdDispose: true), new(_transports[1].Connection), new(_transports[2].Connection)];
        _generationFactories =
        [
            new(_transports[0], _peers[0], holdConnect: false, holdDispose: true),
            new(_transports[1], _peers[1], holdConnect: false, holdDispose: false),
            new(_transports[2], _peers[2], holdConnect: true, holdDispose: false)
        ];
        var aCreated = 0;
        _client = ClientBuilderTestHelper.BuildDynamic(new GenerationResolver(new(1,
            [GenerationEndpoint("route-a", "before"), GenerationEndpoint("route-b", "before")])), endpoint =>
        {
            Interlocked.Increment(ref _generationFactoryCount);
            if (endpoint.Id == "route-b") return _generationFactories[1];
            return Interlocked.Increment(ref aCreated) == 1 ? _generationFactories[0] : _generationFactories[2];
        }, builder => { Configure(builder); builder.UseEndpointAdmission(admission); });
        await ObserveAsync(Own("original dynamic Connect", Client.ConnectAsync().AsTask()));
        await ObserveAsync(Own("original dynamic readiness", Client.WaitForReadinessAsync(2).AsTask()));
        CaptureFrameworkOwners("DynamicClusterTopologyResolver", "original resolver worker");
        _channel = ClientInvokerTestHelper.CreateChannel(Client);
        _prepare = Own("original dynamic Prepare", _channel.PrepareAsync(Bound, _calls.Token).AsTask());
        CaptureFrameworkOwners("StartContractBinding", "original binding scheduler");
        foreach (var scheduler in SnapshotOperations().Where(operation => operation.Stage == "original binding scheduler"))
            await ObserveAsync(scheduler);
        await ReadBindAsync(0);
        await ReadBindAsync(1);
        await CaptureOriginalBindOwnersAsync();
        await ConfirmAsync(0);
        await ObserveAsync(_prepare);
        await AwaitAsync(() => View.Targets.Length == 1 && View.Buckets.Length == 2 && View.Buckets[0].EligibleCount() == 1,
            "initial dynamic exact target did not publish");
        OriginalTarget = View.Targets.Single();
        Source = OriginalTarget.Connection;
        Ensure(Source.EndpointId == "route-a" && Source.EndpointGeneration == View.Candidates[0].Generation &&
               _transports[0].ConnectCount == 1 && _transports[1].ConnectCount == 1 && _transports[2].ConnectCount == 0,
            "initial dynamic generation and real physical owner are exact");
        CaptureGenerationTasks();
        _initialized = true;
        Client._callAdmissionReservedTestHook = connection => { lock (Reservations) Reservations.Add(connection); };
    }

    internal async Task ApplyAttributesOnlyAsync()
    {
        _initialized = false;
        await ApplyGenerationSnapshotAsync(new(2,
            [GenerationEndpoint("route-a", "after"), GenerationEndpoint("route-b", "before")]));
        _initialized = true;
    }

    internal void PinOriginalGeneration()
    {
        Ensure(Source.TryReserveExactCallAdmission(), "pin must own exact source reservation");
        Ensure(Source.TryBeginUntrackedCall(), "pin transfers that original reservation to one real active owner");
        _generationPinned = true;
        Ensure(Source.ActiveCallCount == 1 && Source.CallAdmissionReservationCount == 0,
            "old generation remains physically charged without a synthetic load provider");
    }

    internal async Task RemoveAndReaddSameEndpointAsync()
    {
        _initialized = false;
        await ApplyGenerationSnapshotAsync(new(2, [GenerationEndpoint("route-b", "before")]));
        _generationRemoved = true;
        Ensure(Source.State == ClientConnectionState.Draining && Source.ActiveCallCount == 1 &&
               !_generationFactories[0].DisposeEntered.Task.IsCompleted,
            "removal retains the actually charged old physical generation and its factory");
        await ApplyGenerationSnapshotAsync(new(3,
            [GenerationEndpoint("route-a", "after"), GenerationEndpoint("route-b", "before")]));
        await _generationFactories[2].ConnectEntered.Task.WaitAsync(Bound);
        OwnedOperation? reconnect = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("DynamicClusterReconnect", "original new-generation reconnect");
            reconnect = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original new-generation reconnect" && !operation.Task.IsCompleted);
            return reconnect is not null;
        }, "new generation's original reconnect Task was not captured while factory entry was held");
        _generationFactories[2].ReleaseConnect.TrySetResult();
        await ObserveAsync(reconnect!);
        await AwaitAsync(() => View.Topology.Endpoints.Length == 2 && View.Topology.Endpoints[0].Endpoint.Id == "route-a",
            "new generation physical topology did not publish");
        Replacement = View.Topology.Endpoints[0].Connections.Single();
        await ReadBindAsync(2);
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            return SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3;
        }, "new generation's original Bind Task was not captured while its reply was held");
        CaptureGenerationTasks();
        Ensure(GenerationFactoryCount == 3 && _transports[2].ConnectCount == 1 && !View.HasEligibleTarget() && !_replyStarted[2],
            "same textual endpoint owns a new factory/physical generation with no implicit binding confirmation");
        _initialized = true;
    }

    private async Task ApplyGenerationSnapshotAsync(SharpLinkEndpointSnapshot snapshot)
    {
        var launcher = Own("dynamic snapshot invocation launcher", Task.Factory.StartNew(() =>
        {
            var cluster = Read<object>(Client, "_cluster");
            var method = cluster.GetType().GetMethod("ApplySnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Actual dynamic ApplySnapshotAsync not found.");
            var original = (Task<bool>)(method.Invoke(cluster, [snapshot, false])
                ?? throw new InvalidOperationException("Actual resolver application returned no original Task."));
            return Own("original dynamic ApplySnapshotAsync", original);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        await ObserveAsync(launcher);
        var apply = ((Task<OwnedOperation>)launcher.Task).Result;
        await ObserveAsync(apply);
        Ensure(((Task<bool>)apply.Task).Result, "the original actual resolver snapshot application must accept this version");
    }

    internal async Task ReleaseRetiredGenerationAsync()
    {
        if (_generationPinned)
        {
            _generationPinned = false;
            Source.EndUntrackedCall();
        }
        if (!_generationRemoved) return;
        await _peers[0].DisposeEntered.Task.WaitAsync(Bound);
        await _generationFactories[0].DisposeEntered.Task.WaitAsync(Bound);
        OwnedOperation? cleanup = null;
        OwnedOperation? release = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("DynamicClusterIdleConnectionCleanup", "original retired-generation physical cleanup");
            CaptureFrameworkOwners("DynamicClusterRetiredTopologyRelease", "original retired-generation factory release");
            cleanup = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original retired-generation physical cleanup");
            release = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original retired-generation factory release");
            return cleanup is not null && release is not null;
        }, "original retired generation cleanup/release Tasks were not retained while their physical gates were held");
        CapturePeerOwners();
        CaptureGenerationTasks();
        _peers[0].ReleaseDispose.TrySetResult();
        _generationFactories[0].ReleaseDispose.TrySetResult();
        await ObserveAsync(cleanup!);
        await ObserveAsync(release!);
        Ensure(Source.ActiveCallCount == 0 && Source.CallAdmissionReservationCount == 0,
            "retired original physical generation leaves no charged owner");
        _generationRemoved = false;
    }

    internal async Task CleanupGenerationAsync(Exception? primary)
    {
        List<Exception> failures = [];
        foreach (var factory in _generationFactories) factory.ReleaseConnect.TrySetResult();
        try { if (_generationPinned || _generationRemoved) await ReleaseRetiredGenerationAsync(); }
        catch (Exception error) { failures.Add(error); _initialized = false; }
        foreach (var factory in _generationFactories) factory.ReleaseDispose.TrySetResult();
        CaptureGenerationTasks();
        try { await CleanupAsync(primary); }
        catch (Exception error) { failures.Insert(0, error); }
        // Current generation factory Dispose tasks may have been created by original Stop/
        // client Dispose. Capture and join them after those producer owners have terminated;
        // an unknown producer is already quarantined by the base owner protocol.
        CaptureGenerationTasks();
        foreach (var operation in SnapshotOperations().Where(operation => operation.Stage == "original dynamic factory owner"))
            await JoinAsync(operation, failures);
        if (SnapshotOperations().Any(operation => !operation.Task.IsCompleted))
            await QuarantineAsync(failures, "Dynamic generation original task graph remains unresolved.");
        foreach (var operation in SnapshotOperations().Where(operation => operation.Task.IsCompleted))
            await JoinAsync(operation, failures);
        ThrowFailures(failures);
    }

    private void CaptureGenerationTasks()
    {
        foreach (var factory in _generationFactories)
            foreach (var task in factory.Tasks()) OwnOnce("original dynamic factory owner", task);
    }

    private sealed class GenerationFactory(TestClientTransportFactory transport, PeerOwner peer, bool holdConnect, bool holdDispose)
        : IClientTransportFactory
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private Task? _dispose;
        internal TaskCompletionSource ConnectEntered { get; } = Signal();
        internal TaskCompletionSource ReleaseConnect { get; } = Signal(!holdConnect);
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource ReleaseDispose { get; } = Signal(!holdDispose);
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var task = ConnectCoreAsync(cancellationToken);
            lock (_gate) _tasks.Add(task);
            return new(task);
        }
        private async Task<ITransportConnection> ConnectCoreAsync(CancellationToken token)
        {
            ConnectEntered.TrySetResult();
            await ReleaseConnect.Task.WaitAsync(token);
            var child = transport.ConnectAsync(token).AsTask();
            lock (_gate) _tasks.Add(child);
            await child;
            return peer;
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_dispose is null) { _dispose = DisposeCoreAsync(); _tasks.Add(_dispose); }
                return new(_dispose);
            }
        }
        private async Task DisposeCoreAsync() { DisposeEntered.TrySetResult(); await ReleaseDispose.Task; }
        internal Task[] Tasks() { lock (_gate) return _tasks.ToArray(); }
        private static TaskCompletionSource Signal(bool open = false)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (open) signal.TrySetResult();
            return signal;
        }
    }

    private sealed class GenerationResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(initial);
        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
