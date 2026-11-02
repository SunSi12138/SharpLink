using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Private original-owner fixture for the cold publication race controls.
public sealed partial class ContractScopedRouteFailureControls
{
    private sealed partial class FailureFixture
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
        // An unknown exit is not permission to release the fixture roots. The containing test
        // process must be isolated/terminated by the runner; this list is not a success path.
        private static readonly List<FailureFixture> Quarantined = [];
        private readonly List<OwnedOperation> _operations = [];
        private readonly Lock _operationsGate = new();
        private readonly List<ColdBarrier> _barriers = [];
        private int _applicationThread;
        private int _siteCreationThread;
        internal int SiteCreationThread => Volatile.Read(ref _siteCreationThread);
        internal int ApplicationThread => Volatile.Read(ref _applicationThread);
        internal SharpLinkClient Client => _client ?? throw new InvalidOperationException("Client not started.");
        internal SharpLinkClient.ContractCallSite Site => (SharpLinkClient.ContractCallSite)(_channel
            ?? throw new InvalidOperationException("Site not created."));
        internal SharpLinkClient.ContractRouteTopology GlobalTopology
            => Read<SharpLinkClient.ContractRouteTopology>(Client, "_contractRouteTopology");
        internal SharpLinkClient.ContractRouteView View => Site.CaptureContractRouteForDiagnostics();
        private readonly CancellationTokenSource _calls = new();
        private readonly ManualTimeProvider _clock = new();
        private Exception? _expectedLifecycleFailure;
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

