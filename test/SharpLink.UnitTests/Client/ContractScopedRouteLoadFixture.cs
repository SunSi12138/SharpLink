using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Test-only, real protocol3 fixture. No all-Ready binding helper or synthetic route view.
// Foreground alone appends fixture Tasks; finite factory task arrays are joined before their child snapshot.
// Expansion is deliberately triggered through the existing production Selected delegate, not a business RPC.
internal sealed class ContractScopedRouteLoadFixture
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    // An unknown exit is not permission to release the fixture roots. The containing test
    // process must be isolated/terminated by the runner; this list is not a success path.
    private static readonly List<ContractScopedRouteLoadFixture> Quarantined = [];
    private readonly List<OwnedOperation> _operations = [];
    private readonly CancellationTokenSource _calls = new();
    private readonly TestClientTransportFactory[] _transports =
    [
        ManualTransport(), ManualTransport(), ManualTransport(), ManualTransport(), ManualTransport()
    ];
    private readonly TestSentFrame?[] _bindFrames = new TestSentFrame?[5];
    private readonly bool[] _replyStarted = new bool[5];
    private readonly ClientConnection?[] _owners = new ClientConnection?[5];
    private readonly int[] _loads = new int[5];
    private ExpansionTransportFactory? _expansionFactory;
    private SharpLinkClient? _client;
    private IRpcContractChannel? _channel;
    private OwnedOperation? _prepare;
    private bool _initialized;

    internal SharpLinkClient Client => _client ?? throw new InvalidOperationException("Client not created.");
    internal SharpLinkClient.ContractCallSite Site => (SharpLinkClient.ContractCallSite)_channel!;
    internal SharpLinkClient.ContractRouteView View => Site.CaptureContractRouteForDiagnostics();
    internal ClientConnection Owner(int peerIndex) => _owners[peerIndex] ?? throw new InvalidOperationException("Physical owner not captured.");
    internal readonly List<ClientConnection> Reservations = [];

    internal async Task InitializeAsync(SharpLinkLoadBalancingStrategy strategy,
        ISharpLinkEndpointAdmissionPolicy? admission = null, ISharpLinkEndpointSelector? selector = null)
    {
        SharpLinkEndpoint[] endpoints =
        [
            new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001), Attributes = new Dictionary<string, string> { ["zone"] = "a" } },
            new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002), Attributes = new Dictionary<string, string> { ["zone"] = "b" } },
            new() { Id = "route-c", Address = new SharpLinkTcpAddress("127.0.0.1", 19003), Attributes = new Dictionary<string, string> { ["zone"] = "c" } }
        ];
        _expansionFactory = new ExpansionTransportFactory([_transports[0], _transports[3], _transports[4]]);
        _client = ClientBuilderTestHelper.BuildStatic(
            [new(endpoints[0], _expansionFactory), new(endpoints[1], _transports[1]), new(endpoints[2], _transports[2])],
            builder =>
            {
                Configure(builder);
                if (selector is null) builder.UseLoadBalancing(strategy);
                else builder.UseEndpointSelector(selector);
                if (admission is not null) builder.UseEndpointAdmission(admission);
            });
        await ObserveAsync(Own("connect", Client.ConnectAsync().AsTask()));
        await ObserveAsync(Own("transport-readiness", Client.WaitForReadinessAsync(3).AsTask()));
        Ensure(Client.GetDiagnosticSnapshot().Resources.ReadyConnections == 3 &&
               _transports.Take(3).All(transport => transport.ConnectCount == 1),
            "exactly three real Ready physical owners are required");
        _channel = ClientInvokerTestHelper.CreateChannel(Client);
        _prepare = Own("original Prepare", _channel.PrepareAsync(Bound, _calls.Token).AsTask());
        CaptureFrameworkOwners("StartContractBinding", "original binding scheduler");
        foreach (var scheduler in _operations.Where(operation => operation.Stage == "original binding scheduler").ToArray())
            await ObserveAsync(scheduler);
        for (var index = 0; index < 3; index++)
        {
            var reader = Own($"Bind frame reader {index}",
                _transports[index].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
            await ObserveAsync(reader);
            _bindFrames[index] = ((Task<TestSentFrame>)reader.Task).Result;
            var request = Protocol3BindingControlCodec.ReadBindContract(new(_bindFrames[index]!.Value.Payload));
            Ensure(request.ContractId == ManualClientContractFixture.ContractId &&
                   request.Hash == ManualClientContractFixture.AssemblyHash &&
                   request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
                "manual peer received the exact registered protocol3 fixture identity");
        }
        await CaptureOriginalBindOwnersAsync();
        Ensure(!_prepare.Task.IsCompleted, "transport readiness alone cannot complete Prepare");
        await ConfirmAsync(0);
        await ConfirmAsync(2);
        await ObserveAsync(_prepare);
        var started = Stopwatch.GetTimestamp();
        while (View.Buckets.Length != 3 || View.Buckets[0].EligibleCount() != 1 || View.Buckets[2].EligibleCount() != 1)
        {
            if (Stopwatch.GetElapsedTime(started) >= Bound)
                throw new TimeoutException("Both selected real protocol3 bindings were not published.");
            await Task.Delay(1);
        }
        Ensure(View.Topology.Endpoints.Select(endpoint => endpoint.Endpoint.Id)
                   .SequenceEqual(new[] { "route-a", "route-b", "route-c" }) &&
               View.Buckets[1].EligibleCount() == 0 && !_replyStarted[1],
            "the unbound Ready endpoint retains its middle index; no compacted candidate array");
        foreach (var connection in View.Topology.Endpoints.SelectMany(endpoint => endpoint.Connections))
            Ensure(connection.State == ClientConnectionState.Ready && connection.CanAcceptCalls,
                "all three exact physical owners must really be Ready before control cuts");
        for (var index = 0; index < 3; index++) _owners[index] = View.Topology.Endpoints[index].Connections.Single();
        await ExpandAAsync(1, 3, confirm: true);
        await ExpandAAsync(2, 4, confirm: false);
        Ensure(View.Topology.Endpoints[0].Connections.Length == 3 && View.Buckets[0].EligibleCount() == 2 &&
               View.Buckets[1].EligibleCount() == 0 && View.Buckets[2].EligibleCount() == 1 &&
               Client.GetDiagnosticSnapshot().Resources.ReadyConnections == 5 &&
               _transports.All(transport => transport.ConnectCount == 1),
            "A owns three genuine Ready sessions, exactly two confirmed; B is unbound, C is confirmed");
        AssertUnboundHeld();
        CaptureFactoryOwners();
        foreach (var operation in _operations.Where(operation => operation.Stage is "original factory Connect" or "original transport Connect").ToArray())
            await ObserveAsync(operation);
        Ensure(_expansionFactory.SnapshotOriginals().Length == 3 && _expansionFactory.SnapshotChildren().Length == 3,
            "all three original factory entries and their independent transport child Tasks are retained");
        _initialized = true;
        Client._callAdmissionReservedTestHook = connection =>
        {
            lock (Reservations) Reservations.Add(connection);
            Volatile.Read(ref _selected)?.TrySetResult(connection);
        };
    }

    private TaskCompletionSource<ClientConnection>? _selected;

    internal async Task<int> CallAsync(int value)
    {
        AssertUnboundHeld();
        var selected = new TaskCompletionSource<ClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _selected, selected);
        var invocation = Own($"original Unary {value}",
            ClientInvokerTestHelper.InvokeUnaryAsync(Client, cancellationToken: _calls.Token).AsTask());
        var first = await Task.WhenAny(invocation.Task, selected.Task).WaitAsync(Bound);
        if (ReferenceEquals(first, invocation.Task)) await ObserveAsync(invocation);
        var connection = await selected.Task.WaitAsync(Bound);
        var index = Array.FindIndex(View.Topology.Endpoints,
            endpoint => endpoint.Connections.Any(owner => ReferenceEquals(owner, connection)));
        Ensure(index is 0 or 2, "actual reservation may only belong to a confirmed endpoint");
        var peerIndex = Array.FindIndex(_owners, owner => ReferenceEquals(owner, connection));
        Ensure(peerIndex is 0 or 2 or 3, "the exact selected physical owner must have its own confirmed token");
        var reader = Own($"request reader {value}",
            _transports[peerIndex].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
        await ObserveAsync(reader);
        var request = ((Task<TestSentFrame>)reader.Task).Result;
        await ObserveAsync(Own($"response injection {value}",
            _transports[peerIndex].Connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), value)));
        await ObserveAsync(invocation);
        Ensure(((Task<int>)invocation.Task).Result == value, "the original ordinary Unary must return its actual peer response");
        Volatile.Write(ref _selected, null);
        AssertUnboundHeld();
        // PendingRequestTable completes the logical caller before its finally releases
        // physical active capacity. Observe that actual completion tail separately.
        var tailStarted = Stopwatch.GetTimestamp();
        while (Enumerable.Range(0, _owners.Length).Any(peer =>
                   Owner(peer).CallAdmissionReservationCount != 0 || Owner(peer).ActiveCallCount != _loads[peer]))
        {
            if (Stopwatch.GetElapsedTime(tailStarted) >= Bound)
                throw new TimeoutException("The original response completion tail did not release physical ownership.");
            await Task.Delay(1);
        }
        return index;
    }

    internal async Task ExpectCallFailureAsync(Func<Exception, bool> expected)
    {
        var before = ReservationCount;
        var invocation = Own("original rejected Unary",
            ClientInvokerTestHelper.InvokeUnaryAsync(Client, cancellationToken: _calls.Token).AsTask());
        await ExpectAsync(invocation, expected);
        Ensure(ReservationCount == before, "a rejected selection must not acquire a physical reservation");
        AssertUnboundHeld();
    }

    internal int ReservationCount { get { lock (Reservations) return Reservations.Count; } }
    internal void AssertUnboundHeld()
        => Ensure(_bindFrames[1].HasValue && !_replyStarted[1] && View.Buckets[1].EligibleCount() == 0 &&
                  _bindFrames[4].HasValue && !_replyStarted[4] && Owner(4).CanAcceptCalls &&
                  View.Targets.All(target => !ReferenceEquals(target.Connection, Owner(4))),
            "B and A's third Ready owner's successful protocol3 replies stay held for the whole body");

    internal void SetLoad(int peer, int count)
    {
        Ensure(count >= 0, "load cannot be negative");
        var owner = Owner(peer);
        while (_loads[peer] < count)
        {
            Ensure(owner.TryReserveExactCallAdmission(), "real physical load must first acquire its exact reservation");
            // TryBeginUntrackedCall transfers/releases the reservation on BOTH outcomes;
            // never release it a second time. Only true owns an active counter to end.
            Ensure(owner.TryBeginUntrackedCall(), "real reservation-to-active transfer must succeed");
            _loads[peer]++;
        }
        while (_loads[peer] > count)
        {
            _loads[peer]--;
            owner.EndUntrackedCall();
        }
        Ensure(owner.ActiveCallCount == count && owner.CallAdmissionReservationCount == 0,
            "held load is the real physical active owner count, not a synthetic delegate return");
    }

    private async Task ExpandAAsync(int ordinal, int peer, bool confirm)
    {
        var before = View.Topology.Endpoints[0].Connections;
        var knownBindings = _operations.Where(operation => operation.Stage == "original BindContractDemand")
            .Select(operation => operation.Task).ToArray();
        // This is an existing production cold expansion entry, not a fabricated topology
        // or a claim that a business RPC caused the expansion in this test.
        View.Topology.Endpoints[0].Selected(Owner(0));
        await _expansionFactory!.Entered[ordinal].Task.WaitAsync(Bound);
        CaptureFrameworkOwners("StaticClusterExpansion", "original static expansion");
        var expansions = _operations.Where(operation => operation.Stage == "original static expansion").ToArray();
        Ensure(expansions.Length == ordinal && !expansions[^1].Task.IsCompleted,
            "capture each original expansion Task while its factory entry is gated");
        _expansionFactory.Release[ordinal].TrySetResult();
        await ObserveAsync(expansions[^1]);
        CaptureFrameworkOwners("ReadyContractBinding", "original ready binding scheduler");
        foreach (var scheduler in _operations.Where(operation => operation.Stage == "original ready binding scheduler").ToArray())
            await ObserveAsync(scheduler);
        var started = Stopwatch.GetTimestamp();
        while (View.Topology.Endpoints[0].Connections.Length != before.Length + 1)
        {
            if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException("Actual expansion did not publish its Ready owner.");
            await Task.Delay(1);
        }
        _owners[peer] = View.Topology.Endpoints[0].Connections.Single(owner => !before.Contains(owner));
        var reader = Own($"expanded Bind reader {peer}", _transports[peer].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
        await ObserveAsync(reader);
        _bindFrames[peer] = ((Task<TestSentFrame>)reader.Task).Result;
        var request = Protocol3BindingControlCodec.ReadBindContract(new(_bindFrames[peer]!.Value.Payload));
        Ensure(request.ContractId == ManualClientContractFixture.ContractId && request.Hash == ManualClientContractFixture.AssemblyHash &&
               request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
            "expanded owner must issue its own exact real protocol3 binding request");
        OwnedOperation? newBinding = null;
        started = Stopwatch.GetTimestamp();
        while (newBinding is null)
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            newBinding = _operations.SingleOrDefault(operation => operation.Stage == "original BindContractDemand" &&
                !knownBindings.Contains(operation.Task));
            if (newBinding is not null) break;
            if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException("Expanded original Bind task was not captured.");
            await Task.Delay(1);
        }
        Ensure(!newBinding.Task.IsCompleted, "new exact Bind owner must remain pending while its successful reply is held");
        if (confirm)
        {
            await ConfirmAsync(peer);
            await ObserveAsync(newBinding);
            Ensure(View.Targets.Any(target => ReferenceEquals(target.Connection, Owner(peer)) && target.IsEligible),
                "expanded owner's original Bind must publish its own confirmed target");
        }
    }

    private static void Configure(SharpClientBuilder builder)
    {
        builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 3;
            options.MaxEndpoints = 3;
            options.MaxConnections = 5;
            options.MaxConnectionsPerEndpoint = 3;
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
            if (original.Length == 3)
            {
                foreach (var task in original) Own("original BindContractDemand", task);
                return;
            }
            Ensure(original.Length < 3, "fixture must not admit additional physical Bind owners");
            if (Stopwatch.GetElapsedTime(started) >= Bound)
                throw new TimeoutException("All three pending original BindContractDemand tasks were not registered.");
            await Task.Delay(1);
        }
    }

    internal async Task CleanupAsync(Exception? primary)
    {
        List<Exception> failures = [];
        if (primary is not null) failures.Add(primary);
        if (_expansionFactory is not null)
            foreach (var release in _expansionFactory.Release) release.TrySetResult();
        for (var peer = 0; peer < _loads.Length; peer++)
        {
            while (_loads[peer] > 0)
            {
                _loads[peer]--;
                try { Owner(peer).EndUntrackedCall(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (_client is not null && !_initialized)
        {
            // A not-yet-consumed manifest can schedule Bind after an expansion/scheduler
            // snapshot. Setup failure is NOT proof that this producer graph is closed.
            // Do not dispose behind unknown original workers or substitute Stop for them.
            try { CaptureUnclosedFrameworkOwners(); }
            catch (Exception error) { failures.Add(error); }
            try { CaptureFactoryOwners(); }
            catch (Exception error) { failures.Add(error); }
            lock (Quarantined) Quarantined.Add(this);
            failures.Add(new InvalidOperationException(
                "Load setup producer graph did not close; all roots retained. Isolate/terminate this test process."));
            foreach (var operation in _operations.Where(operation => operation.Task.IsCompleted).ToArray())
                await JoinAsync(operation, failures);
            ThrowFailures(failures);
            return;
        }
        if (_client is not null)
        {
            Client._callAdmissionReservedTestHook = null;
            // A registration scheduler can be suspended at Task.Yield and create Bind
            // owners later. Join its original task before taking the final Bind snapshot.
            try { CaptureFrameworkOwners("StaticClusterExpansion", "original static expansion"); }
            catch (Exception error) { failures.Add(error); }
            foreach (var expansion in _operations.Where(operation => operation.Stage == "original static expansion").ToArray())
                await JoinAsync(expansion, failures);
            try { CaptureFrameworkOwners("ReadyContractBinding", "original ready binding scheduler"); }
            catch (Exception error) { failures.Add(error); }
            try { CaptureFrameworkOwners("StartContractBinding", "original binding scheduler"); }
            catch (Exception error) { failures.Add(error); }
            var schedulers = _operations.Where(operation => operation.Stage is "original binding scheduler" or "original ready binding scheduler" or "original static expansion").ToArray();
            foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
            if (schedulers.Any(operation => !operation.Task.IsCompleted))
            {
                lock (Quarantined) Quarantined.Add(this);
                failures.Add(new InvalidOperationException("Binding scheduler exit unknown; retain roots and isolate/terminate process."));
                foreach (var operation in _operations.Where(operation => operation.Task.IsCompleted).ToArray())
                    await JoinAsync(operation, failures);
                ThrowFailures(failures);
                return;
            }
            foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
            try { CaptureFrameworkOwners("ContractBinding", "original BindContractDemand"); }
            catch (Exception error) { failures.Add(error); }
        }
        // Open our held peer-reply gate before shutdown. Never make this release part of
        // the readiness/call oracle; it occurs only after the body has ended.
        for (var index = 0; index < 5; index++)
        {
            if (!_bindFrames[index].HasValue || _replyStarted[index]) continue;
            try { await ConfirmAsync(index); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var binding in _operations.Where(operation => operation.Stage == "original BindContractDemand").ToArray())
            await JoinAsync(binding, failures);
        try { _calls.Cancel(); } catch (Exception error) { failures.Add(error); }
        OwnedOperation? stop = null;
        if (_client is not null)
        {
            try { stop = Own("original Stop", _client.StopAsync().AsTask()); }
            catch (Exception error) { failures.Add(error); }
        }
        // Dispose peers independently so every outstanding original frame reader can
        // really end, even when an assertion failed before its request was emitted.
        foreach (var transport in _transports)
        {
            try { Own("original transport Dispose", transport.DisposeAsync().AsTask()); }
            catch (Exception error) { failures.Add(error); }
        }
        foreach (var operation in _operations.ToArray()) await JoinAsync(operation, failures);
        if (_client is not null && stop is { Task.IsCompleted: true })
        {
            try
            {
                var disposal = Own("original client Dispose", _client.DisposeAsync().AsTask());
                await JoinAsync(disposal, failures);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (_expansionFactory is not null)
        {
            CaptureFactoryOwners();
            foreach (var operation in _operations.Where(operation => operation.Stage is "original factory Connect" or "original transport Connect").ToArray())
                await JoinAsync(operation, failures);
        }
        if (_operations.Any(operation => !operation.Task.IsCompleted) ||
            _owners.Any(owner => owner is not null && (owner.ActiveCallCount != 0 || owner.CallAdmissionReservationCount != 0)))
        {
            lock (Quarantined) Quarantined.Add(this);
            failures.Add(new InvalidOperationException(
                "Original task exit or charged physical owner remains unresolved; fixture roots retained. Isolate/terminate this test process."));
        }
        else
        {
            // Completion is monotonic: after the all-terminal observation, audit every
            // original again. A task may have faulted after its first bounded join timed
            // out; that timeout did not consume the later complete exception graph.
            foreach (var operation in _operations) await JoinAsync(operation, failures);
            try { _calls.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        // Even if another owner remains unknown, retain every independent late terminal
        // fault which followed its first bounded join. This never waits on a pending owner.
        foreach (var operation in _operations.Where(operation => operation.Task.IsCompleted).ToArray())
            await JoinAsync(operation, failures);
        ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Body first, then independent original owner failures.", failures);
    }

    private void CaptureFactoryOwners()
    {
        if (_expansionFactory is null) return;
        foreach (var task in _expansionFactory.SnapshotOriginals())
            if (!_operations.Any(operation => operation.Stage == "original factory Connect" && ReferenceEquals(operation.Task, task)))
                Own("original factory Connect", task);
        foreach (var task in _expansionFactory.SnapshotChildren())
            if (!_operations.Any(operation => operation.Stage == "original transport Connect" && ReferenceEquals(operation.Task, task)))
                Own("original transport Connect", task);
    }

    private void CaptureUnclosedFrameworkOwners()
    {
        var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
        var gate = Read<Lock>(supervisor, "_gate");
        var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
        lock (gate)
            foreach (var pair in active)
                if (!_operations.Any(operation => ReferenceEquals(operation.Task, pair.Key)))
                    Own("unclosed setup original " + pair.Value.Operation, pair.Key);
    }

    private void CaptureFrameworkOwners(string operationName, string stage)
    {
        var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
        var gate = Read<Lock>(supervisor, "_gate");
        var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
        lock (gate)
            foreach (var pair in active)
                if (pair.Value.Operation == operationName &&
                    !_operations.Any(operation => operation.Stage == stage && ReferenceEquals(operation.Task, pair.Key)))
                    Own(stage, pair.Key);
    }

    private OwnedOperation Own(string stage, Task task)
    {
        var operation = new OwnedOperation(stage, task);
        _operations.Add(operation);
        return operation;
    }

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

    private sealed class OwnedOperation(string stage, Task task)
    {
        internal string Stage { get; } = stage;
        internal Task Task { get; } = task;
        internal bool Reported;
    }

    internal static T Read<T>(object owner, string field)
        => (T)(owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing exact cold owner field {owner.GetType().Name}.{field}."));

    private sealed class ExpansionTransportFactory(TestClientTransportFactory[] peers) : IClientTransportFactory
    {
        private readonly object _gate = new();
        private readonly Task<ITransportConnection>?[] _originals = new Task<ITransportConnection>?[3];
        private readonly Task<ITransportConnection>?[] _children = new Task<ITransportConnection>?[3];
        private int _next;
        internal TaskCompletionSource[] Entered { get; } = [Signal(), Signal(), Signal()];
        internal TaskCompletionSource[] Release { get; } = [Signal(), Signal(), Signal()];
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _next) - 1;
            if (index >= peers.Length) throw new InvalidOperationException("Unexpected extra physical connection attempt.");
            var original = ConnectCoreAsync(index, cancellationToken);
            lock (_gate) _originals[index] = original;
            return new(original);
        }
        private async Task<ITransportConnection> ConnectCoreAsync(int index, CancellationToken token)
        {
            Entered[index].TrySetResult();
            if (index != 0) await Release[index].Task.WaitAsync(token);
            var child = peers[index].ConnectAsync(token).AsTask();
            lock (_gate) _children[index] = child;
            return await child;
        }
        internal Task[] SnapshotOriginals() { lock (_gate) return _originals.OfType<Task>().ToArray(); }
        internal Task[] SnapshotChildren() { lock (_gate) return _children.OfType<Task>().ToArray(); }
        // This queue facade owns no transport resource. The test fixture independently
        // owns, retains and disposes every peer with the exact original Dispose task.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TestClientTransportFactory ManualTransport()
        => new(contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
            acceptManualContractBindings: false);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
