using System.Linq;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

internal sealed partial class ContractScopedRouteHandoffFixture
{
    private RetryClock _retryClock = null!;
    private DelayedOneRetry _oneRetry = null!;
    private RetrySelector _retrySelector = null!;
    private bool _retryDynamic;
    private int _retryPinIndex = -1;
    private ClientConnection _retryB = null!;
    private TaskCompletionSource<ClientConnection>? _retryReservation;

    internal async Task ExerciseSameMapRetryAsync(string change)
    {
        await InitializeRetryAsync(dynamicResolver: false, bindB: change != "binding");
        var before = View;
        var originalSelector = _retrySelector!;
        var logical = await FirstAttemptAndBackoffAsync();
        if (change == "binding") await ConfirmRetryBAsync();
        else if (change == "policy")
        {
            _retrySelector = new RetrySelector();
            Client.UpdateEndpointSelector(_retrySelector);
        }
        else if (change == "replacement")
        {
            await RefreshAForRetryAsync();
            await ConfirmReplacementAsync();
            await JoinRetiredSourceAsync();
        }
        else throw new InvalidOperationException("Unknown same-map control variant.");
        Ensure(ReferenceEquals(before.Topology.IndexMap, View.Topology.IndexMap) &&
               View.Buckets.All(bucket => bucket.EligibleCount() == 1) && !logical.Task.IsCompleted,
            "binding/policy/physical replacement changes must preserve actual endpoint index authority during one original retry delay");
        if (change == "binding") Ensure(View.BindingRevision > before.BindingRevision, "a real new Bind view must publish");
        if (change == "replacement") Ensure(View.Topology.Revision > before.Topology.Revision &&
            !ReferenceEquals(Source, Replacement), "real same-endpoint physical replacement must publish a new topology but the same index map");
        var selected = ArmRetryReservation();
        _retryClock!.ResumeRetry();
        await CompleteRetryWireAsync(logical, selected, 1, _retryB!, 3100);
        var first = originalSelector.Captures[0];
        var last = _retrySelector!.Captures[^1];
        Ensure(first.Ids.SequenceEqual(new[] { "route-a", "route-b" }) && first.Selected == 0 &&
               first.Mask == (change == "binding" ? 0b10UL : 0UL) &&
               last.Ids.SequenceEqual(new[] { "route-a", "route-b" }) && last.Mask == 0b01 && last.Selected == 1 &&
               last.Generations.SequenceEqual(first.Generations) &&
               (change == "policy" ? originalSelector.Captures.Length == 1 && _retrySelector.Captures.Length == 1
                                    : originalSelector.Captures.Length == 2),
            "the same logical retry must preserve A exclusion across a new Bind view, policy, or physical owner; no old-only-A premature cycle reset");
        Ensure(_oneRetry!.Calls == 1, "one original logical Unary evaluated exactly one retry, not a replacement independent call");
    }

