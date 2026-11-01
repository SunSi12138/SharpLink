using System.Buffers.Binary;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Candidate SOURCE controls, not executed. Actual Session/pending/pooled sink ownership;
// manual connection is NOT full pool-owned Connect/readloop/Stop evidence.
[NotInParallel("dispatcher-pool")]
public sealed class ClientDuplexSinkRetentionOwnershipTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public Task SessionCloseKeepsTerminalMoveNextPinnedUntilOriginalPendingFinishes()
        => RunCase(async fixture =>
        {
            fixture.Initialize(); await fixture.CompleteFiniteProducerOrigin();
            var before = fixture.Snapshot("origin-exited-pending-pin");
            Ensure(before.PendingPresent && before.ProducerOperations == 1 && before.LeaseState == fixture.OriginalLease,
                "single ORIGINAL pin transferred to exact pending, no second retain");
            fixture.Session.NotifyDisconnected(fixture.CloseReason);
            var consumer = fixture.Original.MoveNextAsync().AsTask();
            Ensure(!consumer.IsCompleted && fixture.Connection.PendingCalls.Contains(fixture.RequestId),
                "actual terminal MoveNext waits pin; pending completion must not wait this consumer");
            fixture.Connection.PendingCalls.FailAllPendingRequests(fixture.CloseReason);
            Ensure(ReferenceEquals(await Observe(consumer), fixture.CloseReason), "original Session error delivered after pending release");
            var after = fixture.Snapshot("pending-released");
            Ensure(after.ProducerOperations == 0 && !after.PendingPresent && after.GroupScalarLease == fixture.OriginalLease,
                "one release, immutable identity retained, exact pending removed");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            await fixture.DisposeOriginalConsumer();
            Ensure(PooledAsyncStreamDispatcher<int>.RetainedCountForTests == 1, "original returns only after actual consumer and pending owners finish");
        });

    [Test]
    public Task ExplicitDisposeStaysSameGenerationUntilOriginalPendingFinishes()
        => RunCase(async fixture =>
        {
            fixture.Initialize(); await fixture.CompleteFiniteProducerOrigin();
            fixture.Session.NotifyDisconnected(fixture.CloseReason);
            await fixture.DisposeOriginalConsumer();
            var disposing = fixture.Snapshot("consumer-disposed-pending-pin");
            Ensure(disposing.PendingPresent && disposing.ProducerOperations == 1 && disposing.PoolCount == 0 &&
                (disposing.LeaseState & ~3L) == (fixture.OriginalLease & ~3L), "Dispose completes without pool return/producer wait; same generation pinned");
            var other = PooledAsyncStreamDispatcher<int>.Rent(default, fixture.Codec);
            try
            {
                Ensure(!ReferenceEquals(other, fixture.Original), "actual pool cannot re-rent original pending sink");
                fixture.Connection.PendingCalls.FailAllPendingRequests(fixture.CloseReason);
                await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
                Ensure(PooledAsyncStreamDispatcher<int>.RetainedCountForTests == 1, "last original pin returns disposed old generation");
            }
            finally { other.Complete(exception: null); await other.DisposeAsync(); }
        });

    [Test]
    public Task ActualConsumerAbandonmentReentersPendingWithoutWaitingForItsOriginalPin()
        => RunCase(async fixture =>
        {
            fixture.Initialize(); await fixture.CompleteFiniteProducerOrigin();
            Ensure(fixture.Connection.PendingCalls.Contains(fixture.RequestId), "original pending still live before actual abandonment");
            // Dispose invokes the real route-installed abandonment callback while the original
            // generation is Disposing. It must not wait for the producer count before callback.
            await fixture.DisposeOriginalConsumer().WaitAsync(Limit);
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            var after = fixture.Snapshot("actual-consumer-abandonment");
            Ensure(!after.PendingPresent && after.ProducerOperations == 0 && after.RouteCount == 0 && after.PoolCount == 1,
                "actual abandonment callback terminalizes pending, releases original once and returns consumer without a cycle");
        });

    [Test]
    public Task TerminalBeforeHeldOriginalRoutePublishCannotRecycleItsGeneration()
        => RunCase(async fixture =>
        {
            fixture.Initialize(publishRoute: false);
            // Hold BETWEEN actual pending publication and the same production-used route step.
            fixture.Connection.PendingCalls.TryComplete(fixture.RequestId, PendingCallCompletionReason.ConnectionClosed, fixture.CloseReason);
            await fixture.DisposeOriginalConsumer();
            var held = fixture.Snapshot("terminal-before-route");
            Ensure(held.ProducerOperations == 1 && held.PoolCount == 0 && !held.PendingPresent,
                "terminal sink use done alone cannot release origin/late-route identity");
            fixture.PublishOriginalRoute();
            await fixture.CompleteUnstartedOrigin();
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            Ensure(fixture.Session.StreamManager.ActiveStreamCount == 0 && PooledAsyncStreamDispatcher<int>.RetainedCountForTests == 1,
                "late exact route drained; settled origin+pending release original once");
        });

    [Test]
    public Task AlreadyCancelledRegistrationTransfersBeforeSynchronousTerminalCallback()
        => RunCase(async fixture =>
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            fixture.Initialize(token: canceled.Token);
            Ensure(fixture.Group.IsAdopted && !fixture.Connection.PendingCalls.Contains(fixture.RequestId), "exact adoption precedes synchronous canceled registration terminal");
            await WaitForResponseDrain(fixture.Group);
            Ensure(fixture.Snapshot("canceled-before-origin-finally").ProducerOperations == 1, "original origin still uses one pin");
            await fixture.CompleteUnstartedOrigin();
            Ensure(await Observe(fixture.Original.MoveNextAsync().AsTask()) is OperationCanceledException, "original cancellation delivered, no pool/consumer cycle");
            Ensure(fixture.Snapshot("canceled-after-origin-finally").ProducerOperations == 0, "origin-last release exactly once");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        });

    [Test]
    public Task PublishedClockFailureLeavesTransferredPinForActualOriginCleanup()
        => RunCase(async fixture =>
        {
            var expected = new IOException("actual postpublication original clock callback");
            var clock = new RegistrationClock(expected);
            var setup = Capture(() => fixture.Initialize(clock: clock));
            Ensure(ReferenceEquals(setup, expected) && fixture.Group.IsAdopted, "published=true setup error must not revert to unadopted release");
            Ensure(fixture.Snapshot("published-clock-failure").ProducerOperations == 1, "pending callback finished but original origin has not");
            await fixture.CompleteUnstartedOrigin();
            Ensure(ReferenceEquals(await Observe(fixture.Original.MoveNextAsync().AsTask()), expected), "exact original error delivered after final pin release");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
        });

    [Test]
    public Task CapacityRejectionBeforeAdoptionKeepsOriginalOriginReleaseAuthority()
        => RunCase(async fixture =>
        {
            var setup = Capture(() => fixture.Initialize(fillCapacity: true));
            Ensure(setup is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted } && !fixture.Group.IsAdopted,
                "actual full pending table rejects before identity transfer");
            await fixture.CompleteUnstartedOrigin();
            Ensure(fixture.Snapshot("unadopted-origin-release").ProducerOperations == 0, "original origin releases its own untransferred pin once");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            // This source control has no returned response stream; fixture owns its consumer cleanup.
            fixture.Original.Complete(setup);
        });

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(6)]
    public Task EveryTerminalReasonReleasesExactOriginalPendingPin(int terminal)
        => RunCase(async fixture =>
        {
            fixture.Initialize(); await fixture.CompleteFiniteProducerOrigin();
            var reason = terminal switch
            {
                0 => PendingCallCompletionReason.RemoteStreamComplete,
                1 => PendingCallCompletionReason.RemoteError,
                2 => PendingCallCompletionReason.UserCancellation,
                3 => PendingCallCompletionReason.DeadlineExceeded,
                4 => PendingCallCompletionReason.ConsumerAbandoned,
                5 => PendingCallCompletionReason.ConnectionClosed,
                _ => PendingCallCompletionReason.SendFailure
            };
            var error = reason == PendingCallCompletionReason.RemoteStreamComplete ? null : new IOException("actual terminal owner " + reason);
            fixture.Connection.PendingCalls.TryComplete(fixture.RequestId, reason, error);
            var outcome = await Observe(fixture.Original.MoveNextAsync().AsTask());
            Ensure(error is null ? outcome is null : ReferenceEquals(outcome, error), "actual terminal owner preserves its exact primary");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            var snapshot = fixture.Snapshot("terminal-" + reason);
            Ensure(snapshot.ProducerOperations == 0 && !snapshot.PendingPresent && snapshot.PendingCapacity == 0,
                "all terminal paths consume only one original pin and original pending capacity");
        });

    [Test]
    public Task OriginLastOriginalReleaseFailureRemainsStrictPhysicalOutcome()
        => RunCase(async fixture =>
        {
            fixture.Initialize();
            fixture.Connection.PendingCalls.TryComplete(fixture.RequestId, PendingCallCompletionReason.ConnectionClosed, fixture.CloseReason);
            await fixture.DisposeOriginalConsumer();
            var expected = new IOException("actual origin-last original retention release callback");
            fixture.Original.SetBeforeReturnTransitionForTests(() =>
            {
                fixture.Original.SetBeforeReturnTransitionForTests(null);
                throw expected;
            });
            var origin = await Observe(fixture.CompleteUnstartedOrigin());
            Ensure(ReferenceEquals(PhysicalTaskFailureException.GetRaw(origin!), expected), "original release failure not classified as delivered business terminal");
            fixture.AcceptAlreadyAssertedOriginFailure(expected);
            var supervisor = Read<FrameworkTaskSupervisor>(fixture.Client, "_frameworkTasks");
            supervisor.Seal();
            Ensure(ReferenceEquals(await Observe(supervisor.DrainAsync()), expected), "actual existing supervisor preserves exact independent origin release failure");
            Ensure(fixture.Snapshot("origin-release-failed").ProducerOperations == 0 &&
                fixture.Group.FinishOriginSinkUseForRetention() == 0 && fixture.Group.FinishPendingSinkUseForRetention() == 0,
                "original count decremented once despite failed pool return callback; no second claim");
            // A thrown return hook is NOT proof of successful pool insertion; do not assert it.
        });

    [Test]
    public Task PendingLastOriginalReleaseFailureStaysWithLateTerminalCaller()
        => RunCase(async fixture =>
        {
            fixture.Initialize(); await fixture.CompleteFiniteProducerOrigin();
            fixture.Session.NotifyDisconnected(fixture.CloseReason);
            await fixture.DisposeOriginalConsumer();
            var expected = new IOException("actual pending-last original retention release callback");
            fixture.Original.SetBeforeReturnTransitionForTests(() =>
            {
                fixture.Original.SetBeforeReturnTransitionForTests(null);
                throw expected;
            });
            var terminal = Capture(() => fixture.Connection.PendingCalls.FailAllPendingRequests(fixture.CloseReason));
            Ensure(ReferenceEquals(terminal, expected), "physical reducer already closed: late release failure remains exact terminal caller outcome");
            await fixture.Group.SupervisionTaskForDiagnostics.WaitAsync(Limit);
            Ensure(fixture.Snapshot("pending-release-failed").ProducerOperations == 0 &&
                fixture.Group.FinishPendingSinkUseForRetention() == 0 && fixture.Group.FinishOriginSinkUseForRetention() == 0,
                "claim is consumed exactly once, original identity unchanged even when release callback fails");
        });

    private static async Task WaitForResponseDrain(ClientStreamPublicationGroup group)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (group.HasResponseDrainRootForDiagnostics)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= Limit) throw new TimeoutException("Actual response cleanup did not exit.");
            await Task.Yield();
        }
    }

    private sealed class RegistrationClock(Exception error) : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private int _armed;
        internal void Arm() => _armed = 1;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp()
        {
            // Armed only after all fixture construction and deadline creation, immediately
            // before actual registration. OnProducingRegistered owns the first timestamp read.
            if (Interlocked.Exchange(ref _armed, 0) != 0) throw error;
            return _inner.GetTimestamp();
        }
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            => _inner.CreateTimer(callback, state, due, period);
    }
    private static async Task RunCase(Func<Fixture, Task> body)
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var fixture = new Fixture(); var failures = new List<Exception>();
        try { await body(fixture); }
        catch (Exception error) { failures.Add(new InvalidOperationException("candidate body", error)); }
        finally
        {
            await fixture.DisposeObserved(failures);
            PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        }
        if (failures.Count != 0) throw new AggregateException("Candidate body and independent cleanup outcomes", failures);
    }

    private sealed class Fixture
    {
        private System.IO.Pipelines.Pipe? _input;
        private System.IO.Pipelines.Pipe? _output;
        private CancellationTokenSource? _connectionCancellation;
        internal SharpLinkClient Client = null!;
        internal RpcSession Session = null!;
        internal ClientConnection Connection = null!;
        internal ClientStreamPublicationGroup Group = null!;
        internal PooledAsyncStreamDispatcher<int> Original = null!;
        internal IRpcCodec<int> Codec = null!;
        internal long OriginalLease;
        internal long RequestId;
        internal readonly SharpLinkException CloseReason = new(SharpLinkErrorCode.ConnectionClosed, "causal exact Session closure");
        private Task? _origin;
        private bool _originFailureAlreadyAsserted;
        private bool _originRegistrationRetentionOwned;
        private Task? _originalConsumerDispose;
        private readonly List<RpcRequestOperation<int>> _capacityBlockers = [];
        private bool _groupAdmitted;
        private bool _selectionReserved;
        private bool _selectionTransferred;
        internal void Initialize(CancellationToken token = default, bool publishRoute = true,
            TimeProvider? clock = null, bool fillCapacity = false)
        {
            _input = new();
            _output = new();
            Client = ClientBuilderTestHelper.Build(new TestClientTransportFactory(),
                builder => { if (clock is not null) builder.UseTimeProvider(clock); });
            var context = (SharpLinkRuntimeContext)Client.RuntimeContext;
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("sink-retention-causal", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ClientOptions(context));
            Session.NotifyConnected();
            _connectionCancellation = new CancellationTokenSource();
            Connection = new ClientConnection(Client, Session, _connectionCancellation, 8, context);
            Codec = context.Codecs.GetCodec<int>();
            Original = PooledAsyncStreamDispatcher<int>.Rent(default, Codec);
            OriginalLease = Original.RetainForRegistration();
            _originRegistrationRetentionOwned = true;
            Group = new ClientStreamPublicationGroup(1);
            Group.AdmitSupervision(Client, "causal original Duplex group");
            _groupAdmitted = true;
            Ensure(Connection.TryReserveExactCallAdmission(), "original selection");
            _selectionReserved = true;
            Group.BeginResponseRoutePublication();
            Group.PrepareRegistration(Connection, new TestRpcBindingPublication(Session), OriginalLease);
            _selectionTransferred = true;
            if (fillCapacity)
                for (var index = 0; index < 8; index++)
                    _capacityBlockers.Add(Connection.PendingCalls.Rent(Codec, out _));
            try
            {
                var deadline = clock is null ? default : RpcDeadline.Create(Limit, clock);
                if (clock is RegistrationClock failureClock) failureClock.Arm();
                RequestId = Connection.PendingCalls.RegisterStream(PendingCallKind.DuplexStreaming, Original,
                    deadline, token, producerGroup: Group);
                if (publishRoute) PublishOriginalRoute();
            }
            finally
            {
                if (Group.IsAdopted) RequestId = Group.RequestId; // published=true callback failure retains exact identity
                if (publishRoute) Group.CompleteResponseRoutePublication();
            }
        }
        internal async Task CompleteFiniteProducerOrigin()
        {
            var packet = Session.RentFrameWriter();
            using (packet.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.Cancellable, (ulong)RequestId))
            {
                var bytes = packet.GetSpan(16);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, Group.Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], 1);
                packet.Advance(16);
            }
            var prepared = Session.PrepareOutboundFrame(packet, Group.ProducerToken);
            Ensure(Connection.PendingCalls.TryPublishRequest(RequestId, Session, prepared, false, Group.ProducerToken,
                Connection.PendingCalls, out _, out _, Group), "actual head publication");
            Connection.PendingCalls.ReceiveRequestAccepted((ulong)RequestId);
            async Task Origin()
            {
                Exception? failure = null;
                try { await Group.RunWriterAsync(new EmptyWriter(Codec), Group.ProducerToken); }
                catch (Exception error) { failure = PhysicalTaskFailureException.GetRaw(error); }
                finally
                {
                    try { ReleaseOriginRegistrationRetention(); }
                    catch (Exception error) { failure = ClientStreamPublicationGroup.AddPhysical(failure, error); }
                }
                if (failure is not null) throw PhysicalTaskFailureException.From(failure);
            }
            _origin = Origin(); Group.AttachOriginTask(_origin);
            await _origin.WaitAsync(Limit);
            Ensure(!Group.SupervisionTaskForDiagnostics.IsCompleted, "finite producer does not await original future logical terminal");
        }
        internal SnapshotRecord Snapshot(string stage)
        {
            var value = new SnapshotRecord(stage, Session.Id.ToString(), RequestId, OriginalLease,
                Read<long>(Original, "_leaseState"), Read<int>(Original, "_producerOperations"),
                Connection.PendingCalls.Contains(RequestId), Connection.PendingCalls.ActiveCount,
                Session.StreamManager.ActiveStreamCount, PooledAsyncStreamDispatcher<int>.RetainedCountForTests,
                Group.ResponseDispatcherLease, Group.HasProducerLifetime);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value));
            return value;
        }
        private void ReleaseOriginRegistrationRetention()
        {
            if (!_originRegistrationRetentionOwned) return;
            _originRegistrationRetentionOwned = false;
            var owned = Group is { IsAdopted: true } ? Group.FinishOriginSinkUseForRetention() : OriginalLease;
            if (owned != 0) Original.ReleaseRegistrationRetention(owned);
        }
        internal void PublishOriginalRoute()
        {
            try
            {
                using var route = SharpLinkClient.ContractCallSite.PrepareDuplexResponseRoute(Connection, RequestId, Original, Group);
                route.Publish();
            }
            finally { Group.CompleteResponseRoutePublication(); }
        }
        internal async Task CompleteUnstartedOrigin()
        {
            async Task Origin()
            {
                Exception? error = null;
                try { await Group.FinishAsync(); }
                catch (Exception failure) { error = PhysicalTaskFailureException.GetRaw(failure); }
                finally
                {
                    try { ReleaseOriginRegistrationRetention(); }
                    catch (Exception failure) { error = ClientStreamPublicationGroup.AddPhysical(error, failure); }
                }
                if (error is not null) throw PhysicalTaskFailureException.From(error);
            }
            _origin = Origin(); Group.AttachOriginTask(_origin);
            await _origin.WaitAsync(Limit);
        }
        internal void AcceptAlreadyAssertedOriginFailure(Exception expected)
        {
            Ensure(_origin is { IsCompleted: true, Exception.InnerException: { } error } &&
                ReferenceEquals(PhysicalTaskFailureException.GetRaw(error), expected),
                "only exact completed original physical task failure can be marked already observed");
            _originFailureAlreadyAsserted = true;
        }
        internal Task DisposeOriginalConsumer()
        {
            if (_originalConsumerDispose is not null) return _originalConsumerDispose;
            if (Original is null) return Task.CompletedTask; // No consumer was acquired by staged setup.
            // Keep the exact one-time physical operation even when a body wait times out.
            // Cleanup re-observes this task, never invokes Dispose on a possibly pooled instance.
            try { _originalConsumerDispose = Original.DisposeAsync().AsTask(); }
            catch (Exception failure) { _originalConsumerDispose = Task.FromException(failure); }
            return _originalConsumerDispose;
        }
        internal async Task DisposeObserved(List<Exception> failures)
        {
            if (Connection is not null)
                Attempt(failures, "original pending cleanup", () => Connection.PendingCalls.FailAllPendingRequests(CloseReason));
            if (_groupAdmitted)
            {
                // RegisterStream/route setup can throw before the normal route-finally is reached.
                Attempt(failures, "unfinished response-route phase", () => Group.CompleteResponseRoutePublication());
                if (_origin is null)
                {
                    async Task UnstartedCleanup()
                    {
                        try { await Group.FinishAsync(); }
                        finally { ReleaseOriginRegistrationRetention(); }
                    }
                    _origin = UnstartedCleanup();
                    Attempt(failures, "unstarted origin assignment", () => Group.AttachOriginTask(_origin));
                }
                if (!_originFailureAlreadyAsserted) await AttemptAsync(failures, "original physical join", () => _origin);
            }
            else Attempt(failures, "unadmitted original registration pin", ReleaseOriginRegistrationRetention);
            if (_selectionReserved && !_selectionTransferred)
            {
                _selectionReserved = false;
                Attempt(failures, "untransferred original selection", () => Connection!.ReleaseCallAdmissionReservation());
            }
            foreach (var operation in _capacityBlockers)
            {
                var terminal = await Observe(operation.AsValueTask().AsTask());
                if (terminal is not SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed })
                    failures.Add(new InvalidOperationException("Actual capacity blocker terminal was not ConnectionClosed.", terminal));
            }
            _capacityBlockers.Clear();
            await AttemptAsync(failures, "original consumer Dispose", DisposeOriginalConsumer);
            if (Connection is not null) await AttemptAsync(failures, "Connection.Dispose", () => Connection.DisposeAsync().AsTask());
            if (Session is not null) await AttemptAsync(failures, "Session.Dispose", () => Session.DisposeAsync().AsTask());
            if (Client is not null) await AttemptAsync(failures, "Client.Dispose", () => Client.DisposeAsync().AsTask());
            if (_connectionCancellation is not null) Attempt(failures, "connection cancellation source", () => _connectionCancellation.Dispose());
            if (_input is not null)
            {
                await AttemptAsync(failures, "input pipe writer", () => _input.Writer.CompleteAsync().AsTask());
                await AttemptAsync(failures, "input pipe reader", () => _input.Reader.CompleteAsync().AsTask());
            }
            if (_output is not null)
            {
                await AttemptAsync(failures, "output pipe reader", () => _output.Reader.CompleteAsync().AsTask());
                await AttemptAsync(failures, "output pipe writer", () => _output.Writer.CompleteAsync().AsTask());
            }
        }
    }
    private sealed record SnapshotRecord(string Stage, string SessionId, long FullRequestId, long OriginalLease,
        long LeaseState, int ProducerOperations, bool PendingPresent, int PendingCapacity, int RouteCount, int PoolCount,
        long GroupScalarLease, bool ProducerAdopted);
    private static T Read<T>(object instance, string name)
        => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing diagnostic field " + name)); // Read only; no mutation.
    private readonly struct EmptyWriter(IRpcCodec<int> codec) : IRpcClientStreamWriter
    { public ValueTask WriteAsync(IRpcClientStreamSink sink, long id, CancellationToken token = default) => new(sink.SendClientStreamAsync(id, 1, new EmptyStream(), codec, token)); }
    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    { public int Current => 0; public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken token = default) => this; public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static async Task<Exception?> Observe(Task task) { try { await task.WaitAsync(Limit); return null; } catch (Exception error) { return error; } }
    private static void Attempt(List<Exception> errors, string stage, Action action)
    { var error = Capture(action); if (error is not null) errors.Add(new InvalidOperationException(stage, error)); }
    private static async Task AttemptAsync(List<Exception> errors, string stage, Func<Task> action)
    { try { var error = await Observe(action()); if (error is not null) errors.Add(new InvalidOperationException(stage, error)); } catch (Exception error) { errors.Add(new InvalidOperationException(stage, error)); } }
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
