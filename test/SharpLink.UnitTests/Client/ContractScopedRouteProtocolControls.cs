using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// R01/R02 are protocol counterexamples, not an all-Ready-bind fixture or logical retry.
public sealed class ContractScopedRouteProtocolControls
{
    [Test]
    public Task StaticTwoReadyOneBound() => TwoReadyOneBound(dynamicResolver: false);

    [Test]
    public Task DynamicTwoReadyOneBound() => TwoReadyOneBound(dynamicResolver: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoConfirmedTarget(bool dynamicResolver)
    {
        var callbacks = new ForbiddenCallbacks();
        var fixture = new PartialBindingFixture();
        Exception? primary = null;
        try
        {
            await fixture.InitializeAsync(dynamicResolver, callbacks);
            await fixture.AssertNoConfirmedTargetAsync();
            Ensure(callbacks.SelectCalls == 0 && callbacks.AcquireCalls == 0 && callbacks.ReportCalls == 0,
                "empty contract eligibility must not reach selector, admission or outcome reporting");
        }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private static async Task TwoReadyOneBound(bool dynamicResolver)
    {
        var fixture = new PartialBindingFixture();
        Exception? primary = null;
        try
        {
            await fixture.InitializeAsync(dynamicResolver);
            await fixture.ConfirmFirstAndPrepareAsync();
            // Neither the default initial cursor nor a second logical attempt is an oracle:
            // two independent ordinary Unary calls cover both old whole-pool RR positions.
            await fixture.CallFirstAsync(17);
            await fixture.CallFirstAsync(29);
            fixture.AssertSecondStillUnconfirmed();
        }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private sealed class PartialBindingFixture
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
        // An unknown exit is not permission to release the fixture roots. The containing test
        // process must be isolated/terminated by the runner; this list is not a success path.
        private static readonly List<PartialBindingFixture> Quarantined = [];
        private readonly List<OwnedOperation> _operations = [];
        private readonly CancellationTokenSource _calls = new();
        private readonly TestClientTransportFactory[] _transports =
        [
            ManualTransport(), ManualTransport()
        ];
        private readonly TestSentFrame?[] _bindFrames = new TestSentFrame?[2];
        private readonly bool[] _replyStarted = new bool[2];
        private SharpLinkClient? _client;
        private IRpcContractChannel? _channel;
        private OwnedOperation? _prepare;
        private bool _secondConfirmed;
        private bool _firstConfirmed;

        internal async Task InitializeAsync(bool dynamicResolver, ForbiddenCallbacks? callbacks = null)
        {
            SharpLinkEndpoint[] endpoints =
            [
                new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) },
                new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) }
            ];
            void ConfigureFixture(SharpClientBuilder builder)
            {
                Configure(builder);
                if (callbacks is null) return;
                builder.UseEndpointSelector(callbacks);
                builder.UseEndpointAdmission(callbacks);
            }
            _client = dynamicResolver
                ? ClientBuilderTestHelper.BuildDynamic(new FixedResolver(new(1, endpoints)),
                    endpoint => endpoint.Id == "route-a" ? _transports[0] : _transports[1], ConfigureFixture)
                : ClientBuilderTestHelper.BuildStatic(
                    [new(endpoints[0], _transports[0]), new(endpoints[1], _transports[1])], ConfigureFixture);
            await ObserveAsync(Own("connect", _client.ConnectAsync().AsTask()));
            await ObserveAsync(Own("transport-readiness", _client.WaitForReadinessAsync(2).AsTask()));
            var snapshot = _client.GetDiagnosticSnapshot();
            Ensure(snapshot.Resources.ReadyConnections == 2 && snapshot.Topology.TotalEndpoints == 2 &&
                   _transports.All(transport => transport.ConnectCount == 1),
                "exactly two distinct endpoints each own one genuinely Ready physical session");
            _channel = ClientInvokerTestHelper.CreateChannel(_client);
            _prepare = Own("original Prepare", _channel.PrepareAsync(Bound, _calls.Token).AsTask());
            for (var index = 0; index < 2; index++)
            {
                var reader = Own($"Bind frame reader {index}",
                    _transports[index].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
                await ObserveAsync(reader);
                _bindFrames[index] = ((Task<TestSentFrame>)reader.Task).Result;
                var request = Protocol3BindingControlCodec.ReadBindContract(new(_bindFrames[index]!.Value.Payload));
                Ensure(request.ContractId == ManualClientContractFixture.ContractId &&
                       request.Hash == ManualClientContractFixture.AssemblyHash &&
                       request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
                    "the controlled peer must receive only the actually registered exact fixture identity");
            }
            // Both replies remain held. Snapshot only actual cold owner Tasks, under the
            // existing supervisor gate; do not inspect targets or wait for all routes bound.
            await CaptureOriginalBindOwnersAsync();
            Ensure(!_prepare.Task.IsCompleted, "transport Ready alone cannot satisfy public Prepare");
        }

