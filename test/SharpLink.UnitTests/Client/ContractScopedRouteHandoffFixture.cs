using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Test-only, real protocol3 fixture. No all-Ready binding helper or synthetic route view.
// Original invocation launchers may append their actual Unary Task; cleanup joins/audits
// every launcher before fixing the final owner snapshot. Hooks themselves append no Tasks.
internal sealed partial class ContractScopedRouteHandoffFixture
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    // An unknown exit is not permission to release the fixture roots. The containing test
    // process must be isolated/terminated by the runner; this list is not a success path.
    private static readonly List<ContractScopedRouteHandoffFixture> Quarantined = [];
    private readonly List<OwnedOperation> _operations = [];
    private readonly object _operationsGate = new();
    private readonly List<ReservationBarrier> _barriers = [];
    private readonly CancellationTokenSource _calls = new();
    private readonly TestClientTransportFactory[] _transports =
    [
        ManualTransport(), ManualTransport(), ManualTransport()
    ];
    private readonly TestSentFrame?[] _bindFrames = new TestSentFrame?[3];
    private readonly bool[] _replyStarted = new bool[3];
    private PeerOwner[] _peers = [];
    private HandoffTransportFactory? _factory;
    private bool _initialized;
    private bool _refreshStarted;
    private bool _refreshInitialized;
    private OwnedOperation? _rollout;
    private SharpLinkClient? _client;
    private IRpcContractChannel? _channel;
    private OwnedOperation? _prepare;

    internal SharpLinkClient Client => _client ?? throw new InvalidOperationException("Client not created.");
    internal SharpLinkClient.ContractCallSite Site => (SharpLinkClient.ContractCallSite)_channel!;
    internal SharpLinkClient.ContractRouteView View => Site.CaptureContractRouteForDiagnostics();
    internal ClientConnection Source { get; private set; } = null!;
    internal ClientConnection Replacement { get; private set; } = null!;
    internal SharpLinkClient.ContractCallSite.BoundTarget OriginalTarget { get; private set; } = null!;
    internal SharpLinkClient.ContractCallSite.BoundTarget ReplacementTarget => View.Targets.Single(target => ReferenceEquals(target.Connection, Replacement));
    internal readonly List<ClientConnection> Reservations = [];
    private Action<ClientConnection>? _reservationAction;
    internal int ReservationCount { get { lock (Reservations) return Reservations.Count; } }
    internal bool SourceDisposalStarted => _peers[0].DisposeEntered.Task.IsCompleted;

    internal async Task InitializeAsync(ISharpLinkEndpointAdmissionPolicy? admission = null)
    {
        _peers = [new(_transports[0].Connection, holdDispose: true), new(_transports[1].Connection), new(_transports[2].Connection)];
        _factory = new HandoffTransportFactory(_transports[0], _transports[2], _peers[0], _peers[2]);
        _client = ClientBuilderTestHelper.BuildStatic(
            [new(new SharpLinkEndpoint { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) }, _factory),
             new(new SharpLinkEndpoint { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) },
                 new SinglePeerFactory(_transports[1], _peers[1]))],
            builder => { Configure(builder); if (admission is not null) builder.UseEndpointAdmission(admission); });
        await ObserveAsync(Own("original Connect", Client.ConnectAsync().AsTask()));
        await ObserveAsync(Own("original readiness", Client.WaitForReadinessAsync(2).AsTask()));
        Ensure(Client.GetDiagnosticSnapshot().Resources.ReadyConnections == 2 &&
               _transports[0].ConnectCount == 1 && _transports[1].ConnectCount == 1 && _transports[2].ConnectCount == 0,
            "two real initial Ready physical owners, no implicit replacement or all-bound helper");
        _channel = ClientInvokerTestHelper.CreateChannel(Client);
        _prepare = Own("original Prepare", _channel.PrepareAsync(Bound, _calls.Token).AsTask());
        CaptureFrameworkOwners("StartContractBinding", "original binding scheduler");
        foreach (var scheduler in SnapshotOperations().Where(operation => operation.Stage == "original binding scheduler"))
            await ObserveAsync(scheduler);
        for (var peer = 0; peer < 2; peer++) await ReadBindAsync(peer);
        await CaptureOriginalBindOwnersAsync();
        Ensure(!_prepare.Task.IsCompleted, "both real Bind replies start held");
        await ConfirmAsync(0);
        await ObserveAsync(_prepare);
        await AwaitAsync(() => View.Targets.Length == 1 && View.Buckets.Length == 2 && View.Buckets[0].EligibleCount() == 1,
            "A's original exact bound target was not published");
        OriginalTarget = View.Targets.Single();
        Source = OriginalTarget.Connection;
        Ensure(Source.EndpointId == "route-a" && View.Buckets[1].EligibleCount() == 0,
            "A alone is bound; B is Ready and unbound");
        _initialized = true;
        Client._callAdmissionReservedTestHook = connection =>
        {
            lock (Reservations) Reservations.Add(connection);
            _reservationAction?.Invoke(connection);
        };
    }

    internal ReservationBarrier HoldNextReservation()
    {
        var barrier = new ReservationBarrier();
        _barriers.Add(barrier);
        _reservationAction = connection =>
        {
            if (Interlocked.Exchange(ref barrier.Claimed, 1) != 0) return;
            barrier.Entered.TrySetResult(connection);
            barrier.Release.Wait(); // Owner cleanup opens this gate before joining the launcher.
        };
        return barrier;
    }

    internal OwnedOperation StartCall(string name)
        => Own(name + " launcher", Task.Factory.StartNew(() =>
            Own(name + " original Unary", ClientInvokerTestHelper.InvokeUnaryAsync(Client, cancellationToken: _calls.Token).AsTask()),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    internal async Task<OwnedOperation> OriginalCallAsync(OwnedOperation launcher)
    {
        await ObserveAsync(launcher);
        return ((Task<OwnedOperation>)launcher.Task).Result;
    }

    internal async Task CompleteCallAsync(OwnedOperation launcher, int peer, ClientConnection exactOwner, int value)
    {
        var invocation = await OriginalCallAsync(launcher);
        var reader = Own($"peer {peer} original request reader {value}",
            _transports[peer].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
        var first = await Task.WhenAny(invocation.Task, reader.Task).WaitAsync(Bound);
        if (ReferenceEquals(first, invocation.Task)) await ObserveAsync(invocation);
        await ObserveAsync(reader);
        var request = ((Task<TestSentFrame>)reader.Task).Result;
        await ObserveAsync(Own($"peer {peer} response injection {value}",
            _transports[peer].Connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), value)));
        await ObserveAsync(invocation);
        Ensure(((Task<int>)invocation.Task).Result == value, "original ordinary Unary must return its exact physical peer response");
        await AwaitAsync(() => exactOwner.ActiveCallCount == 0 && exactOwner.CallAdmissionReservationCount == 0,
            "captured exact physical response completion tail did not release its ownership");
        AssertUnboundB();
    }

    internal async Task ExpectCallFailureAsync(OwnedOperation launcher, Func<Exception, bool> expected)
        => await ExpectAsync(await OriginalCallAsync(launcher), expected);

    internal async Task RefreshWhileReplacementUnboundAsync()
    {
        _refreshStarted = true;
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Client._afterSessionRefreshEligibilitySwapTestHook = () => cut.TrySetResult();
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(payload,
            new ProtocolV2SessionRefreshRequested(_transports[0].Connection.Authority.ServerInstanceId, 2));
        var bytes = payload.WrittenMemory.ToArray();
        await ObserveAsync(Own("original refresh request injection", _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.SessionRefreshRequested, ProtocolV2FrameFlags.None, 0, bytes)));
        await _factory!.ReplacementEntered.Task.WaitAsync(Bound);
        CaptureFrameworkOwners("StaticClusterSessionRefreshRollout", "original session refresh rollout");
        _rollout = SnapshotOperations().Single(operation => operation.Stage == "original session refresh rollout");
        Ensure(!_rollout.Task.IsCompleted, "original refresh rollout is captured while its factory entry is held");
        _factory.ReleaseReplacement.TrySetResult();
        await cut.Task.WaitAsync(Bound);
        await ObserveAsync(_rollout);
        Replacement = Source.SessionRefreshRedirect?.Current ?? throw new InvalidOperationException("Exact refresh redirect missing.");
        Ensure(!ReferenceEquals(Source, Replacement) && Replacement.CanAcceptCalls && !Source.CanAcceptCalls &&
               View.Topology.Endpoints[0].Connections.All(owner => !ReferenceEquals(owner, Source)),
            "actual cut publishes replacement owner but never reclassifies old token authority");
        await ReadBindAsync(2);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            if (SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3) break;
            if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException("Replacement's original Bind Task was not registered.");
            await Task.Delay(1);
        }
        Ensure(!_replyStarted[2] && !View.HasEligibleTarget(), "replacement has no route until its own successful Bind confirmation");
        _refreshInitialized = true;
        AssertUnboundB();
    }

    internal async Task ConfirmReplacementAsync()
    {
        await ConfirmAsync(2);
        await AwaitAsync(() => View.Targets.Any(target => ReferenceEquals(target.Connection, Replacement) && target.IsEligible),
            "replacement's independent exact protocol3 target did not publish");
        Ensure(ReferenceEquals(ReplacementTarget.Connection, Replacement) &&
               !ReferenceEquals(OriginalTarget.Connection, ReplacementTarget.Connection),
            "replacement token is scoped to its own physical authority, never the source's token");
    }

    internal async Task JoinRetiredSourceAsync()
    {
        await _peers[0].DisposeEntered.Task.WaitAsync(Bound);
        var cleanup = await CaptureRetiredCleanupAsync();
        Ensure(!cleanup.Task.IsCompleted, "physical retired cleanup original must be captured before its disposal gate opens");
        CapturePeerOwners();
        _peers[0].ReleaseDispose.TrySetResult();
        await ObserveAsync(cleanup);
        Ensure(Source.CallAdmissionReservationCount == 0 && Source.ActiveCallCount == 0,
            "retired source finishes with no original charged owners");
    }

    private async Task<OwnedOperation> CaptureRetiredCleanupAsync()
    {
        OwnedOperation? cleanup = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("StaticClusterSessionRefreshRetiredConnectionCleanup", "original retired source cleanup");
            cleanup = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original retired source cleanup");
            return cleanup is not null;
        }, "Original retired source cleanup was not registered while its disposal gate was held.");
        return cleanup!;
    }

    internal async Task AssertNoRequestAsync(int peer, ClientConnection owner)
    {
        await ObserveAsync(Own("original rejected-call send fence", owner.Session.FlushSendQueueAsync().AsTask()));
        var absence = Own("original post-fence request check", _transports[peer].Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(30)));
        await ObserveAsync(absence);
        Ensure(!((Task<bool>)absence.Task).Result, "rejected reserved call cannot publish a business Request");
    }

    internal void AssertUnboundB() => Ensure(_bindFrames[1].HasValue && !_replyStarted[1],
        "B's successful real Bind reply is held for the entire body");

    private async Task ReadBindAsync(int peer)
    {
        var reader = Own($"original Bind frame reader {peer}",
            _transports[peer].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
        await ObserveAsync(reader);
        _bindFrames[peer] = ((Task<TestSentFrame>)reader.Task).Result;
        var request = Protocol3BindingControlCodec.ReadBindContract(new(_bindFrames[peer]!.Value.Payload));
        Ensure(request.ContractId == ManualClientContractFixture.ContractId && request.Hash == ManualClientContractFixture.AssemblyHash &&
               request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
            "manual peer receives only its registered exact protocol3 fixture request");
    }

    internal static async Task AwaitAsync(Func<bool> predicate, string message)
    {
        var started = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException(message);
            await Task.Delay(1);
        }
    }

    private static void Configure(SharpClientBuilder builder, bool useBuiltIn = true)
    {
        if (useBuiltIn) builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
        builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
            options.MaxRetiringConnections = 1;
        });
    }

    private async Task ConfirmAsync(int index)
    {
        var frame = _bindFrames[index] ?? throw new InvalidOperationException("Bind request was not observed.");
        var authority = _transports[index].Connection.Authority;
        using var payload = new PooledByteBufferWriter();
        Protocol3BindingControlCodec.WriteBindResult(payload, new Protocol3BindResult(
            Protocol3BindStatus.Success, Protocol3BindingToken.Create(0, 1, authority.Limits.Slots),
            ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash,
            1, 1, authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
        // The original injection may outlive our bounded observer while waiting for the
        // peer write gate. Never return pooled memory that its source task still owns.
        var bytes = payload.WrittenMemory.ToArray();
        Ensure(!_replyStarted[index], "an original BindResult injection cannot be retried on an unknown outcome");
        _replyStarted[index] = true;
        var injection = Own($"BindResult injection {index}", _transports[index].Connection.InjectFrameAsync(
            ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, frame.Header.RequestId, bytes));
        await ObserveAsync(injection);
    }

    private async Task CaptureOriginalBindOwnersAsync()
    {
        var supervisor = Read<FrameworkTaskSupervisor>(_client!, "_frameworkTasks");
        var gate = Read<Lock>(supervisor, "_gate");
        var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task[] original;
            lock (gate)
                original = active.Where(pair => pair.Value.Operation == "ContractBinding")
                    .Select(pair => pair.Key).ToArray();
            if (original.Length == 2)
            {
                foreach (var task in original) Own("original BindContractDemand", task);
                return;
            }
            Ensure(original.Length < 2, "fixture must not admit additional physical Bind owners");
            if (Stopwatch.GetElapsedTime(started) >= Bound)
                throw new TimeoutException("Both initial pending original BindContractDemand tasks were not registered.");
            await Task.Delay(1);
        }
    }

    internal async Task CleanupAsync(Exception? primary)
    {
        List<Exception> failures = [];
        if (primary is not null) failures.Add(primary);
        foreach (var barrier in _barriers) barrier.Release.Set();
        _factory?.ReleaseReplacement.TrySetResult();
        if (_client is not null)
        {
            Client._callAdmissionReservedTestHook = null;
            Client._afterSessionRefreshEligibilitySwapTestHook = null;
        }
        var launchers = SnapshotOperations().Where(operation => operation.Stage.EndsWith(" launcher", StringComparison.Ordinal)).ToArray();
        foreach (var launcher in launchers) await JoinAsync(launcher, failures);
        if (launchers.Any(operation => !operation.Task.IsCompleted))
        {
            await QuarantineAsync(failures, "Original invocation launcher exit unknown; actual Task list is not closed.");
            ThrowFailures(failures);
            return;
        }
        foreach (var launcher in launchers) await JoinAsync(launcher, failures);
        if (_client is not null && (!_initialized || _refreshStarted && !_refreshInitialized))
        {
            await QuarantineAsync(failures, "Manual Bind/refresh setup producer graph is not closed.");
            ThrowFailures(failures);
            return;
        }
        if (_client is not null)
        {
            try { CaptureFrameworkOwners("StartContractBinding", "original binding scheduler"); }
            catch (Exception error) { failures.Add(error); }
            var producers = SnapshotOperations().Where(operation => operation.Stage is "original binding scheduler" or "original session refresh rollout").ToArray();
            foreach (var producer in producers) await JoinAsync(producer, failures);
            if (producers.Any(operation => !operation.Task.IsCompleted))
            {
                await QuarantineAsync(failures, "Original cold producer exit unknown.");
                ThrowFailures(failures);
                return;
            }
            foreach (var producer in producers) await JoinAsync(producer, failures);
            try { CaptureFrameworkOwners("ContractBinding", "original BindContractDemand"); }
            catch (Exception error) { failures.Add(error); }
        }
        // Close pending invocations only after their launchers have returned their
        // original Tasks. Keep the source disposal gate until its original queued cleanup
        // is retained; opening it before this capture could lose a fast terminal task.
        try { _calls.Cancel(); } catch (Exception error) { failures.Add(error); }
        foreach (var invocation in SnapshotOperations().Where(operation => operation.Stage.EndsWith(" original Unary", StringComparison.Ordinal)))
            await JoinAsync(invocation, failures);
        if (_refreshInitialized && !SnapshotOperations().Any(operation => operation.Stage == "original retired source cleanup"))
        {
            try
            {
                await _peers[0].DisposeEntered.Task.WaitAsync(Bound);
                await CaptureRetiredCleanupAsync();
            }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var peer in _peers) peer.ReleaseDispose.TrySetResult();
        for (var peer = 0; peer < 3; peer++)
        {
            if (!_bindFrames[peer].HasValue || _replyStarted[peer]) continue;
            try { await ConfirmAsync(peer); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var binding in SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand"))
            await JoinAsync(binding, failures);
        try { _calls.Cancel(); } catch (Exception error) { failures.Add(error); }
        OwnedOperation? stop = null;
        if (_client is not null)
        {
            try { stop = Own("original Stop", Client.StopAsync().AsTask()); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var peer in _peers)
        {
            try { OwnOnce("original physical transport Dispose", peer.DisposeAsync().AsTask()); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var operation in SnapshotOperations()) await JoinAsync(operation, failures);
        if (_client is not null && stop is { Task.IsCompleted: true })
        {
            try { await JoinAsync(Own("original client Dispose", Client.DisposeAsync().AsTask()), failures); }
            catch (Exception error) { failures.Add(error); }
        }
        CapturePeerOwners();
        CaptureFactoryOwners();
        foreach (var operation in SnapshotOperations()) await JoinAsync(operation, failures);
        var finalOwners = SnapshotOperations();
        if (finalOwners.Any(operation => !operation.Task.IsCompleted) ||
            Source is not null && (Source.ActiveCallCount != 0 || Source.CallAdmissionReservationCount != 0) ||
            Replacement is not null && (Replacement.ActiveCallCount != 0 || Replacement.CallAdmissionReservationCount != 0))
        {
            await QuarantineAsync(failures, "Original task exit or captured physical ownership remains unresolved.");
        }
        else
        {
            foreach (var operation in finalOwners) await JoinAsync(operation, failures);
            foreach (var barrier in _barriers)
                try { barrier.Release.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { _calls.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        foreach (var operation in SnapshotOperations().Where(operation => operation.Task.IsCompleted))
            await JoinAsync(operation, failures);
        ThrowFailures(failures);
    }

    private async Task QuarantineAsync(List<Exception> failures, string reason)
    {
        foreach (var barrier in _barriers) barrier.Release.Set();
        _factory?.ReleaseReplacement.TrySetResult();
        foreach (var peer in _peers) peer.ReleaseDispose.TrySetResult();
        lock (Quarantined) Quarantined.Add(this);
        failures.Add(new InvalidOperationException(reason + " Roots retained; isolate/terminate this test process."));
        if (_client is not null)
        {
            try
            {
                var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
                var gate = Read<Lock>(supervisor, "_gate");
                var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
                lock (gate)
                    foreach (var pair in active)
                        if (!SnapshotOperations().Any(operation => ReferenceEquals(operation.Task, pair.Key)))
                            Own("unclosed original " + pair.Value.Operation, pair.Key);
            }
            catch (Exception error) { failures.Add(error); }
        }
        CapturePeerOwners();
        CaptureFactoryOwners();
        foreach (var operation in SnapshotOperations().Where(operation => operation.Task.IsCompleted))
            await JoinAsync(operation, failures);
    }

    private void CapturePeerOwners()
    {
        foreach (var peer in _peers)
            foreach (var (stage, task) in peer.Tasks()) OwnOnce(stage, task);
    }

    private void CaptureFactoryOwners()
    {
        if (_factory is not null)
            foreach (var task in _factory.Tasks()) OwnOnce("original refresh factory Connect", task);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Body first, then independent original owner failures.", failures);
    }

    private void CaptureFrameworkOwners(string operationName, string stage)
    {
        var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
        var gate = Read<Lock>(supervisor, "_gate");
        var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
        lock (gate)
            foreach (var pair in active)
                if (pair.Value.Operation == operationName &&
                    !SnapshotOperations().Any(operation => operation.Stage == stage && ReferenceEquals(operation.Task, pair.Key)))
                    Own(stage, pair.Key);
    }

    internal OwnedOperation Own(string stage, Task task)
    {
        var operation = new OwnedOperation(stage, task);
        lock (_operationsGate) _operations.Add(operation);
        return operation;
    }

    private void OwnOnce(string stage, Task task)
    {
        lock (_operationsGate)
            if (!_operations.Any(operation => operation.Stage == stage && ReferenceEquals(operation.Task, task)))
                _operations.Add(new(stage, task));
    }

    private OwnedOperation[] SnapshotOperations() { lock (_operationsGate) return _operations.ToArray(); }

    private static async Task ObserveAsync(OwnedOperation operation)
    {
        try { await operation.Task.WaitAsync(Bound); }
        catch (Exception observerFailure)
        {
            if (!operation.Task.IsCompleted) throw;
            var sourceFailure = await CaptureTerminalFailureAsync(operation.Task);
            if (sourceFailure is null) throw; // The bounded observation still failed.
            operation.Reported = true;
            if (IsObserverTimeout(operation.Task, observerFailure))
                throw new AggregateException("Observation timeout, then actual original source outcome.",
                    observerFailure, sourceFailure);
            ExceptionDispatchInfo.Capture(sourceFailure).Throw();
            throw;
        }
    }

    private static async Task ExpectAsync(OwnedOperation operation, Func<Exception, bool> expected)
    {
        try { await operation.Task.WaitAsync(Bound); }
        catch (Exception observerFailure)
        {
            if (!operation.Task.IsCompleted) throw; // Unknown source stays owned for cleanup/quarantine.
            var sourceFailure = await CaptureTerminalFailureAsync(operation.Task);
            if (sourceFailure is null) throw; // A late success cannot erase the observation timeout.
            var sourceGraph = operation.Task.Exception;
            var sourceRoot = sourceGraph is { InnerExceptions.Count: 1 }
                ? sourceGraph.InnerExceptions[0] : sourceFailure;
            Exception? validationFailure = null;
            try
            {
                Ensure(sourceGraph is null || sourceGraph.InnerExceptions.Count == 1,
                    "an expected outcome cannot hide sibling roots or an empty source graph");
                Ensure(sourceRoot is not AggregateException { InnerExceptions.Count: 0 },
                    "an empty aggregate is not an expected source outcome");
                Ensure(expected(sourceRoot), $"Unexpected original outcome at {operation.Stage}.");
            }
            catch (Exception validation) { validationFailure = validation; }
            operation.Reported = true; // Every rejected terminal graph is included below.
            var timedOut = IsObserverTimeout(operation.Task, observerFailure);
            if (validationFailure is not null)
                throw new AggregateException($"Original outcome validation failed at {operation.Stage}.",
                    timedOut ? new[] { observerFailure, sourceFailure, validationFailure } : new[] { sourceFailure, validationFailure });
            if (timedOut)
                throw new AggregateException($"Observation timeout, then actual original outcome at {operation.Stage}.",
                    observerFailure, sourceFailure);
            return;
        }
        throw new InvalidOperationException($"Expected failure at {operation.Stage}.");
    }

    private static async Task JoinAsync(OwnedOperation operation, List<Exception> failures)
    {
        try { await operation.Task.WaitAsync(Bound); }
        catch (Exception observerFailure)
        {
            if (operation.Reported) return;
            if (!operation.Task.IsCompleted)
            {
                failures.Add(new TimeoutException($"Original owner '{operation.Stage}' has not exited.", observerFailure));
                return; // A timeout is not a join and does not consume its later outcome.
            }
            // Re-await the original, now-terminal Task, not its timed observation proxy.
            // A cancellation racing the timeout has no Task.Exception container.
            var sourceFailure = await CaptureTerminalFailureAsync(operation.Task);
            if (sourceFailure is null || IsObserverTimeout(operation.Task, observerFailure))
                failures.Add(observerFailure);
            if (sourceFailure is null) return;
            operation.Reported = true;
            failures.Add(sourceFailure);
        }
    }

    private static async Task<Exception?> CaptureTerminalFailureAsync(Task source)
    {
        Ensure(source.IsCompleted, "terminal audit cannot silently become an unbounded join");
        try { await source; return null; }
        catch (Exception original) { return source.Exception is { } graph ? graph : original; }
    }

    private static bool IsObserverTimeout(Task source, Exception error)
        => error is TimeoutException &&
           !(source.Exception?.InnerExceptions.Any(original => ReferenceEquals(original, error)) ?? false);

    internal sealed class OwnedOperation(string stage, Task task)
    {
        internal string Stage { get; } = stage;
        internal Task Task { get; } = task;
        internal bool Reported;
    }

    internal static T Read<T>(object owner, string field)
        => (T)(owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing exact cold owner field {owner.GetType().Name}.{field}."));

    internal sealed class ReservationBarrier
    {
        internal readonly TaskCompletionSource<ClientConnection> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Claimed;
        internal async Task<ClientConnection> WaitAsync() => await Entered.Task.WaitAsync(Bound);
    }

    private sealed class PeerOwner(TestTransportConnection inner, bool holdDispose = false) : ITransportConnection
    {
        private readonly object _gate = new();
        private Task? _dispose;
        private Task? _innerDispose;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = Signal(holdDispose);
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output => inner.Output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync()
        {
            lock (_gate) return new(_dispose ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task;
            Task original;
            lock (_gate) _innerDispose = original = inner.DisposeAsync().AsTask();
            await original;
        }
        internal (string Stage, Task Task)[] Tasks()
        {
            lock (_gate)
            {
                List<(string, Task)> tasks = [];
                if (_dispose is not null) tasks.Add(("original physical transport Dispose", _dispose));
                if (_innerDispose is not null) tasks.Add(("original peer pipeline Dispose", _innerDispose));
                return tasks.ToArray();
            }
        }
        private static TaskCompletionSource Signal(bool hold)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!hold) signal.TrySetResult();
            return signal;
        }
    }

    private sealed class SinglePeerFactory(TestClientTransportFactory peer, PeerOwner owner) : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await peer.ConnectAsync(cancellationToken);
            return owner;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Fixture owns peer resources independently.
    }

    private sealed class HandoffTransportFactory(TestClientTransportFactory source, TestClientTransportFactory replacement,
        PeerOwner sourceOwner, PeerOwner replacementOwner) : IClientTransportFactory
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private int _count;
        internal TaskCompletionSource ReplacementEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReplacement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _count) - 1;
            if (index > 1) throw new InvalidOperationException("Unexpected extra source/replacement connection.");
            var task = ConnectCoreAsync(index, cancellationToken);
            lock (_gate) _tasks.Add(task);
            return new(task);
        }
        private async Task<ITransportConnection> ConnectCoreAsync(int index, CancellationToken token)
        {
            if (index == 1)
            {
                ReplacementEntered.TrySetResult();
                await ReleaseReplacement.Task.WaitAsync(token);
            }
            var task = (index == 0 ? source : replacement).ConnectAsync(token).AsTask();
            lock (_gate) _tasks.Add(task);
            await task;
            return index == 0 ? sourceOwner : replacementOwner;
        }
        internal Task[] Tasks() { lock (_gate) return _tasks.ToArray(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Independent captured peer Dispose roots.
    }

    private static TestClientTransportFactory ManualTransport()
        => new(contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
            acceptManualContractBindings: false);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
