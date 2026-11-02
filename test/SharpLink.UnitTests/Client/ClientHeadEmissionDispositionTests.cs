using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks.Sources;
using System.Threading.Channels;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Receipt controls below exercise the actual core and exceptional carrier, not a copied
// outcome reducer. The final controls use actual pending registration + Session pump/fence.
public sealed class ClientHeadEmissionDispositionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly List<object> Quarantine = [];
    private static readonly IRpcCodec<int> Codec = new IntCodec();

    [Test]
    public void OriginalCoreAndReturnFailureKeepIndependentFullGraphs()
    {
        var source = new AggregateException(new IOException("source one"), new OperationCanceledException("real source two"));
        var cleanup = new AggregateException(new IOException("return one"), new InvalidOperationException("return two"));
        var returns = 0;
        var operation = NewOperation(_ => { ++returns; throw cleanup; });
        var version = operation.FailureObservationVersion;
        operation.SetError(source);
        var result = operation.ConsumeFailure(version);
        Ensure(result.Consumed && result.Status == ValueTaskSourceStatus.Faulted &&
            ReferenceEquals(result.CoreFailure, source) && ReferenceEquals(result.ReturnFailure, cleanup) && returns == 1,
            "one original core consume preserves the full source and separate return graph");
    }

    [Test]
    public void IncompleteCoreIsNotReturnedOrReplacedBySyntheticCompletion()
    {
        var returns = 0;
        var operation = NewOperation(_ => ++returns);
        var version = operation.FailureObservationVersion;
        var pending = operation.ConsumeFailure(version);
        Ensure(!pending.Consumed && pending.Status == ValueTaskSourceStatus.Pending && returns == 0,
            "not-terminal is not ownership permission to reset the core");
        var terminal = new IOException("actual later completion");
        operation.SetError(terminal);
        var actual = operation.ConsumeFailure(version);
        Ensure(actual.Consumed && ReferenceEquals(actual.CoreFailure, terminal) && actual.ReturnFailure is null && returns == 1,
            "same original remains usable and has one actual return");
    }

    [Test]
    public void StaleVersionCannotReturnANewPooledIncarnation()
    {
        var returns = 0;
        var operation = NewOperation(_ => ++returns);
        var oldVersion = operation.FailureObservationVersion;
        operation.SetError(new IOException("old original"));
        Ensure(operation.ConsumeFailure(oldVersion).Consumed && returns == 1, "old original consumed once");
        operation.Initialize(29, Codec);
        var newVersion = operation.FailureObservationVersion;
        var stale = operation.ConsumeFailure(oldVersion);
        Ensure(!stale.Consumed && stale.CoreFailure is InvalidOperationException && returns == 1 &&
            operation.GetStatus(newVersion) == ValueTaskSourceStatus.Pending,
            "stale version cannot reset or refund the new original owner");
        var current = new IOException("new original");
        operation.SetError(current);
        var actual = operation.ConsumeFailure(newVersion);
        Ensure(ReferenceEquals(actual.CoreFailure, current) && returns == 2, "new owner remains independently consumable");
    }

    [Test]
    public async Task PositivePublicationReceiptConsumesKnownPropagationOnce()
    {
        var returns = 0;
        var operation = NewOperation(_ => ++returns);
        var source = new IOException("caller-owned origin");
        var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(operation, 17, source, null);
        // Component publication boundary: actual SetError must return before the receipt. The
        // real table-selection control below separately covers who is permitted to record it.
        receipt.RecordSelection(operation, 17);
        operation.SetError(source);
        receipt.RecordPublication(operation, 17);
        await ExpectLeaves(receipt.ObserveFailureAsync().AsTask(), source);
        Ensure(returns == 1, "known propagation was consumed and returned exactly once");
    }

    [Test]
    public async Task SameReferenceInIndependentCallerAndTerminalStagesIsNotDeduplicated()
    {
        var returns = 0;
        var operation = NewOperation(_ => ++returns);
        var shared = new IOException("distinct stages share an object");
        var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(operation, 17, shared, null);
        operation.SetError(shared); // No own-selection/publication receipt: a distinct terminal.
        await ExpectLeaves(receipt.ObserveFailureAsync().AsTask(), shared, shared);
        Ensure(returns == 1, "same reference is not a substitute for origin provenance");
    }

    [Test]
    public async Task OwnPublishedOriginAndThrowingReturnKeepSeparateOccurrences()
    {
        var shared = new OperationCanceledException("intentional core and return occurrences");
        var returns = 0;
        var operation = NewOperation(_ => { ++returns; throw shared; });
        var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(operation, 17, shared, null);
        receipt.RecordSelection(operation, 17);
        operation.SetError(shared);
        receipt.RecordPublication(operation, 17);
        await ExpectLeaves(receipt.ObserveFailureAsync().AsTask(), shared, shared);
        Ensure(returns == 1, "pool return does not mask the real original cancellation or copy core propagation twice");
    }

    [Test]
    public async Task CarrierDelegatesOriginalPendingContinuationAndConsumesOnce()
    {
        var returns = 0;
        var operation = NewOperation(_ => ++returns);
        var caller = new IOException("caller stage");
        var terminal = new InvalidOperationException("independent later terminal");
        var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(operation, 17, caller, null);
        var observed = receipt.ObserveFailureAsync().AsTask();
        Ensure(!observed.IsCompleted && returns == 0, "carrier awaits the actual original core, not a replacement completion");
        operation.SetError(terminal);
        await ExpectLeaves(observed, caller, terminal);
        Ensure(returns == 1, "one delegated continuation consumes one original core");
    }

    // Pure outcome-reader component controls. These real Task outcomes are NOT presented as
    // pump emissions or evidence of queue adoption; the Session controls below cover adoption.
    [Test]
    public async Task RawOutcomePreservesSingleCauseAndIntentionalAggregateShape()
    {
        var single = new IOException("single actual source cause");
        var one = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        one.SetException(single);
        Ensure(ReferenceEquals(await ReadRawOutcome(one.Task), single), "one cause keeps its original public error shape");
        var intentional = new AggregateException(new IOException("inner one"), new OperationCanceledException("inner two"));
        var aggregate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        aggregate.SetException(intentional);
        Ensure(ReferenceEquals(await ReadRawOutcome(aggregate.Task), intentional),
            "intentional AggregateException remains one exact source cause, not recursively flattened");
    }

    [Test]
    public async Task RawOutcomeKeepsOrderedActualTaskFaultSiblings()
    {
        var first = new IOException("actual first sibling");
        var second = new OperationCanceledException("actual second sibling");
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetException([first, second]);
        var graph = await ReadRawOutcome(source.Task) as AggregateException;
        Ensure(graph is not null && graph.InnerExceptions.Count == 2 &&
            ReferenceEquals(graph.InnerExceptions[0], first) && ReferenceEquals(graph.InnerExceptions[1], second),
            "multiple actual Task siblings preserve complete original order without type filtering");
    }

    [Test]
    public async Task RawCancellationOutcomeComesFromTheOriginalTask()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetCanceled();
        Exception? actual = null;
        try { await source.Task; } catch (Exception error) { actual = error; }
        var observed = await ReadRawOutcome(source.Task);
        // TaskCanceledException may be materialized for each GetResult. Its Task identifies
        // the consumed ORIGINAL source, not token/type evidence authorizing suppression.
        Ensure(actual is TaskCanceledException && observed is TaskCanceledException cancellation &&
            ReferenceEquals(cancellation.Task, source.Task), "real source cancellation remains an owned raw outcome");
    }

    private static ValueTask<Exception?> ReadRawOutcome(Task source)
        => (ValueTask<Exception?>)(typeof(ClientStreamPublicationGroup).GetMethod("ReadHeadOutcomeAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source])
            ?? throw new InvalidOperationException("Actual production outcome reader missing."));

    [Test]
    public Task ActualPendingSelectionPublishesCallerOriginOnce()
        => RunFixture(async f =>
        {
            var origin = new IOException("actual caller origin");
            var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(f.Operation, f.Group.RequestId, origin, null);
            receipt.CompletePending(f.Connection, f.Group);
            var observed = f.Consume(receipt);
            await ExpectLeaves(observed, origin);
            f.Accept(observed);
            Ensure(f.Group.ProducerToken.IsCancellationRequested, "actual selected pending owner canceled producer");
        });

    [Test]
    public Task ActualCompetingPendingTerminalDoesNotConsumeCallerOrigin()
        => RunFixture(async f =>
        {
            var origin = new IOException("actual caller origin");
            var terminal = new InvalidOperationException("actual competing remote terminal");
            Ensure(f.Connection.PendingCalls.TryComplete(f.Group.RequestId, PendingCallCompletionReason.RemoteError, terminal),
                "competing terminal must actually win the original slot");
            var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(f.Operation, f.Group.RequestId, origin, null);
            receipt.CompletePending(f.Connection, f.Group);
            var observed = f.Consume(receipt);
            await ExpectLeaves(observed, origin, terminal);
            f.Accept(observed);
        });

    [Test]
    public Task SelectedDeadlineReadinessLeavesActualHeldSourceAndFenceOwned()
        => RunFixture(async f =>
        {
            await f.Publish();
            var raw = f.RawSource();
            await f.Output.Entered.Task.WaitAsync(Limit);
            Ensure(!raw.IsCompleted, "actual pump completion is held before pending terminal");
            var ready = f.Own(f.Group.ObserveHeadEmissionAsync().AsTask());
            f.Group.RetainHeadTask(ready);
            var deadline = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "exact selected deadline");
            Ensure(f.Connection.PendingCalls.TryComplete(f.Group.RequestId, PendingCallCompletionReason.DeadlineExceeded, deadline),
                "deadline owns actual pending selection");
            var decision = await ready.WaitAsync(Limit);
            Ensure(decision.Kind == ClientStreamPublicationGroup.HeadEmissionKind.PendingTerminalSelected &&
                decision.Source is null && decision.Failure is null && !raw.IsCompleted,
                "logical readiness does not consume, cancel or synthesize a failure for the actual raw source");
            var pending = f.Consume();
            await ExpectLeaves(pending, deadline);
            f.Accept(pending);
            var physical = f.BeginPhysical();
            Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1 && f.Connection.CallAdmissionReservationCount == 1,
                "real held output/fence retains physical charge after caller terminal");
            f.Output.Release.TrySetResult();
            await physical.WaitAsync(Limit);
            Ensure(raw.IsCompletedSuccessfully, "the SAME raw source actually completes successfully after release");
        });

    [Test]
    public Task ActualSourceSuccessIsClaimedBeforeLaterPendingTerminal()
        => RunFixture(async f =>
        {
            await f.Publish();
            var raw = f.RawSource();
            f.Output.Release.TrySetResult();
            await raw.WaitAsync(Limit);
            var decision = await f.Group.ObserveHeadEmissionAsync();
            Ensure(decision.Kind == ClientStreamPublicationGroup.HeadEmissionKind.SourceConsumed &&
                ReferenceEquals(decision.Source, raw) && decision.Failure is null,
                "positive consume names the exact original source, not a cancellation wrapper");
            var terminal = new IOException("later independent terminal");
            Ensure(f.Connection.PendingCalls.TryComplete(f.Group.RequestId, PendingCallCompletionReason.RemoteError, terminal),
                "later terminal uses actual pending slot");
            var pending = f.Consume();
            await ExpectLeaves(pending, terminal);
            f.Accept(pending);
        });

    [Test]
    public Task SelectedDeadlineKeepsLateActualOutputIOExceptionPhysical()
        => ActualOutputFaultAfterHead(new IOException("late physical output"), terminalBeforeOutput: true);

    [Test]
    public Task SelectedDeadlineKeepsLateActualOutputCancellationCausePhysical()
        => ActualOutputFaultAfterHead(new OperationCanceledException("late physical output, not readiness cancellation"), terminalBeforeOutput: true);

    [Test]
    public Task ActualEmissionObserverSelectsTerminalBeforeFullRawAggregateIsConsumed()
        => ActualOutputFaultAfterHead(new AggregateException(new IOException("actual output sibling one"),
            new OperationCanceledException("actual output sibling two")), terminalBeforeOutput: false);

    private static Task ActualOutputFaultAfterHead(Exception injected, bool terminalBeforeOutput)
        => RunFixture(async f =>
        {
            f.Output.Failure = injected;
            await f.Publish();
            var raw = f.RawSource();
            await f.Output.Entered.Task.WaitAsync(Limit);
            Ensure(!raw.IsCompleted, "genuine output is held before its actual completion source");
            var deadline = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "selected before physical output fault");
            Task<ClientStreamPublicationGroup.HeadEmissionDecision>? ready = null;
            Task? physical = null;
            Task<bool>? acceptedCleanupFence = null;
            if (terminalBeforeOutput)
            {
                ready = f.Own(f.Group.ObserveHeadEmissionAsync().AsTask());
                f.Group.RetainHeadTask(ready);
                Ensure(f.Connection.PendingCalls.TryComplete(f.Group.RequestId, PendingCallCompletionReason.DeadlineExceeded, deadline),
                    "deadline must win while actual raw output is held");
                var selected = await ready.WaitAsync(Limit);
                Ensure(selected.Kind == ClientStreamPublicationGroup.HeadEmissionKind.PendingTerminalSelected &&
                    selected.Source is null && selected.Failure is null && !raw.IsCompleted,
                    "readiness terminal never claims unfinished raw outcome");
                physical = f.BeginPhysical();
                Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1 &&
                    f.Connection.CallAdmissionReservationCount == 1,
                    "actual physical finish/fence and original capacity remain held after logical terminal");
                acceptedCleanupFence = await f.CaptureAcceptedCleanupFence(raw);
                f.AssertHeadOutputHeld(raw); // Recheck after observation, immediately before release.
            }
            f.Output.Release.TrySetResult();
            var rawOutcome = await ActualStageFailure(raw);
            Ensure(rawOutcome is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } normalized &&
                ReferenceEquals(normalized.InnerException, injected),
                "actual pump normalizes one exact transport cause; nested intentional aggregate is unchanged", rawOutcome);
            var emission = rawOutcome!;
            f.Accept(raw);
            if (acceptedCleanupFence is not null)
            {
                var fenceOutcome = await ActualStageFailure(acceptedCleanupFence);
                Ensure(ReferenceEquals(fenceOutcome, emission),
                    "the positively accepted original cleanup fence receives the same exact emission failure", fenceOutcome);
                f.Accept(acceptedCleanupFence);
            }
            Ensure(f.EmissionObserver.Calls == 1 && f.EmissionObserver.RequestId == f.Group.RequestId &&
                ReferenceEquals(f.EmissionObserver.Observed, emission),
                "the actual emission observer received the same normalized source before raw completion");
            if (!terminalBeforeOutput)
            {
                // Manual ClientConnection has no live-pool OnDisconnected forwarder. This
                // explicit component seam lets the actual emission observer own SendFailure.
                ready = f.Own(f.Group.ObserveHeadEmissionAsync().AsTask());
                f.Group.RetainHeadTask(ready);
                var selected = await ready.WaitAsync(Limit);
                Ensure(selected.Kind == ClientStreamPublicationGroup.HeadEmissionKind.PendingTerminalSelected &&
                    selected.Source is null && selected.Failure is null,
                    "observer-first selected receipt leaves the final raw graph physically owned");
            }
            var pending = f.Consume();
            await ExpectLeaves(pending, terminalBeforeOutput ? deadline : emission);
            f.Accept(pending);
            physical ??= f.BeginPhysical();
            var physicalOutcome = await ActualStageFailure(physical);
            var cleanup = f.CleanupSource();
            var cleanupOutcome = await ActualStageFailure(cleanup);
            Ensure(cleanupOutcome is AggregateException c && c.InnerExceptions.Count == 2 &&
                ReferenceEquals(c.InnerExceptions[0], emission) && ReferenceEquals(c.InnerExceptions[1], emission),
                "cleanup coordinator retains its catch stage and the same original inner publication stage", cleanupOutcome);
            Ensure(physicalOutcome is PhysicalTaskFailureException,
                "physical cancellation/error never escapes without its strict raw envelope", physicalOutcome);
            var residual = ((PhysicalTaskFailureException)physicalOutcome!).RawException;
            Ensure(residual is AggregateException rawPair && rawPair.InnerExceptions.Count == 2 &&
                ReferenceEquals(rawPair.InnerExceptions[1], emission) &&
                rawPair.InnerExceptions[0] is AggregateException fencePair && fencePair.InnerExceptions.Count == 2 &&
                ReferenceEquals(fencePair.InnerExceptions[0], cleanupOutcome) && ReferenceEquals(fencePair.InnerExceptions[1], emission),
                "exact cleanup then fence then unconsumed raw stages remain complete and ordered", residual);
            f.Accept(cleanup);
            f.Accept(physical);
            await f.AssertSupervisionAndStop(residual);
        });

    [Test]
    public Task OwnPublishedCallerOriginKeepsActualTerminalCallbackPhysical()
    {
        var callback = new IOException("actual pending completion observer");
        var observer = new ThrowingCompletionObserver(callback);
        return RunFixture(async f =>
        {
            var origin = new IOException("actual caller-owned source stage");
            var receipt = new ClientStreamPublicationGroup.HeadFailureReceipt<int>(f.Operation, f.Group.RequestId, origin, null);
            receipt.CompletePending(f.Connection, f.Group);
            var pending = f.Consume(receipt);
            await ExpectLeaves(pending, origin);
            f.Accept(pending);
            Ensure(observer.Calls == 1 && ReferenceEquals(observer.Observed, origin),
                "actual observer sees original published-origin stage once and then throws independently");
            var physical = f.BeginPhysical();
            var outcome = await ActualStageFailure(physical);
            Ensure(outcome is PhysicalTaskFailureException envelope && ReferenceEquals(envelope.RawException, callback),
                "accepted terminal batch retains exact callback marker, not a caller-origin replay", outcome);
            f.Accept(physical);
            await f.AssertSupervisionAndStop(callback);
        }, observer);
    }

    // Actual source-stage reader for negative controls: wait arbitration remains independent
    // and bounded; only Task's accessor envelope is normalized, never the actual graph.
    private static async Task<Exception?> ActualStageFailure(Task source)
    {
        var waiter = source.WaitAsync(Limit);
        Exception? observed = null;
        try { await waiter; }
        catch (Exception failure)
        {
            var forwarded = source.IsFaulted && waiter.Exception is { } wg && source.Exception is { } sg &&
                wg.InnerExceptions.SequenceEqual(sg.InnerExceptions, ReferenceEqualityComparer.Instance);
            forwarded |= waiter.IsCanceled && source.IsCanceled; // Timeout-only wrapper.
            if (!forwarded)
            {
                var retained = new RetainedObservation(source, waiter, waiter.Exception ?? failure);
                lock (Quarantine) Quarantine.Add(retained);
                retained.Observe();
                throw new AggregateException("Independent wait failure is not an expected source outcome.", waiter.Exception ?? failure);
            }
            observed = failure;
        }
        if (!source.IsCompleted)
            throw new InvalidOperationException("A completed source-forwarding waiter did not join its source.");
        if (source.Exception is not { } graph) return observed;
        return graph.InnerExceptions.Count == 1 ? graph.InnerExceptions[0] : graph;
    }

    private sealed class ThrowingCompletionObserver(Exception failure) : IPendingCallCompletionObserver
    {
        internal int Calls;
        internal Exception? Observed;
        public void OnResponseObserved() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        { ++Calls; Observed = completion.Exception; throw failure; }
    }

    private sealed class RecordingEmissionObserver(IRequestEmissionFailureObserver actual) : IRequestEmissionFailureObserver
    {
        internal int Calls;
        internal long RequestId;
        internal Exception? Observed;
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            ++Calls; RequestId = requestId; Observed = exception;
            actual.OnRequestEmissionFailure(requestId, exception);
        }
    }

    private static RpcRequestOperation<int> NewOperation(Action<RpcRequestOperation<int>> onReturn)
    {
        var operation = new RpcRequestOperation<int>(onReturn);
        operation.Initialize(17, Codec);
        return operation;
    }

    private static async Task ExpectLeaves(Task source, params Exception[] expected)
    {
        var waiter = source.WaitAsync(Limit); // Timeout-only wrapper, retained independently.
        Exception? caught = null;
        Exception? independentWait = null;
        try { await waiter; }
        catch (Exception error)
        {
            caught = error;
            var forwarded = source.IsFaulted && waiter.Exception is { } waitGraph && source.Exception is { } sourceGraph &&
                waitGraph.InnerExceptions.SequenceEqual(sourceGraph.InnerExceptions, ReferenceEqualityComparer.Instance);
            // This wrapper has no external cancellation input. A canceled wrapper forwards
            // source cancellation; a true timeout remains Faulted even if source later cancels.
            forwarded |= waiter.IsCanceled && source.IsCanceled;
            if (!forwarded) independentWait = waiter.Exception ?? error;
        }
        var sourceTerminal = source.IsCompleted;
        if (independentWait is not null || !sourceTerminal)
        {
            var retained = new RetainedObservation(source, waiter, independentWait ?? caught);
            lock (Quarantine) Quarantine.Add(retained);
            retained.Observe(); // Includes already-completed originals after timeout arbitration.
            throw new AggregateException("Actual wait failed; timeout/ownership containment is irreversible.",
                independentWait ?? caught ?? new InvalidOperationException("Original source remained incomplete."));
        }
        var outcome = source.Exception is { } graph ? graph : caught;
        var leaves = Leaves(outcome).ToArray();
        Ensure(leaves.Length == expected.Length && leaves.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "exact complete original outcome graph has unexpected/missing stage leaves", outcome);
    }

    private sealed class RetainedObservation(Task source, Task waiter, Exception? waitFailure)
    {
        // Strong identities and independent wait failure remain available after the test exits.
        internal readonly Task Source = source;
        internal readonly Task Waiter = waiter;
        internal readonly Exception? WaitFailure = waitFailure;
        internal Exception? ActualOutcome;
        internal void Observe()
            => _ = Source.ContinueWith(done =>
            {
                ActualOutcome = done.Exception;
                if (done.IsCanceled)
                    try { done.GetAwaiter().GetResult(); } catch (Exception actual) { ActualOutcome = actual; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static IEnumerable<Exception> Leaves(Exception? error)
    {
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var leaf in Leaves(inner)) yield return leaf;
        }
        else if (error is not null) yield return error;
    }
    private static void Ensure(bool condition, string message, Exception? cause = null)
    { if (!condition) throw new InvalidOperationException(message, cause); }

    private static async Task RunFixture(Func<Fixture, Task> body, IPendingCallCompletionObserver? observer = null)
    {
        var fixture = new Fixture();
        var failures = new List<Exception>();
        try { fixture.Initialize(observer); await body(fixture); }
        catch (Exception primary) { failures.Add(primary); }
        await fixture.Finish(failures);
        if (failures.Count != 0) throw new AggregateException("Independent body and owned teardown outcomes.", failures);
    }

    private sealed class Fixture
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();
        private readonly List<Task> _owned = [];
        private readonly Dictionary<Task, Exception[]> _accepted = [];
        private readonly HashSet<Task> _observed = [];
        private readonly List<Exception> _late = [];
        private SharpLinkClient? _client;
        private RpcSession? _session;
        private ClientConnection? _connection;
        private CancellationTokenSource? _cancel;
        private Task? _physical;
        private Task? _connectionDispose;
        private Task? _sessionDispose;
        private Task? _stop;
        private bool _pendingConsumed;
        internal ClientConnection Connection => _connection!;
        internal ClientStreamPublicationGroup Group { get; private set; } = null!;
        internal RpcRequestOperation<int> Operation { get; private set; } = null!;
        internal HeldOutput Output { get; private set; } = null!;
        internal RecordingEmissionObserver EmissionObserver { get; private set; } = null!;
        internal void Initialize(IPendingCallCompletionObserver? observer)
        {
            _client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
            var context = (SharpLinkRuntimeContext)_client.RuntimeContext;
            Output = new HeldOutput(_output.Writer);
            _session = RpcSessionTestFixture.CreateSessionOverTestTransport("owned-head-disposition", _input.Reader, Output,
                RpcSessionTestFixture.ClientOptions(context));
            _session.NotifyConnected();
            _connection = new ClientConnection(_client, _session, new CancellationTokenSource(), 8, context);
            EmissionObserver = new RecordingEmissionObserver(_connection.PendingCalls);
            _cancel = new CancellationTokenSource();
            Group = new ClientStreamPublicationGroup(1);
            Group.AdmitSupervision(_client, "head disposition control");
            Ensure(_connection.TryReserveExactCallAdmission(), "actual connection selection reservation");
            Group.PrepareRegistration(_connection, new TestRpcBindingPublication(_session));
            Operation = _connection.PendingCalls.Rent(Codec, PendingCallKind.ClientStreaming, default, _cancel.Token,
                out _, observer, producerGroup: Group);
            _ = Group.TakeRegisteredOperation<int>();
        }
        internal Task<T> Own<T>(Task<T> task) { _owned.Add(task); return task; }
        internal Task Own(Task task) { _owned.Add(task); return task; }
        internal void Accept(Task task)
        {
            Ensure(task.IsCompleted, "only an actually joined asserted source can be acknowledged");
            _accepted.Add(task, task.Exception?.InnerExceptions.ToArray() ?? []);
        }
        internal Task<int> Consume(ClientStreamPublicationGroup.HeadFailureReceipt<int>? receipt = null)
        {
            Ensure(!_pendingConsumed, "the original pending operation has one consumer");
            _pendingConsumed = true;
            return Own(receipt is null ? Operation.AsValueTask().AsTask() : receipt.ObserveFailureAsync().AsTask());
        }
        internal async Task Publish()
        {
            var writer = _session!.RentFrameWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.Cancellable, (ulong)Group.RequestId))
            {
                var span = writer.GetSpan(16);
                BinaryPrimitives.WriteUInt64LittleEndian(span, Group.Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
                writer.Advance(16);
            }
            var packet = _session.PrepareOutboundFrame(writer, Group.ProducerToken);
            Ensure(Connection.PendingCalls.TryPublishRequest(Group.RequestId, _session, packet, true, Group.ProducerToken,
                EmissionObserver, out var emission, out var rejection, Group, RpcHeadEmissionObservation.PendingOwnedHead),
                "actual accepted transaction", rejection);
            Ensure(emission.IsCompletedSuccessfully, "selected finalization does not return a cancellation-forwarding head wrapper");
            await emission;
        }
        // This diagnostic is intentionally pinned to the reviewed .NET 10.0.2 channel
        // implementation. It never consumes/requeues a frame or adds a production probe.
        internal async Task<Task<bool>> CaptureAcceptedCleanupFence(Task<bool> raw)
        {
            AssertHeadOutputHeld(raw);
            var witness = new AcceptedFenceWitness(_session!);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= Limit)
                    throw new TimeoutException("Actual cleanup-fence admission was not witnessed within the original bound.");
                var snapshot = witness.Capture(this, raw); // Synchronous: no frame in an async state machine.
                // Register the actual source before ANY assertion/exit can reach Finish's Release.
                if (snapshot.Completion is { } captured) _ = Own(captured);
                var failure = snapshot.Failure;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= Limit)
                {
                    var timeout = new TimeoutException("Actual cleanup-fence observation exceeded the original bound.");
                    failure = failure is null ? timeout : new AggregateException(failure, timeout);
                }
                if (failure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                AssertHeadOutputHeld(raw); // Monitor is no longer held; recheck sole-reader exclusion.
                Ensure(!snapshot.Invalid, "unexpected frame/ownership shape in the held normal queue");
                if (snapshot.Completion is { } completion)
                {
                    Ensure(snapshot.Total == 3 && snapshot.Cancels == 1 && snapshot.Terminals == 1 &&
                        snapshot.Fences == 1 && !completion.IsCompleted,
                        "actual accepted cohort must be exactly Cancel, stream1 terminal and its early fence");
                    return completion;
                }
                await Task.Yield(); // Finite phase observation, not a delay guessed to imply admission.
            }
        }
        internal void AssertHeadOutputHeld(Task<bool> raw)
            => Ensure(IsHeadOutputHeld(raw), "actual head Flush/raw source/release/session no longer prove a paused sole reader");
        private bool IsHeadOutputHeld(Task<bool> raw)
            => Output.Entered.Task.IsCompletedSuccessfully && !Output.Release.Task.IsCompleted && !raw.IsCompleted &&
                _session!.ProtocolPhase == RpcSessionProtocolPhase.Ready && _session.IsConnected;

        private struct FenceSnapshot
        {
            internal int Total, Cancels, Terminals, Fences;
            internal bool Invalid;
            internal Task<bool>? Completion;
            internal Exception? Failure;
        }
        private sealed class AcceptedFenceWitness
        {
            private readonly object _channel;
            private readonly object _sync;
            private readonly MethodInfo _enumerate;

            internal AcceptedFenceWitness(RpcSession session)
            {
                var assembly = typeof(Channel<OwnedFrame>).Assembly;
                Ensure(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ==
                    "10.0.2+44525024595742ebe09023abe709df51de65009b",
                    "accepted-fence observation requires the reviewed channel runtime bytes/version");
                var pump = typeof(RpcSession).GetMethod("CapturePumpForStop", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, null) ?? throw new InvalidOperationException("Actual held Session pump is absent.");
                Ensure(pump.GetType() == typeof(RpcSession.SendPump), "unexpected actual pump type");
                _channel = typeof(RpcSession.SendPump).GetField("_normalQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pump) ?? throw new InvalidOperationException("Actual normal queue is absent.");
                var expected = assembly.GetType("System.Threading.Channels.SingleConsumerUnboundedChannel`1", throwOnError: true)!
                    .MakeGenericType(typeof(OwnedFrame));
                Ensure(_channel.GetType() == expected && _channel is Channel<OwnedFrame>, "unexpected actual normal-channel type");
                _sync = expected.GetProperty("SyncObj", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_channel)
                    ?? throw new InvalidOperationException("Actual channel synchronization object is absent.");
                var items = expected.GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_channel);
                Ensure(ReferenceEquals(_sync, items) && _sync.GetType().IsGenericType &&
                    _sync.GetType().GetGenericTypeDefinition().FullName ==
                        "System.Collections.Concurrent.SingleProducerSingleConsumerQueue`1" &&
                    _sync.GetType().GenericTypeArguments.SequenceEqual([typeof(OwnedFrame)]),
                    "channel SyncObj must be its actual reviewed producer-synchronized item queue");
                var contract = assembly.GetType("System.Threading.Channels.IDebugEnumerable`1", throwOnError: true)!
                    .MakeGenericType(typeof(OwnedFrame));
                Ensure(contract.IsInstanceOfType(_channel), "actual channel has no reviewed non-consuming enumeration interface");
                _enumerate = contract.GetMethod("GetEnumerator")!;
                Ensure(_enumerate.GetParameters().Length == 0 && _enumerate.ReturnType == typeof(IEnumerator<OwnedFrame>),
                    "unexpected non-consuming enumeration signature");
            }

            // The writer monitor alone cannot stop the lock-free consumer. The independently
            // held original Flush is required before, during and after this entire snapshot.
            // Only counters and the original completion Task leave; no OwnedFrame/Memory/owner
            // or enumerator is stored on this object or in an async state machine.
            internal FenceSnapshot Capture(Fixture fixture, Task<bool> raw)
            {
                var result = new FenceSnapshot();
                var entered = false;
                IEnumerator<OwnedFrame>? enumerator = null;
                try
                {
                    Monitor.TryEnter(_sync, ref entered);
                    if (entered)
                    {
                        if (!fixture.IsHeadOutputHeld(raw)) result.Invalid = true;
                        else
                        {
                            enumerator = (IEnumerator<OwnedFrame>?)_enumerate.Invoke(_channel, null);
                            if (enumerator is null) result.Invalid = true;
                            else
                            {
                                while (enumerator.MoveNext())
                                {
                                    var frame = enumerator.Current;
                                    ++result.Total;
                                    if (result.Total > 3) { result.Invalid = true; break; }
                                    Inspect(frame, fixture.Group, ref result);
                                }
                            }
                        }
                        if (!fixture.IsHeadOutputHeld(raw)) result.Invalid = true;
                    }
                }
                catch (Exception failure) { result.Failure = failure; }
                finally
                {
                    try { enumerator?.Dispose(); }
                    catch (Exception cleanup) { AddFailure(ref result, cleanup); }
                    // Dispose failure must never prevent releasing the actual producer monitor.
                    if (entered)
                        try { Monitor.Exit(_sync); }
                        catch (Exception cleanup) { AddFailure(ref result, cleanup); }
                }
                return result;
            }
            private static void AddFailure(ref FenceSnapshot result, Exception failure)
                => result.Failure = result.Failure is null ? failure : new AggregateException(result.Failure, failure);

            private static void Inspect(in OwnedFrame frame, ClientStreamPublicationGroup group, ref FenceSnapshot result)
            {
                if (frame.PublicationRole == RpcFramePublicationRole.StreamCleanupFence)
                {
                    ++result.Fences;
                    // Capture before other validation: even a malformed known source is retained.
                    result.Completion ??= frame.FlushCompletion?.Task;
                    result.Invalid |= result.Fences != 1 || !ReferenceEquals(frame.Publication, group) ||
                        frame.Length != 0 || frame.ForceFlush || frame.IsProtocolProgress ||
                        frame.FlushCompletion is null || frame.FlushCompletion.Task.IsCompleted;
                    return;
                }
                var span = frame.Memory.Span;
                if (frame.IsProtocolProgress || frame.ForceFlush || frame.FlushCompletion is not null ||
                    span.Length != frame.Length || span.Length < ProtocolV2Constants.HeaderBytes ||
                    span[0] != ProtocolV2Constants.Magic ||
                    BinaryPrimitives.ReadInt32LittleEndian(span[1..]) != span.Length - ProtocolV2Constants.HeaderBytes ||
                    BinaryPrimitives.ReadInt64LittleEndian(span[7..]) != group.RequestId)
                { result.Invalid = true; return; }
                if (span[5] == (byte)ProtocolV2FrameType.Cancel)
                {
                    ++result.Cancels;
                    result.Invalid |= result.Cancels != 1 || frame.Publication is not null ||
                        frame.PublicationRole != RpcFramePublicationRole.None || span[6] != (byte)ProtocolV2FrameFlags.None ||
                        span.Length != ProtocolV2Constants.HeaderBytes + 1 ||
                        span[ProtocolV2Constants.HeaderBytes] != (byte)ProtocolV2CancelReason.DeadlineExceeded;
                }
                else if (span[5] == (byte)ProtocolV2FrameType.StreamComplete)
                {
                    ++result.Terminals;
                    result.Invalid |= result.Terminals != 1 || !ReferenceEquals(frame.Publication, group) ||
                        frame.PublicationRole != RpcFramePublicationRole.StreamTerminal || frame.StreamId != 1 ||
                        span[6] != (byte)ProtocolV2FrameFlags.Error || span.Length < ProtocolV2Constants.HeaderBytes + 2 ||
                        BinaryPrimitives.ReadUInt16LittleEndian(span[ProtocolV2Constants.HeaderBytes..]) != 1;
                }
                else result.Invalid = true;
            }
        }
        internal Task<bool> RawSource()
            => Own((Task<bool>)(typeof(ClientStreamPublicationGroup).GetField("_headEmissionSource", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Group) ?? throw new InvalidOperationException("Actual accepted source was not retained.")));
        internal Task BeginPhysical()
        {
            if (_physical is not null) return _physical;
            _physical = Own(Group.FinishAsync());
            Group.AttachOriginTask(_physical);
            Own(Group.SupervisionTaskForDiagnostics);
            return _physical;
        }
        internal Task CleanupSource()
            => Own(((TaskCompletionSource)(typeof(ClientStreamPublicationGroup).GetField("_cleanupCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Group) ?? throw new InvalidOperationException("Actual cleanup owner is absent."))).Task);
        internal async Task AssertSupervisionAndStop(Exception expectedRaw)
        {
            var supervision = Group.SupervisionTaskForDiagnostics;
            var supervised = await ActualStageFailure(supervision);
            Ensure(supervised is PhysicalTaskFailureException envelope && ReferenceEquals(envelope.RawException, expectedRaw),
                "actual strict anchor transports the complete exact raw graph", supervised);
            Accept(supervision);
            _connectionDispose ??= Own(Connection.DisposeAsync().AsTask());
            await _connectionDispose.WaitAsync(Limit);
            _sessionDispose ??= Own(_session!.DisposeAsync().AsTask());
            await _sessionDispose.WaitAsync(Limit);
            _stop ??= Own(_client!.StopAsync().AsTask());
            var stopped = await ActualStageFailure(_stop);
            Ensure(ReferenceEquals(stopped, expectedRaw), "one actual Stop preserves the full original strict physical graph", stopped);
            Accept(_stop);
        }
        private async Task Join(Task task, List<Exception> failures)
        {
            var waiter = task.WaitAsync(Limit); // Timeout only: no caller cancellation input.
            try { await waiter; }
            catch (Exception waitError)
            {
                var forwarded = task.IsFaulted && waiter.Exception is { } waitGraph && task.Exception is { } sourceGraph &&
                    waitGraph.InnerExceptions.SequenceEqual(sourceGraph.InnerExceptions, ReferenceEqualityComparer.Instance);
                // With this timeout-only waiter, a canceled waiter can only forward the actual
                // source cancellation; timeout remains Faulted even if source later cancels.
                forwarded |= waiter.IsCanceled && task.IsCanceled;
                if (!forwarded) failures.Add(waiter.Exception ?? waitError);
            }
            CaptureCompleted(task, failures);
        }
        private void CaptureCompleted(Task task, List<Exception> failures)
        {
            if (!task.IsCompleted || !_observed.Add(task)) return;
            if (task.Exception is { } graph && (!_accepted.TryGetValue(task, out var expected) ||
                !graph.InnerExceptions.SequenceEqual(expected, ReferenceEqualityComparer.Instance))) failures.Add(graph);
            if (task.IsCanceled)
                try { task.GetAwaiter().GetResult(); } catch (Exception cancellation) { failures.Add(cancellation); }
        }
        private bool AllJoined(List<Exception> failures)
        {
            // Latch terminality BEFORE the complete sweep. No late-fault race can turn an
            // earlier incomplete sample into permission to release without observing it.
            if (_owned.Any(task => !task.IsCompleted)) { Retain(); return false; }
            foreach (var task in _owned) CaptureCompleted(task, failures);
            return true;
        }
        internal async Task Finish(List<Exception> failures)
        {
            Output?.Release.TrySetResult();
            try { _cancel?.Cancel(); } catch (Exception error) { failures.Add(error); }
            // Actual fixture consumers normally own the logical negative. If setup/body failed
            // before consumption, retain and report its outcome; no cancellation-type waiver.
            if (Operation is not null && !_pendingConsumed) _ = Consume();
            // Readiness must exit before the same group can adopt its one physical origin.
            foreach (var task in _owned.ToArray()) await Join(task, failures);
            if (!AllJoined(failures)) return;
            if (Group is not null)
            {
                try { _ = BeginPhysical(); } catch (Exception error) { failures.Add(error); }
                foreach (var task in _owned.ToArray()) await Join(task, failures);
                if (!AllJoined(failures)) return;
                try
                {
                    Ensure(!Group.HasProducerLifetime || Connection.PendingCalls.ActiveCount == 0 &&
                        Connection.CallAdmissionReservationCount == 0 && _session!.QueuedSendBytes == 0,
                        "joined actual physical owners must refund original pending/selection/output charges");
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (_connection is not null && _connectionDispose is null)
                await Stage(() => _connectionDispose = _connection.DisposeAsync().AsTask(), failures);
            if (!AllJoined(failures)) return;
            if (_session is not null && _sessionDispose is null)
                await Stage(() => _sessionDispose = _session.DisposeAsync().AsTask(), failures);
            if (!AllJoined(failures)) return;
            if (_client is not null && _stop is null)
                await Stage(() => _stop = _client.StopAsync().AsTask(), failures);
            if (!AllJoined(failures)) return;
            await Stage(() => _input.Writer.CompleteAsync().AsTask(), failures);
            await Stage(() => _output.Reader.CompleteAsync().AsTask(), failures);
            if (!AllJoined(failures)) return;
            try { _cancel?.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        private async Task Stage(Func<Task> start, List<Exception> failures)
        {
            try { await Join(Own(start()), failures); }
            catch (Exception synchronous) { failures.Add(synchronous); }
        }
        private void Retain()
        {
            lock (Quarantine) Quarantine.Add(this);
            foreach (var task in _owned.ToArray())
                _ = task.ContinueWith(done =>
                {
                    Exception? outcome = done.Exception;
                    if (done.IsCanceled) try { done.GetAwaiter().GetResult(); } catch (Exception canceled) { outcome = canceled; }
                    if (outcome is not null) lock (_late) _late.Add(outcome);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class HeldOutput(System.IO.Pipelines.PipeWriter inner) : System.IO.Pipelines.PipeWriter
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure;
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int hint = 0) => inner.GetMemory(hint);
        public override Span<byte> GetSpan(int hint = 0) => inner.GetSpan(hint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? error = null) => inner.Complete(error);
        public override ValueTask CompleteAsync(Exception? error = null) => inner.CompleteAsync(error);
        public override async ValueTask<System.IO.Pipelines.FlushResult> FlushAsync(CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            if (Failure is { } failure) throw failure; // Actual original Flush outcome, never an injected completion signal.
            return await inner.FlushAsync(token);
        }
    }
    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> writer)
        { BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value); writer.Advance(4); }
        public int Deserialize(in ReadOnlySequence<byte> value) => BinaryPrimitives.ReadInt32LittleEndian(value.FirstSpan);
    }
}