        internal async Task AssertNoConfirmedTargetAsync()
        {
            var prepare = _prepare ?? throw new InvalidOperationException("Original Prepare was not started.");
            Ensure(!prepare.Task.IsCompleted && !_firstConfirmed && !_secondConfirmed,
                "original public Prepare stays pending while both successful Bind replies are held");
            var invocation = Own("unprepared ordinary Unary",
                ClientInvokerTestHelper.InvokeUnaryAsync(_client!, cancellationToken: _calls.Token).AsTask());
            await ExpectAsync(invocation, error => error is SharpLinkException
            {
                Code: SharpLinkErrorCode.Unavailable,
                DetailCode: SharpLinkErrorDetails.Unavailable.NoBoundRoute
            });
            Ensure(!prepare.Task.IsCompleted, "a direct no-route failure must not complete Prepare");
            _calls.Cancel();
            await ExpectAsync(prepare, error => error is OperationCanceledException canceled &&
                canceled.CancellationToken == _calls.Token);
            Ensure(!_firstConfirmed && !_secondConfirmed,
                "Prepare exits via its own caller cancellation, not an implicit Bind confirmation");
        }

        internal async Task ConfirmFirstAndPrepareAsync()
        {
            await ConfirmAsync(0);
            await ObserveAsync(_prepare!);
            AssertSecondStillUnconfirmed();
        }

