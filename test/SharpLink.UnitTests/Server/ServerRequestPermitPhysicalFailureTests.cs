using System.Buffers;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

// Actual permit/invocation component controls. The scripted stub isolates the static
// invocation fast path; it is NOT generated TCP/retirement matrix coverage.
public partial class ServerRequestPermitTests
{
    [Test]
    public async Task CompletedTailCannotRefundInSealBeforeCleanupOwnershipWindow()
    {
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            var original = new OperationCanceledException("completed original tail error");
            permit.RetainPhysicalTail();
            var accepted = false;
            permit.ReportPhysicalFailure(GeneratedPhysicalFailureStage.EnumeratorDispose, original, ref accepted);
            permit.ReleasePhysicalTail();
            Ensure(accepted && !permit.HasPhysicalTails, "original error retained at actual count zero");
            var gate = (Lock)typeof(ServerRequestPermit).GetField("_physicalTailGate",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(permit)!;
            var flags = typeof(ServerRequestPermit).GetField("_physicalOutcomeFlags",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Task<bool> begin;
            lock (gate)
            {
                begin = Task.Run(permit.BeginPhysicalServiceCleanup);
                Ensure(SpinWait.SpinUntil(() => ((int)flags.GetValue(permit)! & 2) != 0,
                    TimeSpan.FromSeconds(5)), "Begin published seal before blocking on original gate");
                // This thread owns the existing reentrant gate, so Dispose can test its actual
                // final-release predicate while Begin is deterministically unable to set Owned.
                permit.Dispose();
                Ensure(connection.ActiveCalls == 1 && server.ActiveCallCountForDiagnostics == 1,
                    "seal alone cannot consume error or refund completed-tail ownership");
            }
            Ensure(await begin.WaitAsync(TimeSpan.FromSeconds(5)), "original cleanup owner claims after gate handoff");
            Ensure(ReferenceEquals(await ObservePhysicalFailureAsync(permit.JoinPhysicalTailsAsync().AsTask()), original),
                "same error remains available after competing Dispose");
            permit.FinishPhysicalServiceCleanup();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "only actual cleanup finish refunds the deferred owner");
        });
    }

    [Test]
    public async Task NoTailInvocationSealsBeforeAnyLaterFirstRetain()
    {
        await WithPhysicalPermitAsync((server, connection, permit) =>
        {
            Ensure(!permit.BeginPhysicalServiceCleanup(), "no tail selects the ordinary cleanup path");
            Exception? failure = null;
            try { permit.RetainPhysicalTail(); }
            catch (Exception error) { failure = error; }
            Ensure(failure is InvalidOperationException && !permit.HasPhysicalTails,
                "late first retain is rejected, not stranded behind a nonexistent cleanup owner");
            permit.Dispose();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "late-retain rejection does not strand actual capacity");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task FirstRetainAndInvocationSealHaveOneDefinitiveOwner()
    {
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            using var start = new ManualResetEventSlim();
            Exception? retainFailure = null;
            var retaining = Task.Run(() =>
            {
                start.Wait();
                try { permit.RetainPhysicalTail(); }
                catch (Exception error) { retainFailure = error; }
            });
            var sealing = Task.Run(() => { start.Wait(); return permit.BeginPhysicalServiceCleanup(); });
            start.Set();
            await Task.WhenAll(retaining, sealing).WaitAsync(TimeSpan.FromSeconds(5));
            if (sealing.Result)
            {
                Ensure(retainFailure is null && permit.HasPhysicalTails,
                    "accepted first retain is included in the exact cleanup phase");
                permit.Dispose();
                permit.ReleasePhysicalTail();
                await permit.JoinPhysicalTailsAsync();
                permit.FinishPhysicalServiceCleanup();
            }
            else
            {
                Ensure(retainFailure is InvalidOperationException && !permit.HasPhysicalTails,
                    "ordinary path won seal and refused the competing first retain");
                permit.Dispose();
            }
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "both legal seal orders settle actual capacity once");
        });
    }

    [Test]
    public async Task CompletedPhysicalFailureRemainsOwnedAtZeroTailCount()
    {
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            var original = new OperationCanceledException("independent physical disposal");
            permit.RetainPhysicalTail();
            var accepted = false;
            permit.ReportPhysicalFailure(GeneratedPhysicalFailureStage.EnumeratorDispose, original, ref accepted);
            permit.ReleasePhysicalTail();
            Ensure(accepted && !permit.HasPhysicalTails, "raw failure transferred before actual count zero");
            permit.Dispose();
            Ensure(connection.ActiveCalls == 1 && server.ActiveCallCountForDiagnostics == 1,
                "count zero cannot release ownership before invocation seals physical outcome");
            Ensure(permit.BeginPhysicalServiceCleanup(), "completed failed tail still creates the cleanup owner");
            var observed = await ObservePhysicalFailureAsync(permit.JoinPhysicalTailsAsync().AsTask());
            Ensure(ReferenceEquals(observed, original), "count-zero join retains original OCE identity");
            permit.FinishPhysicalServiceCleanup();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "deferred release occurs after physical cleanup phase, not before the join");
        });
    }

    [Test]
    public async Task DeferredReleaseFailureCannotSkipCapacityOrBecomeDiscardedContinuation()
    {
        var releaseFailure = new InvalidOperationException("exact release hook failure");
        var hooks = new ServerRequestPermitTestHooks { ReleaseClaimed = () => throw releaseFailure };
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            permit.RetainPhysicalTail();
            Ensure(permit.BeginPhysicalServiceCleanup(), "cleanup ownership published before task attachment");
            permit.Dispose();
            permit.ReleasePhysicalTail();
            await permit.JoinPhysicalTailsAsync();
            Exception? observed = null;
            try { permit.FinishPhysicalServiceCleanup(); }
            catch (Exception failure) { observed = failure; }
            Ensure(ReferenceEquals(observed, releaseFailure), "current cleanup caller owns exact release error");
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "throwing release observer cannot skip either existing capacity refund");
            permit.Dispose();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "second disposal cannot refund twice");
        }, hooks);
    }

    [Test]
    public async Task StaticServerStreamCompletedTailFaultCannotBypassPhysicalServiceCleanup()
    {
        var original = new OperationCanceledException("static stream late physical failure");
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            var facts = new RpcMethodFacts(101, 202, RpcMethodKind.ServerStreaming, 0, true, true);
            var stub = new PhysicalOutcomeStub(() =>
            {
                permit.RetainPhysicalTail();
                var accepted = false;
                permit.ReportPhysicalFailure(GeneratedPhysicalFailureStage.EnumeratorDispose, original, ref accepted);
                Ensure(accepted, "stub transfers exact physical outcome");
                permit.ReleasePhysicalTail();
            });
            await using var registration = ServiceRegistration.CreateSingleton(typeof(object), stub,
                new object(), ownsService: false);
            // The component drives the real invocation dispatcher but keeps lifecycle escalation
            // out of scope. Strict supervisor ownership is verified directly below.
            Ensure(permit.TryProtectRegistration(registration, facts), "original registration protected once");
            SetServerState(server, 0); // Created: no actual listener/runtime-fault transition in this component.
            var resolved = new ResolvedMethodCall(registration, facts);
            var invocation = (ValueTask)typeof(SharpLinkServer).GetMethod("InvokeServiceAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server,
                new object?[] { resolved, connection, connection.Session, 81L,
                    ReadOnlySequence<byte>.Empty, null, CancellationToken.None,
                    new SharpLinkCallContextSnapshot(connection.Session.Id, null), permit })!;
            await invocation;
            Ensure(stub.Entries == 1, "actual static singleton invocation entered exactly once");
            Ensure(server.FrameworkTaskSnapshotForDiagnostics.TotalTracked == 1,
                "static server stream uses existing owned cleanup even after tail count is zero");
            var supervisor = (FrameworkTaskSupervisor)typeof(SharpLinkServer).GetField("_frameworkTasks",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
            supervisor.Seal();
            var observed = await ObservePhysicalFailureAsync(supervisor.DrainAsync());
            Ensure(ReferenceEquals(observed, original), "strict cleanup exposes the original raw physical OCE");
            permit.Dispose();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "static service cleanup completes before original capacity releases");
        }, expectedServerDisposeFailure: original);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OutgoingAndChildFailureHaveDistinctOwnersWithoutPrerequisiteDuplication(bool sameException)
    {
        await WithPhysicalPermitAsync(async (server, connection, permit) =>
        {
            var outgoingFailure = new OperationCanceledException("original outgoing physical stage");
            var childFailure = sameException ? outgoingFailure : new OperationCanceledException("child physical stage");
            var outgoing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = new ControlledFailureChild(childFailure);
            var manager = connection.Session.StreamManager;
            Ensure(manager.TryReservePreAdmissionStreams(82, 1, connection.Session.RuntimeContext.Buffers,
                static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true),
                "original child route reserved");
            Ensure(manager.TryCaptureRequestStreamsForInvocation(82, 1, out var capture), "whole route captured");
            manager.Register(82, 1, child);
            permit.RetainPhysicalTail();
            Ensure(permit.BeginPhysicalServiceCleanup(), "existing request owner owns cleanup");
            var inbound = manager.AbandonCapturedRequestStreamsAsync(ref capture, outgoing.Task,
                prerequisiteFailureOwnedByCaller: true);
            var cleanup = (Task)typeof(SharpLinkServer).GetMethod("CompletePhysicalServiceCleanupAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { permit, new ServiceLease(new object()), outgoing.Task, inbound })!;
            permit.AttachPhysicalServiceCleanup(cleanup);
            permit.Dispose();
            try
            {
                Ensure(!child.Entered.Task.IsCompleted, "child is retained while outgoing physical task is pending");
                outgoing.TrySetException(outgoingFailure);
                permit.ReleasePhysicalTail();
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Ensure(!cleanup.IsCompleted && connection.ActiveCalls == 1,
                    "outgoing failure does not skip actual child physical ownership");
                child.Release.TrySetResult();
                var result = await ObservePhysicalFailureAsync(cleanup);
                Ensure(result is PhysicalTaskFailureException,
                    "existing cleanup task transports independent OCE residual as a fault");
                var raw = ((PhysicalTaskFailureException)result!).RawException;
                Ensure(raw is AggregateException exact && exact.InnerExceptions.Count == 2 &&
                    ReferenceEquals(exact.InnerExceptions[0], outgoingFailure) &&
                    ReferenceEquals(exact.InnerExceptions[1], childFailure),
                    "one outgoing occurrence plus one child occurrence, even when same object; no tree dedup");
                Ensure(child.Disposals == 1 && connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                    "independent physical stages and original capacity finish once");
            }
            finally
            {
                outgoing.TrySetException(outgoingFailure);
                child.Release.TrySetResult();
                if (permit.HasPhysicalTails) permit.ReleasePhysicalTail();
                // Observe original owned outcome; never use this cleanup catch to accept the body.
                _ = await ObservePhysicalFailureAsync(cleanup);
                manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "fixture finished"));
            }
        });
    }

    private sealed class ControlledFailureChild(Exception failure) : IStreamDispatcher, IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? error) { }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult();
            await Release.Task;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task WithPhysicalPermitAsync(
        Func<SharpLinkServer, ServerConnectionState, ServerRequestPermit, Task> body,
        ServerRequestPermitTestHooks? hooks = null, Exception? expectedServerDisposeFailure = null)
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration().UseTransport(new IdleListener()).Build();
        await using var session = CreateSession("physical-permit-failure");
        var connection = CreateConnection(session);
        Ensure(connection.MarkReady(null), "connection ready");
        SetServerState(server, 2);
        ServerRequestPermit? permit = null;
        Exception? failure = null;
        try
        {
            Ensure(server.TryReserveCall(connection, hooks, mayDecode: false, out permit) ==
                ServerCallAdmissionResult.Acquired && permit is not null, "actual permit acquired");
            await body(server, connection, permit!);
        }
        catch (Exception error) { failure = error; }
        try { permit?.Dispose(); }
        catch (Exception error) { failure = JoinControlFailure(failure, error); }
        SetServerState(server, 3);
        try { await connection.CloseAsync(); }
        catch (Exception error) { failure = JoinControlFailure(failure, error); }
        try { await connection.ServiceCleanupTask; }
        catch (Exception error) { failure = JoinControlFailure(failure, error); }
        try { await server.DisposeAsync(); }
        catch (Exception error)
        {
            if (!ReferenceEquals(error, expectedServerDisposeFailure))
                failure = JoinControlFailure(failure, error);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception JoinControlFailure(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private static async Task<Exception?> ObservePhysicalFailureAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return null; }
        catch (Exception failure) when (failure is not TimeoutException) { return failure; }
    }

    private sealed class PhysicalOutcomeStub(Action body) : IRpcStub
    {
        public int Entries { get; private set; }
        public long InterfaceHash => 101;
        public RpcMethodFacts ResolveMethodFacts(long methodHash) => throw new InvalidOperationException("No hot facts callback.");
        private ValueTask Invoke() { Entries++; body(); return ValueTask.CompletedTask; }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => Invoke();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Invoke();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Invoke();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken) => Invoke();
    }
}