        internal async Task InitializeAsync(bool createSite = true)
        {
            SharpLinkEndpoint[] endpoints =
            [
                new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) },
                new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) }
            ];
            void ConfigureFixture(SharpClientBuilder builder)
            {
                Configure(builder);
                builder.UseTimeProvider(_clock); // No unsolicited heartbeat can consume the Release output gate.
            }
            _client = ClientBuilderTestHelper.BuildDynamic(new FixedResolver(new(1, endpoints)),
                endpoint => endpoint.Id == "route-a" ? _transports[0] : _transports[1], ConfigureFixture);
            await ObserveAsync(Own("connect", _client.ConnectAsync().AsTask()));
            await ObserveAsync(Own("transport-readiness", _client.WaitForReadinessAsync(2).AsTask()));
            var snapshot = _client.GetDiagnosticSnapshot();
            Ensure(snapshot.Resources.ReadyConnections == 2 && snapshot.Topology.TotalEndpoints == 2 &&
                   _transports.All(transport => transport.ConnectCount == 1),
                "exactly two distinct endpoints each own one genuinely Ready physical session");
            if (createSite) await InitializeSiteAsync();
        }

        internal async Task InitializeSiteAsync()
        {
            _channel ??= ClientInvokerTestHelper.CreateChannel(Client);
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

        internal async Task ConfirmFirstAndPrepareAsync()
        {
            await ConfirmAsync(0);
            await ObserveAsync(_prepare!);
            AssertSecondStillUnconfirmed();
        }

        internal async Task CallFirstAsync(int response, bool requireSecondReplyHeld = true)
        {
            Ensure(_firstConfirmed, "A's genuine BindResult has been injected");
            if (requireSecondReplyHeld) AssertSecondStillUnconfirmed();
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
            if (requireSecondReplyHeld) AssertSecondStillUnconfirmed();
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

        internal async Task ConfirmAsync(int index)
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
            foreach (var barrier in _barriers) barrier.Release();
            if (_client is not null)
            {
                _client.ContractRouteBuildForTesting = null;
                _client.ContractRoutePublicationForTesting = null;
            }
            var launchers = SnapshotOperations().Where(operation =>
                operation.Stage.EndsWith("launcher", StringComparison.Ordinal)).ToArray();
            foreach (var launcher in launchers) await JoinAsync(launcher, failures);
            if (RetainUnknownOwners(launchers, failures))
            {
                await AuditTerminalOwnersAsync(failures);
                ThrowCleanupFailures(failures);
                return;
            }
            // Each launcher has now returned, so its actual Apply Task was inserted before
            // this point. No launcher's synchronous entry can append a new owner afterward.
            foreach (var launcher in launchers) await JoinAsync(launcher, failures);
            if (_client is not null)
            {
                try { CaptureFrameworkOwners("StartContractBinding", "original binding scheduler"); }
                catch (Exception error) { failures.Add(error); }
                var schedulers = SnapshotOperations().Where(operation => operation.Stage == "original binding scheduler").ToArray();
                foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
                if (RetainUnknownOwners(schedulers, failures))
                {
                    await AuditTerminalOwnersAsync(failures);
                    ThrowCleanupFailures(failures);
                    return;
                }
                foreach (var scheduler in schedulers) await JoinAsync(scheduler, failures);
                try { CaptureFrameworkOwners("ContractBinding", "original BindContractDemand"); }
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
            foreach (var binding in SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand").ToArray())
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
            if (stop is not null && _expectedLifecycleFailure is not null)
            {
                try { await ExpectFailureAsync(stop, error => ReferenceEquals(error, _expectedLifecycleFailure)); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (var operation in SnapshotOperations()) await JoinAsync(operation, failures);
            if (_client is not null && stop is { Task.IsCompleted: true })
            {
                try
                {
                    var disposal = Own("original client Dispose", _client.DisposeAsync().AsTask());
                    if (_expectedLifecycleFailure is not null)
                        await ExpectFailureAsync(disposal, error => ReferenceEquals(error, _expectedLifecycleFailure));
                    else await JoinAsync(disposal, failures);
                }
                catch (Exception error) { failures.Add(error); }
            }
            var finalOwners = SnapshotOperations();
            if (!RetainUnknownOwners(finalOwners, failures))
            {
                // No launcher can append after its proven exit. Audit the fixed all-terminal
                // owner set again, including any fault that followed a first join timeout.
                foreach (var operation in finalOwners) await JoinAsync(operation, failures);
                foreach (var barrier in _barriers)
                    try { barrier.Dispose(); } catch (Exception error) { failures.Add(error); }
                try { _calls.Dispose(); } catch (Exception error) { failures.Add(error); }
            }
            await AuditTerminalOwnersAsync(failures);
            ThrowCleanupFailures(failures);
        }

        private async Task AuditTerminalOwnersAsync(List<Exception> failures)
        {
            foreach (var operation in SnapshotOperations())
                if (operation.Task.IsCompleted) await JoinAsync(operation, failures);
        }

        private bool RetainUnknownOwners(OwnedOperation[] owners, List<Exception> failures)
        {
            if (owners.All(operation => operation.Task.IsCompleted)) return false;
            lock (Quarantined) Quarantined.Add(this);
            failures.Add(new InvalidOperationException(
                "Original task exit is unknown; fixture roots retained. Isolate/terminate this test process."));
            return true;
        }

        private static void ThrowCleanupFailures(List<Exception> failures)
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
            {
                foreach (var pair in active)
                    if (pair.Value.Operation == operationName && !SnapshotOperations().Any(operation =>
                        operation.Stage == stage && ReferenceEquals(operation.Task, pair.Key)))
                        Own(stage, pair.Key);
            }
        }

        internal ColdBarrier NewBarrier()
        {
            var barrier = new ColdBarrier();
            _barriers.Add(barrier);
            return barrier;
        }

        internal OwnedOperation StartSiteCreation()
            => Own("site creation launcher", Task.Factory.StartNew(() =>
            {
                Volatile.Write(ref _siteCreationThread, Environment.CurrentManagedThreadId);
                var channel = ClientInvokerTestHelper.CreateChannel(Client);
                _channel = channel;
                return channel;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal OwnedOperation StartReorder()
            => Own("topology invocation launcher", Task.Factory.StartNew(() =>
            {
                Volatile.Write(ref _applicationThread, Environment.CurrentManagedThreadId);
                var cluster = Read<object>(Client, "_cluster");
                var apply = cluster.GetType().GetMethod("ApplySnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Actual dynamic ApplySnapshotAsync was not found.");
                SharpLinkEndpoint[] reordered =
                [
                    new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) },
                    new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) }
                ];
                var original = (Task<bool>)(apply.Invoke(cluster, [new SharpLinkEndpointSnapshot(2, reordered), false])
                    ?? throw new InvalidOperationException("Actual topology application did not return its original Task."));
                return Own("original ApplySnapshotAsync", original);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal async Task FinishReorderAsync(OwnedOperation launcher)
        {
            await ObserveAsync(launcher);
            var original = ((Task<OwnedOperation>)launcher.Task).Result;
            await ObserveAsync(original);
            Ensure(((Task<bool>)original.Task).Result, "the sole actual resolver reorder must be accepted");
        }

        internal async Task FinishSiteCreationAsync(OwnedOperation launcher)
        {
            await ObserveAsync(launcher);
            Ensure(ReferenceEquals(((Task<IRpcContractChannel>)launcher.Task).Result, _channel),
                "the original site construction result is retained");
        }

        internal async Task AwaitViewAsync(Func<SharpLinkClient.ContractRouteView, bool> predicate)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate(View))
            {
                if (Stopwatch.GetElapsedTime(started) >= Bound)
                    throw new TimeoutException("The exact cold publication predicate was not observed.");
                await Task.Delay(1);
            }
        }

        internal bool OriginalPrepareIsPending => _prepare is { Task.IsCompleted: false };

        internal (object Attempt, bool Completed, object? Failure, Exception? PublicationFailure, Task<Exception?> Signal) CaptureAttempt(ClientConnection connection)
        {
            var gate = Read<Lock>(Site, "_bindingGate");
            lock (gate)
            {
                var attempts = Read<System.Collections.IDictionary>(Site, "_bindingAttempts");
                var attempt = attempts[connection] ?? throw new InvalidOperationException("Original binding attempt is missing.");
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var completed = (bool)attempt.GetType().GetField("Completed", flags)!.GetValue(attempt)!;
                var failure = attempt.GetType().GetField("Failure", flags)!.GetValue(attempt);
                var publicationFailure = (Exception?)attempt.GetType().GetField("PublicationFailure", flags)!.GetValue(attempt);
                var signal = Read<TaskCompletionSource<Exception?>>(Site, "_bindingSignal").Task;
                return (attempt, completed, failure, publicationFailure, signal);
            }
        }

        internal void HoldNextPeerOutput(int endpoint, ColdBarrier barrier)
            => _transports[endpoint].Connection.RunOnNextOutputBufferRequest(barrier.HoldOnce);

        internal async Task<OwnedOperation> CaptureCandidateCleanupAsync()
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                CaptureFrameworkOwners("BindingCandidateCleanup", "original candidate cleanup");
                var owners = SnapshotOperations().Where(operation => operation.Stage == "original candidate cleanup").ToArray();
                Ensure(owners.Length <= 1, "one failed target must have exactly one cleanup owner");
                if (owners.Length == 1) return owners[0];
                if (Stopwatch.GetElapsedTime(started) >= Bound)
                    throw new TimeoutException("The original candidate cleanup Task was not registered.");
                await Task.Delay(1);
            }
        }

        internal async Task FinishCandidateCleanupAsync(OwnedOperation cleanup, int endpoint)
        {
            var reader = Own("original ReleaseBinding frame reader",
                _transports[endpoint].Connection.WaitForSentFrame(ProtocolV2FrameType.ReleaseBinding));
            await ObserveAsync(reader);
            var frame = ((Task<TestSentFrame>)reader.Task).Result;
            var authority = _transports[endpoint].Connection.Authority;
            Ensure(Protocol3BindingControlCodec.ReadReleaseBinding(new(frame.Payload), authority.Limits.Slots) ==
                   Protocol3BindingToken.Create(0, 1, authority.Limits.Slots),
                "the failed candidate's exact token owns the one real ReleaseBinding frame");
            await ObserveAsync(cleanup);
            Ensure(cleanup.Task.IsCompletedSuccessfully, "the original cleanup Task physically exited successfully");
        }

        internal async Task ExpectOneFailedBindAsync(Exception marker)
        {
            var binds = SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand").ToArray();
            Ensure(binds.Length == 2, "both original Bind tasks were captured before any confirmation");
            var remaining = binds.Where(operation => !operation.Task.IsCompletedSuccessfully).ToArray();
            Ensure(remaining.Length != 0, "the injected infrastructure failure cannot be reported as semantic Bind success");
            var first = await Task.WhenAny(remaining.Select(operation => operation.Task)).WaitAsync(Bound);
            var failed = binds.Single(operation => ReferenceEquals(operation.Task, first));
            var error = await ExpectFailureAsync(failed, exception =>
                exception is SharpLinkClient.ContractRouteFailure && IsOnlyMarker(exception, marker));
            _expectedLifecycleFailure = error;
        }

        internal async Task ExpectOriginalPrepareFailureAsync(Exception marker)
            => _ = await ExpectFailureAsync(_prepare ?? throw new InvalidOperationException("Original Prepare was not started."),
                error => ReferenceEquals(error, marker));

        internal bool OriginalBindStillPending
            => SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand")
                .All(operation => !operation.Task.IsCompleted);

        internal async Task RetryFirstAndPrepareAsync()
        {
            var prepare = Own("original explicit cold retry Prepare", Site.PrepareAsync(Bound, _calls.Token).AsTask());
            var reader = Own("original retry Bind frame reader",
                _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
            await ObserveAsync(reader);
            var frame = ((Task<TestSentFrame>)reader.Task).Result;
            Ensure(frame.Header.RequestId != _bindFrames[0]!.Value.Header.RequestId,
                "cold retry must own a distinct real Bind operation, not retry an unknown injection");
            var request = Protocol3BindingControlCodec.ReadBindContract(new(frame.Payload));
            Ensure(request.ContractId == ManualClientContractFixture.ContractId && request.Hash == ManualClientContractFixture.AssemblyHash &&
                   request.Requirement == Protocol3BindingRequirement.Compatible,
                "the explicit cold retry preserves exact contract identity");
            var captureStarted = Stopwatch.GetTimestamp();
            while (true)
            {
                CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
                var count = SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand");
                Ensure(count <= 3, "cold retry cannot duplicate a pending original Bind owner");
                if (count == 3) break;
                if (Stopwatch.GetElapsedTime(captureStarted) >= Bound)
                    throw new TimeoutException("The original cold-retry Bind task was not registered.");
                await Task.Delay(1);
            }
            Ensure(SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3,
                "only the completed infrastructure-failed A attempt is retried; B's original pending Bind is not duplicated");
            var authority = _transports[0].Connection.Authority;
            using var payload = new PooledByteBufferWriter();
            Protocol3BindingControlCodec.WriteBindResult(payload, new Protocol3BindResult(Protocol3BindStatus.Success,
                Protocol3BindingToken.Create(0, 2, authority.Limits.Slots), request.ContractId, request.Hash,
                1, 1, authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
            var bytes = payload.WrittenMemory.ToArray();
            await ObserveAsync(Own("original retry BindResult injection", _transports[0].Connection.InjectFrameAsync(
                ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, frame.Header.RequestId, bytes)));
            await ObserveAsync(prepare);
            Ensure(View.Targets.Length == 1 && View.Targets[0].Connection.EndpointId == "route-a" &&
                   View.Targets[0].Token == Protocol3BindingToken.Create(0, 2, authority.Limits.Slots),
                "explicit cold retry publishes only A's independently confirmed new incarnation");
        }

        internal void AssertCapturedNotificationStillFails(Task<Exception?> notification, Exception marker)
        {
            Ensure(notification.IsCompletedSuccessfully && ReferenceEquals(notification.Result, marker),
                "the old successful notification value retains its original construction error after current signal rotation");
            try
            {
                _ = Site.TryCompleteReadyPreparation(Bound, _clock.GetTimestamp(), _calls.Token, notification);
            }
            catch (Exception error)
            {
                if (!ReferenceEquals(error, marker))
                    throw new AggregateException("Captured-notification oracle mismatch and original synchronous source error.",
                        new InvalidOperationException("The actual cold ready-check must prefer captured failure over later readiness."), error);
                return;
            }
            throw new InvalidOperationException("A later successful route erased the captured old notification failure.");
        }

        internal async Task ConfirmAndJoinOriginalPrepareAsync(int endpoint)
        {
            await ConfirmAsync(endpoint);
            await ObserveAsync(_prepare ?? throw new InvalidOperationException("Original Prepare was not started."));
        }

        internal async Task ExpectReorderFailureAsync(OwnedOperation launcher, Exception marker)
        {
            await ObserveAsync(launcher);
            var original = ((Task<OwnedOperation>)launcher.Task).Result;
            _ = await ExpectFailureAsync(original, error =>
                error is SharpLinkClient.ContractRouteFailure && IsOnlyMarker(error, marker));
        }

        internal async Task PrepareAgainAsync()
            => await ObserveAsync(Own("original reconcile Prepare", Site.PrepareAsync(Bound, _calls.Token).AsTask()));

        internal async Task ExpectPrepareFailureAsync(Exception marker)
        {
            OwnedOperation operation;
            try { operation = Own("original failed reconcile Prepare", Site.PrepareAsync(Bound, _calls.Token).AsTask()); }
            catch (Exception error)
            {
                if (!ReferenceEquals(error, marker))
                    throw new AggregateException("Prepare oracle mismatch and original synchronous source error.",
                        new InvalidOperationException("Synchronous Prepare must retain the exact construction failure."), error);
                return;
            }
            _ = await ExpectFailureAsync(operation, error => ReferenceEquals(error, marker));
        }

        private static bool IsOnlyMarker(Exception error, Exception marker)
        {
            if (ReferenceEquals(error, marker)) return true;
            if (error is SharpLinkClient.ContractRouteFailure route)
                return route.Count == 1 && IsOnlyMarker(route.ErrorAt(0), marker);
            if (error is AggregateException aggregate)
                return aggregate.InnerExceptions.Count == 1 && IsOnlyMarker(aggregate.InnerExceptions[0], marker);
            return false;
        }

        private static async Task<Exception> ExpectFailureAsync(OwnedOperation operation, Func<Exception, bool> expected)
        {
            try { await operation.Task.WaitAsync(Bound); }
            catch (Exception observed)
            {
                if (!operation.Task.IsCompleted) throw; // Preserve the original observer timeout; this is not source exit.
                var actual = await CaptureTerminalFailureAsync(operation.Task);
                var observerTimeout = IsObserverTimeout(operation.Task, observed);
                Exception original;
                try
                {
                    Ensure(actual is AggregateException { InnerExceptions.Count: 1 },
                        "expected infrastructure error must be one real fault, not cancellation or an unknown sibling graph");
                    original = ((AggregateException)actual!).InnerExceptions[0];
                    Ensure(expected(original), $"unexpected complete original failure at {operation.Stage}: {actual}");
                }
                catch (Exception validation)
                {
                    // An oracle mismatch is not permission to discard the actual source
                    // graph or a distinct observer timeout. This occurrence is now fully
                    // included in the body error, so cleanup need not append it a second time.
                    operation.Reported = true;
                    List<Exception> failures = [validation];
                    if (actual is null || observerTimeout) failures.Add(observed);
                    if (actual is not null) failures.Add(actual);
                    throw new AggregateException("Expected-outcome validation and complete original observation failures.", failures);
                }
                operation.Reported = true;
                if (observerTimeout)
                    throw new AggregateException("Observer timeout plus expected source failure.", observed, actual!);
                return original;
            }
            throw new InvalidOperationException($"Expected original failure at {operation.Stage}.");
        }

        private OwnedOperation[] SnapshotOperations()
        {
            lock (_operationsGate) return _operations.ToArray();
        }

        private OwnedOperation Own(string stage, Task task)
        {
            var operation = new OwnedOperation(stage, task);
            lock (_operationsGate) _operations.Add(operation);
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

        private static T Read<T>(object owner, string field)
            => (T)(owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
                ?? throw new InvalidOperationException($"Missing exact cold owner field {owner.GetType().Name}.{field}."));

        private static TestClientTransportFactory ManualTransport()
            => new(contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
                acceptManualContractBindings: false);
    }

    private sealed class ColdBarrier : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;
        internal Task Entered => _entered.Task;
        internal void HoldOnce()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) return;
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Original cold writer barrier was not released.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
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