        internal async Task CallFirstAsync(int response)
        {
            Ensure(_firstConfirmed, "A's genuine BindResult has been injected");
            AssertSecondStillUnconfirmed();
            var invocation = Own($"ordinary Unary {response}",
                ClientInvokerTestHelper.InvokeUnaryAsync(_client!, cancellationToken: _calls.Token).AsTask());
            var reader = Own($"A request reader {response}",
                _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
            // If old whole-pool selection fails immediately, report that actual original
            // invocation failure rather than waiting for a request which cannot exist.
            var first = await Task.WhenAny(invocation.Task, reader.Task).WaitAsync(Bound);
            if (ReferenceEquals(first, invocation.Task))
                await ObserveAsync(invocation);
            await ObserveAsync(reader);
            var request = ((Task<TestSentFrame>)reader.Task).Result;
            await ObserveAsync(Own($"A response injection {response}",
                _transports[0].Connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), response)));
            await ObserveAsync(invocation);
            Ensure(((Task<int>)invocation.Task).Result == response,
                "each independent Unary must reach confirmed A and return its own response");
            AssertSecondStillUnconfirmed();
        }

        internal void AssertSecondStillUnconfirmed()
            => Ensure(!_secondConfirmed && _bindFrames[1].HasValue,
                "B's successful BindResult remains held continuously through both independent calls");

        private static void Configure(SharpClientBuilder builder)
        {
            builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
            builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
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
            if (index == 0) _firstConfirmed = true;
            else _secondConfirmed = true;
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
                    throw new TimeoutException("Both pending original BindContractDemand tasks were not registered.");
                await Task.Delay(1);
            }
        }

        internal async Task CleanupAsync(Exception? primary)
        {
            List<Exception> failures = [];
            if (primary is not null) failures.Add(primary);
            // Setup can fail while the actual StartContractBinding scheduler is still
            // before its Task.Yield continuation. Join that real producer before taking
            // the original Bind snapshot; Stop's indirect join is not a task identity.
            if (_client is not null)
            {
                var schedulerCaptureSucceeded = false;
                try
                {
                    var supervisor = Read<FrameworkTaskSupervisor>(_client, "_frameworkTasks");
                    var gate = Read<Lock>(supervisor, "_gate");
                    var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
                    lock (gate)
                        foreach (var pair in active)
                            if (pair.Value.Operation == "StartContractBinding" && !_operations.Any(operation =>
                                operation.Stage == "original binding scheduler" && ReferenceEquals(operation.Task, pair.Key)))
                                Own("original binding scheduler", pair.Key);
                    schedulerCaptureSucceeded = true;
                }
                catch (Exception error) { failures.Add(error); }
                var schedulers = _operations.Where(operation => operation.Stage == "original binding scheduler").ToArray();
                foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
                if (!schedulerCaptureSucceeded || schedulers.Any(operation => !operation.Task.IsCompleted))
                {
                    lock (Quarantined) Quarantined.Add(this);
                    failures.Add(new InvalidOperationException(
                        "Original binding producer exit is unknown; fixture roots retained. Isolate/terminate this test process."));
                    foreach (var operation in _operations.Where(operation => operation.Task.IsCompleted).ToArray())
                        await JoinAsync(operation, failures);
                    throw new AggregateException("Body first, then independent original producer failures.", failures);
                }
                foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
            }
            // Scheduler producers are now terminal; capture their actual Bind tasks before
            // Stop can consume/remove registrations, even if frame-reader setup failed.
            if (_client is not null)
            {
                try
                {
                    var supervisor = Read<FrameworkTaskSupervisor>(_client, "_frameworkTasks");
                    var gate = Read<Lock>(supervisor, "_gate");
                    var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
                    lock (gate)
                    {
                        foreach (var pair in active)
                            if (pair.Value.Operation == "ContractBinding" &&
                                !_operations.Any(operation => operation.Stage == "original BindContractDemand" &&
                                    ReferenceEquals(operation.Task, pair.Key)))
                                Own("original BindContractDemand", pair.Key);
                    }
                }
                catch (Exception error) { failures.Add(error); }
            }
            // Open our held peer-reply gate before shutdown. Never make this release part of
            // the readiness/call oracle; it occurs only after the body has ended.
            for (var index = 0; index < 2; index++)
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
            if (_operations.Any(operation => !operation.Task.IsCompleted))
            {
                lock (Quarantined) Quarantined.Add(this);
                failures.Add(new InvalidOperationException(
                    "Original task exit is unknown; fixture roots retained. Isolate/terminate this test process."));
            }
            else
            {
                // Completion is monotonic: after the all-terminal observation, audit every
                // original again. A task may have faulted after its first bounded join timed
                // out; that timeout did not consume the later complete exception graph.
                foreach (var operation in _operations) await JoinAsync(operation, failures);
                try { _calls.Dispose(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Body first, then independent original owner failures.", failures);
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

        private static T Read<T>(object owner, string field)
            => (T)(owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
                ?? throw new InvalidOperationException($"Missing exact cold owner field {owner.GetType().Name}.{field}."));

        private static TestClientTransportFactory ManualTransport()
            => new(contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
                acceptManualContractBindings: false);
    }

    private sealed class ForbiddenCallbacks : ISharpLinkEndpointSelector, ISharpLinkEndpointAdmissionPolicy
    {
        internal int SelectCalls;
        internal int AcquireCalls;
        internal int ReportCalls;
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            Interlocked.Increment(ref SelectCalls);
            return 0;
        }
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref AcquireCalls);
            return new(true, 71, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token)
            => Interlocked.Increment(ref ReportCalls);
    }

    private sealed class FixedResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