    internal async Task ExerciseChangedMapRetryAsync(string change)
    {
        await InitializeRetryAsync(dynamicResolver: true, bindB: true);
        var before = View;
        var logical = await FirstAttemptAndBackoffAsync();
        var peer = 0;
        var owner = Source;
        if (change == "reorder")
        {
            await ApplyGenerationSnapshotAsync(new(2,
                [GenerationEndpoint("route-b", "before"), GenerationEndpoint("route-a", "before")]));
            peer = 1;
            owner = _retryB!;
        }
        else if (change == "attributes") await ApplyAttributesOnlyAsync();
        else if (change == "generation")
        {
            lock (Reservations)
            {
                Ensure(Reservations.Count == 1 && ReferenceEquals(Reservations[0], Source),
                    "only the first actual wire attempt reserved before the test-owned pin");
                _retryPinIndex = Reservations.Count;
            }
            PinOriginalGeneration();
            lock (Reservations)
                Ensure(Reservations.Count == _retryPinIndex + 1 && ReferenceEquals(Reservations[_retryPinIndex], Source),
                    "one test-owned exact pin is independently recorded at its real ordinal, never removed from the trace");
            await NewGenerationDuringRetryAsync(logical);
            await ConfirmReplacementAsync();
            await ReleaseRetiredGenerationAsync();
            peer = 2;
            owner = Replacement;
        }
        else throw new InvalidOperationException("Unknown changed-map control variant.");
        Ensure(!ReferenceEquals(before.Topology.IndexMap, View.Topology.IndexMap) && !logical.Task.IsCompleted,
            "true topology/index identity change occurs while the actual original logical invocation remains in backoff");
        if (change == "attributes") Ensure(View.Candidates[0].Generation == before.Candidates[0].Generation &&
            ReferenceEquals(View.Topology.Endpoints[0].Connections.Single(), Source) &&
            View.Candidates[0].Endpoint.Attributes["revision"] == "after", "attribute-only change preserves physical generation but changes index-map authority");
        if (change == "generation") Ensure(View.Candidates[0].Generation != before.Candidates[0].Generation,
            "generation scenario must establish actual new generation authority");
        var selected = ArmRetryReservation();
        _retryClock!.ResumeRetry();
        await CompleteRetryWireAsync(logical, selected, peer, owner, 3200);
        var captures = _retrySelector!.Captures;
        Ensure(captures.Length == 2 && captures[0].Selected == 0 && captures[1].Selected == 0 && captures[1].Mask == 0 &&
               captures[1].Ids[0] == (change == "reorder" ? "route-b" : "route-a") &&
               captures[1].Generations.SequenceEqual(View.Candidates.Select(candidate => candidate.Generation)),
            "the original retry resets its old A exclusion only on true new index-map authority");
        Ensure(_oneRetry!.Calls == 1, "the tested retry belongs to the original logical Unary");
    }

    internal async Task ExerciseStableSelectionLoopAsync()
    {
        await InitializeRetryAsync(dynamicResolver: true, bindB: true);
        var before = View;
        var gate = new ReservationBarrier();
        _barriers.Add(gate);
        _retrySelector!.BeforeReturn = () =>
        {
            gate.Entered.TrySetResult(Source);
            gate.Release.Wait();
        };
        var selected = ArmRetryReservation();
        var launcher = StartRetryLogicalCall();
        await gate.WaitAsync();
        await ApplyGenerationSnapshotAsync(new(2,
            [GenerationEndpoint("route-b", "before"), GenerationEndpoint("route-a", "before")]));
        Ensure(!ReferenceEquals(before.Topology.IndexMap, View.Topology.IndexMap) && View.Candidates[0].Endpoint.Id == "route-b",
            "real live reorder must publish before the original selector returns its old index");
        gate.Release.Set();
        var logical = await OriginalCallAsync(launcher);
        await CompleteRetryWireAsync(logical, selected, 0, Source, 3201);
        Ensure(_retrySelector.Captures.Length == 1 && _retrySelector.Captures[0].Ids[0] == "route-a" &&
               _oneRetry!.Calls == 0, "one captured selection loop must reserve old index0 A, not restart at new index0 B after live publication");
    }

    private async Task InitializeRetryAsync(bool dynamicResolver, bool bindB)
    {
        _retryDynamic = dynamicResolver;
        _retryClock = new RetryClock();
        _oneRetry = new DelayedOneRetry(_retryClock);
        _retrySelector = new RetrySelector();
        _peers = [new(_transports[0].Connection, holdDispose: true), new(_transports[1].Connection), new(_transports[2].Connection)];
        void ConfigureRetry(SharpClientBuilder builder)
        {
            Configure(builder);
            builder.UseTimeProvider(_retryClock);
            builder.UseRetry(options => { options.MaxAttempts = 2; options.InitialBackoff = TimeSpan.Zero; options.MaxBackoff = TimeSpan.Zero; options.JitterRatio = 0; });
            builder.UseRetry(_oneRetry);
            builder.UseEndpointSelector(_retrySelector);
            // Reconnect is a separate deterministic 1ms clock owner, never the 1s retry timer.
            builder.UseReconnectPolicy(new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, 1, 1, TimeSpan.Zero));
        }
        if (dynamicResolver)
        {
            _generationFactories =
            [new(_transports[0], _peers[0], false, true), new(_transports[1], _peers[1], false, false), new(_transports[2], _peers[2], true, false)];
            var aCreated = 0;
            _client = ClientBuilderTestHelper.BuildDynamic(new GenerationResolver(new(1,
                [GenerationEndpoint("route-a", "before"), GenerationEndpoint("route-b", "before")])), endpoint =>
            {
                Interlocked.Increment(ref _generationFactoryCount);
                return endpoint.Id == "route-b" ? _generationFactories[1] :
                    Interlocked.Increment(ref aCreated) == 1 ? _generationFactories[0] : _generationFactories[2];
            }, ConfigureRetry);
        }
        else
        {
            _factory = new HandoffTransportFactory(_transports[0], _transports[2], _peers[0], _peers[2]);
            _client = ClientBuilderTestHelper.BuildStatic(
                [new(GenerationEndpoint("route-a", "before"), _factory),
                 new(GenerationEndpoint("route-b", "before"), new SinglePeerFactory(_transports[1], _peers[1]))], ConfigureRetry);
        }
        await ObserveAsync(Own("original retry fixture Connect", Client.ConnectAsync().AsTask()));
        await ObserveAsync(Own("original retry fixture readiness", Client.WaitForReadinessAsync(2).AsTask()));
        if (dynamicResolver) CaptureFrameworkOwners("DynamicClusterTopologyResolver", "original resolver worker");
        _channel = ClientInvokerTestHelper.CreateChannel(Client);
        _prepare = Own("original retry fixture Prepare", _channel.PrepareAsync(Bound, _calls.Token).AsTask());
        CaptureFrameworkOwners("StartContractBinding", "original binding scheduler");
        foreach (var scheduler in SnapshotOperations().Where(operation => operation.Stage == "original binding scheduler")) await ObserveAsync(scheduler);
        await ReadBindAsync(0);
        await ReadBindAsync(1);
        await CaptureOriginalBindOwnersAsync();
        await ConfirmAsync(0);
        await ObserveAsync(_prepare);
        await AwaitAsync(() => View.Targets.Any(target => target.Connection.EndpointId == "route-a" && target.IsEligible), "A did not bind");
        OriginalTarget = View.Targets.Single();
        Source = OriginalTarget.Connection;
        _retryB = View.Topology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-b").Connections.Single();
        if (bindB) await ConfirmRetryBAsync();
        CaptureFactoryOwners();
        CaptureGenerationTasks();
        _initialized = true;
        Client._callAdmissionReservedTestHook = connection =>
        {
            lock (Reservations) Reservations.Add(connection);
            Volatile.Read(ref _retryReservation)?.TrySetResult(connection);
        };
    }

    private async Task ConfirmRetryBAsync()
    {
        await ConfirmAsync(1);
        await AwaitAsync(() => View.Targets.Any(target => ReferenceEquals(target.Connection, _retryB) && target.IsEligible),
            "B's own actual protocol3 confirmation did not publish");
    }

    private OwnedOperation StartRetryLogicalCall() => Own("original retry invocation launcher", Task.Factory.StartNew(() =>
        Own("retry logical original Unary", ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(Client, cancellationToken: _calls.Token).AsTask()),
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    private async Task<OwnedOperation> FirstAttemptAndBackoffAsync()
    {
        var logical = await OriginalCallAsync(StartRetryLogicalCall());
        var reader = Own("first actual retry attempt frame reader", _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
        if (ReferenceEquals(await Task.WhenAny(reader.Task, logical.Task).WaitAsync(Bound), logical.Task)) await ObserveAsync(logical);
        await ObserveAsync(reader);
        var frame = ((Task<TestSentFrame>)reader.Task).Result;
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(payload, SharpLinkErrorCode.Unavailable, "controlled first attempt", 1024, out _);
        var bytes = payload.WrittenMemory.ToArray();
        await ObserveAsync(Own("original first wire Unavailable injection", _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.Error, frame.Header.RequestId, bytes)));
        await _retryClock!.RetryTimerCreated.Task.WaitAsync(Bound);
        Ensure(_oneRetry!.Calls == 1 && _oneRetry.Last.Attempt == 1 && _oneRetry.Last.Method.IsIdempotent &&
               _oneRetry.Last.ErrorCode == SharpLinkErrorCode.Unavailable && _oneRetry.Last.ResponseObserved && !logical.Task.IsCompleted &&
               _retryClock.RetryTimer is not null && _retryClock.RetryFires == 0,
            "actual first remote Unavailable arms the original logical invocation's exact retry timer");
        await AwaitAsync(() => Source.PendingCalls.Count == 0 && Source.ActiveCallCount == 0 && Source.CallAdmissionReservationCount == 0,
            "first attempt's captured original physical completion tail did not drain before mutation");
        Ensure(((ISharpLinkClientDrainInspector)Client).ActiveCallCount == 1,
            "one original logical invocation remains active between wire attempts");
        return logical;
    }

    private TaskCompletionSource<ClientConnection> ArmRetryReservation()
    {
        var signal = new TaskCompletionSource<ClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _retryReservation, signal);
        return signal;
    }

    private async Task CompleteRetryWireAsync(OwnedOperation logical, TaskCompletionSource<ClientConnection> selected,
        int peer, ClientConnection exactOwner, int value)
    {
        if (ReferenceEquals(await Task.WhenAny(logical.Task, selected.Task).WaitAsync(Bound), logical.Task)) await ObserveAsync(logical);
        Ensure(ReferenceEquals(await selected.Task.WaitAsync(Bound), exactOwner), "actual next wire attempt reserved the required exact physical owner");
        var reader = Own("next actual retry attempt frame reader", _transports[peer].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
        if (ReferenceEquals(await Task.WhenAny(logical.Task, reader.Task).WaitAsync(Bound), logical.Task)) await ObserveAsync(logical);
        await ObserveAsync(reader);
        var frame = ((Task<TestSentFrame>)reader.Task).Result;
        await ObserveAsync(Own("original final retry response injection", _transports[peer].Connection.InjectInt32ResponseAsync(
            unchecked((long)frame.Header.RequestId), value)));
        await ObserveAsync(logical);
        Ensure(((Task<int>)logical.Task).Result == value, "the same original logical Unary returns its later actual wire response");
        await AwaitAsync(() => exactOwner.PendingCalls.Count == 0 && exactOwner.ActiveCallCount == 0 && exactOwner.CallAdmissionReservationCount == 0,
            "final actual wire attempt's original physical tail did not release ownership");
        Volatile.Write(ref _retryReservation, null);
        Ensure(((ISharpLinkClientDrainInspector)Client).ActiveCallCount == 0, "the original logical invocation ends only after its final attempt");
        lock (Reservations)
        {
            var wireAttempts = _oneRetry.Calls == 0 ? 1 : 2;
            var hasPin = _retryPinIndex >= 0;
            Ensure(Reservations.Count == wireAttempts + (hasPin ? 1 : 0) && ReferenceEquals(Reservations[0], Source),
                "the first actual wire reservation remains independently visible, with no unexplained physical reservations");
            if (hasPin)
                Ensure(_retryPinIndex == 1 && wireAttempts == 2 && ReferenceEquals(Reservations[_retryPinIndex], Source) &&
                       !_generationPinned && Source.ActiveCallCount == 0 && Source.CallAdmissionReservationCount == 0,
                    "the one test-owned source pin occupies its exact ordinal and releases independently of both wire attempts");
            var finalWireIndex = wireAttempts - 1 + (hasPin ? 1 : 0);
            Ensure(ReferenceEquals(Reservations[finalWireIndex], exactOwner),
                "the final actual wire attempt owns its own exact captured reservation, not the test-owned pin");
        }
    }

    private async Task RefreshAForRetryAsync()
    {
        _refreshStarted = true;
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Client._afterSessionRefreshEligibilitySwapTestHook = () => cut.TrySetResult();
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteSessionRefreshRequested(payload,
            new ProtocolV2SessionRefreshRequested(_transports[0].Connection.Authority.ServerInstanceId, 2));
        var bytes = payload.WrittenMemory.ToArray();
        await ObserveAsync(Own("original retry refresh injection", _transports[0].Connection.InjectFrameAsync(
            ProtocolV2FrameType.SessionRefreshRequested, ProtocolV2FrameFlags.None, 0, bytes)));
        await _factory!.ReplacementEntered.Task.WaitAsync(Bound);
        CaptureFrameworkOwners("StaticClusterSessionRefreshRollout", "original session refresh rollout");
        _rollout = SnapshotOperations().Single(operation => operation.Stage == "original session refresh rollout");
        _factory.ReleaseReplacement.TrySetResult();
        await cut.Task.WaitAsync(Bound);
        await ObserveAsync(_rollout);
        Replacement = Source.SessionRefreshRedirect?.Current ?? throw new InvalidOperationException("Real redirect missing");
        await ReadBindAsync(2);
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            return SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3;
        }, "replacement original Bind not retained");
        Ensure(View.Buckets[0].EligibleCount() == 0 && View.Buckets[1].EligibleCount() == 1 && !_replyStarted[2],
            "old A authority is cut; only B remains eligible until replacement independently binds");
        _refreshInitialized = true;
    }

    private async Task NewGenerationDuringRetryAsync(OwnedOperation logical)
    {
        _initialized = false;
        await ApplyGenerationSnapshotAsync(new(2, [GenerationEndpoint("route-b", "before")]));
        _generationRemoved = true;
        _retryClock!.ExpectReconnectTimer();
        await ApplyGenerationSnapshotAsync(new(3,
            [GenerationEndpoint("route-a", "after"), GenerationEndpoint("route-b", "before")]));
        await _retryClock.ReconnectTimerCreated.Task.WaitAsync(Bound);
        _retryClock.AdvanceReconnectOnly();
        Ensure(_retryClock.RetryFires == 0 && !logical.Task.IsCompleted, "advancing only the independent 1ms reconnect must not release the held 1s retry");
        await _generationFactories[2].ConnectEntered.Task.WaitAsync(Bound);
        OwnedOperation? reconnect = null;
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("DynamicClusterReconnect", "original new-generation reconnect");
            reconnect = SnapshotOperations().SingleOrDefault(operation => operation.Stage == "original new-generation reconnect" && !operation.Task.IsCompleted);
            return reconnect is not null;
        }, "original generation reconnect not retained under factory gate");
        _generationFactories[2].ReleaseConnect.TrySetResult();
        await ObserveAsync(reconnect!);
        await AwaitAsync(() => View.Topology.Endpoints.Length == 2 && View.Topology.Endpoints[0].Endpoint.Id == "route-a", "new generation Ready topology missing");
        Replacement = View.Topology.Endpoints[0].Connections.Single();
        await ReadBindAsync(2);
        await AwaitAsync(() =>
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            return SnapshotOperations().Count(operation => operation.Stage == "original BindContractDemand") == 3;
        }, "original new generation Bind not retained");
        CaptureGenerationTasks();
        Ensure(View.Buckets[0].EligibleCount() == 0 && View.Buckets[1].EligibleCount() == 1,
            "new generation is unbound while original B remains confirmed");
        _initialized = true;
    }

    internal async Task CleanupRetryAsync(Exception? primary)
    {
        List<Exception> failures = [];
        try
        {
            if (_retryDynamic) await CleanupGenerationAsync(primary);
            else await CleanupAsync(primary);
        }
        catch (Exception error) { failures.Add(error); }
        bool alreadyQuarantined;
        lock (Quarantined) alreadyQuarantined = Quarantined.Contains(this);
        var ownersBeforeTimerRelease = SnapshotOperations();
        var producersClosed = _initialized && (!_refreshStarted || _refreshInitialized) &&
            ownersBeforeTimerRelease.All(operation => operation.Task.IsCompleted) &&
            ownersBeforeTimerRelease.Any(operation => operation.Stage == "original Stop" && operation.Task.IsCompleted) &&
            ownersBeforeTimerRelease.Any(operation => operation.Stage == "original client Dispose" && operation.Task.IsCompleted);
        if (alreadyQuarantined || !producersClosed)
        {
            // A base cleanup exception is not evidence that a launcher/logical delay or
            // its cold producers exited. Never dispose their still-owned timer/clock.
            if (!alreadyQuarantined)
            {
                // Retain roots even if reopening a previously disposed gate itself fails.
                lock (Quarantined) Quarantined.Add(this);
                try { await QuarantineAsync(failures, "Retry timer producer graph is not proven closed."); }
                catch (Exception error) { failures.Add(error); }
            }
            failures.Add(new InvalidOperationException(
                "Original retry timer/clock retained with unknown producer roots; isolate/terminate this test process."));
            foreach (var operation in SnapshotOperations().Where(operation => operation.Task.IsCompleted))
                await JoinAsync(operation, failures);
            ThrowFailures(failures);
            return;
        }
        foreach (var operation in ownersBeforeTimerRelease) await JoinAsync(operation, failures);
        if (_retryClock?.RetryTimer is { } timer)
        {
            try { await JoinAsync(Own("original retained retry timer Dispose", timer.DisposeAsync().AsTask()), failures); }
            catch (Exception error) { failures.Add(error); }
        }
        if (_retryClock is not null)
        {
            var drained = Own("original manual timer drain", _retryClock.WaitForTimersDrainedAsync());
            await JoinAsync(drained, failures);
        }
        if (SnapshotOperations().Any(operation => !operation.Task.IsCompleted))
        {
            // The base gates are already disposed in the proven-terminal branch. No
            // producer is resumed here; retain timer Dispose/drain originals themselves.
            lock (Quarantined) Quarantined.Add(this);
            failures.Add(new InvalidOperationException(
                "Original timer Dispose/drain exit unknown; roots retained, isolate/terminate this test process."));
        }
        foreach (var operation in SnapshotOperations().Where(operation => operation.Task.IsCompleted)) await JoinAsync(operation, failures);
        ThrowFailures(failures);
    }

    private sealed record RetrySelectionCapture(string[] Ids, long[] Generations, ulong Mask, int Selected);
    private sealed class RetrySelector : ISharpLinkEndpointSelector
    {
        private readonly object _gate = new();
        private readonly List<RetrySelectionCapture> _captures = [];
        internal Action? BeforeReturn;
        internal RetrySelectionCapture[] Captures { get { lock (_gate) return _captures.ToArray(); } }
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            var ids = new string[context.Count];
            var generations = new long[context.Count];
            var selected = -1;
            for (var index = 0; index < context.Count; index++)
            {
                ids[index] = context[index].Endpoint.Id;
                generations[index] = context[index].Generation;
                if (selected < 0 && (context.ExcludedMask & (1UL << index)) == 0) selected = index;
            }
            lock (_gate) _captures.Add(new(ids, generations, context.ExcludedMask, selected));
            BeforeReturn?.Invoke();
            return selected;
        }
    }

    private sealed class DelayedOneRetry(RetryClock clock) : ISharpLinkRetryPolicy
    {
        internal int Calls;
        internal SharpLinkRetryContext Last;
        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Last = context;
            Interlocked.Increment(ref Calls);
            clock.ArmFromEvaluateThread();
            return new(context.Attempt == 1 && context.ErrorCode == SharpLinkErrorCode.Unavailable, TimeSpan.FromSeconds(1));
        }
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private int _evaluateThread;
        private int _expectReconnect;
        private long _retryDue;
        private int _retryFires;
        internal TaskCompletionSource RetryTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReconnectTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ITimer? RetryTimer { get; private set; }
        internal int RetryFires => Volatile.Read(ref _retryFires);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        internal void ArmFromEvaluateThread() => Volatile.Write(ref _evaluateThread, Environment.CurrentManagedThreadId);
        internal void ExpectReconnectTimer() => Volatile.Write(ref _expectReconnect, 1);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var thread = Environment.CurrentManagedThreadId;
            var retry = dueTime == TimeSpan.FromSeconds(1) && Volatile.Read(ref _evaluateThread) == thread &&
                Interlocked.CompareExchange(ref _evaluateThread, 0, thread) == thread;
            var timer = _inner.CreateTimer(retry ? value => { Interlocked.Increment(ref _retryFires); callback(value); } : callback,
                state, dueTime, period);
            if (retry)
            {
                RetryTimer = timer;
                _retryDue = GetTimestamp() + dueTime.Ticks;
                RetryTimerCreated.TrySetResult(); // Only AFTER the actual inner timer was registered.
            }
            if (dueTime == TimeSpan.FromMilliseconds(1) && Interlocked.Exchange(ref _expectReconnect, 0) == 1)
                ReconnectTimerCreated.TrySetResult();
            return timer;
        }
        internal void AdvanceReconnectOnly() => _inner.Advance(TimeSpan.FromMilliseconds(1));
        internal void ResumeRetry()
        {
            Ensure(RetryTimer is not null && RetryFires == 0 && _retryDue > GetTimestamp(), "the exact original retry timer is still held");
            _inner.Advance(TimeSpan.FromTicks(_retryDue - GetTimestamp()));
            Ensure(RetryFires == 1, "advance must release the captured original retry timer exactly once");
        }
        internal Task WaitForTimersDrainedAsync() => _inner.WaitForTimersDrainedAsync();
    }
}
