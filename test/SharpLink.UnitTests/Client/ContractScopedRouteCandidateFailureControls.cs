using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// Source-only R23 extension. Requires production 50acc25's original-cleanup out receipt;
// the local test-base checkout does not itself include that production successor.
public sealed partial class ContractScopedRouteFailureControls
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task SiteBuildAndCandidateCleanupFailuresKeepOriginalTaskGraphs(bool observerHandoffFails)
        => WithCandidateFaultFixture(async fixture =>
        {
            var construction = new InvalidOperationException("R23 original site construction occurrence");
            var observer = new InvalidOperationException("R23 actual cleanup observer handoff occurrence");
            var repeated = new IOException("R23 independent physical Release write sibling, repeated by identity");
            var sibling = new OperationCanceledException("R23 nested independent Release write sibling");
            var writeGraph = new AggregateException(repeated, repeated, sibling);
            // SendPump.NormalizeTransportException preserves an existing SharpLinkException.
            // This is an actual Release output failure, NOT a fabricated transport Dispose fault.
            var writeFailure = new SharpLinkException(SharpLinkErrorCode.Internal,
                "R23 exact physical Release output graph", writeGraph);
            await fixture.InitializeCandidateFaultAsync(observerHandoffFails ? observer : null);
            var first = fixture.GlobalTopology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-a").Connections.Single();
            var priorView = fixture.View;
            var priorAttempt = fixture.CaptureAttempt(first);
            var releaseOutput = fixture.NewBarrier();
            var hits = 0;
            fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
            {
                if (!ReferenceEquals(site, fixture.Site) || stage != "state" || Interlocked.Increment(ref hits) != 1) return;
                fixture.HoldAndFailOriginalReleaseOutput(releaseOutput, writeFailure);
                throw construction;
            };
            await fixture.ConfirmAsync(0);
            await releaseOutput.Entered.WaitAsync(FailureBound);
            var cleanup = await fixture.CaptureObservedCandidateCleanupAsync(observerHandoffFails);
            await fixture.ExpectOriginalPrepareFailureAsync(construction);
            await fixture.ExpectPrepareFailureAsync(construction);
            await fixture.ExpectPrepareFailureAsync(construction);
            fixture.AssertHeldOriginalBindingOwner(first, priorAttempt.Attempt, priorView, construction, cleanup);
            fixture.Client.ContractRouteBuildForTesting = null;
            releaseOutput.Release();
            await fixture.CandidatePhysicalDisposalEntered.WaitAsync(FailureBound);
            var physicalCleanup = await fixture.CaptureCandidateDisconnectCleanupAsync();
            var bindingEnvelope = await fixture.ExpectCandidateAndBindGraphsAsync(
                cleanup, construction, observerHandoffFails ? observer : null, writeFailure, writeGraph);
            Ensure(first.Binding.CandidateCount == 0 && first.Binding.CleanupOwnerCount == 0,
                "exact candidate cleanup graph is reported only after candidate references and permits are physically returned");
            await fixture.JoinActualBindingTerminalDrainAsync(first);
            Ensure(!physicalCleanup.Task.IsCompleted,
                "candidate outbound drain is not mistaken for the separately gated whole physical-session cleanup");
            fixture.ReleaseCandidatePhysicalDisposal();
            await fixture.JoinSuccessfulCandidatePhysicalCleanupAsync(physicalCleanup);
            await fixture.ConfirmRemainingAndPrepareAsync();
            await fixture.ExpectCandidateFaultStopAsync(bindingEnvelope,
                observerHandoffFails ? null : writeFailure, writeGraph);
        });

    private static async Task WithCandidateFaultFixture(Func<FailureFixture, Task> body)
    {
        var fixture = new FailureFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        // Release this extension's physical gate before the base's original cleanup path.
        // The observer remains installed and forwards all other owners until Stop is terminal;
        // no racy delegate restoration can hide a late actual owner registration.
        fixture.ReleaseCandidatePhysicalDisposal();
        await fixture.CleanupAsync(primary);
    }

    private sealed partial class FailureFixture
    {
        private CandidateFaultFactory? _candidateFactory;
        private readonly TaskCompletionSource _candidateObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _candidateObserverExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private OwnedOperation? _candidateOriginal;
        private Exception? _candidateObserverFailure;
        private int _candidateObserverClaims;
        internal Task CandidatePhysicalDisposalEntered => (_candidateFactory
            ?? throw new InvalidOperationException("Candidate factory not initialized.")).Transport.Entered.Task;

        internal async Task InitializeCandidateFaultAsync(Exception? observerFailure)
        {
            Ensure(_client is null, "candidate-fault fixture initializes once");
            _candidateObserverFailure = observerFailure;
            var candidateFactory = new CandidateFaultFactory(this, _transports[0]);
            _candidateFactory = candidateFactory;
            SharpLinkEndpoint[] endpoints =
            [
                new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) },
                new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) }
            ];
            _client = ClientBuilderTestHelper.BuildDynamic(new FixedResolver(new(1, endpoints)),
                endpoint => endpoint.Id == "route-a" ? candidateFactory : _transports[1], builder =>
                {
                    Configure(builder);
                    builder.UseTimeProvider(_clock);
                });
            await ObserveAsync(Own("original candidate-fixture Connect", Client.ConnectAsync().AsTask()));
            await ObserveAsync(Own("original candidate-fixture readiness", Client.WaitForReadinessAsync(2).AsTask()));
            Ensure(_transports.All(transport => transport.ConnectCount == 1), "one actual physical session per endpoint");
            var observerField = typeof(ClientBindingBudget).GetField("_observeTask", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual BindingBudget observer slot missing.");
            Ensure(observerField.GetValue(Client.BindingBudget) is null,
                "install the actual budget observer before first demand; do not mutate a latched production delegate");
            Client.BindingBudget.SetTaskObserver(ObserveCandidateFaultTask);
            await InitializeSiteAsync(); // Original Bind/reader/Prepare ownership remains the base fixture's exact flow.
        }

        private void ObserveCandidateFaultTask(Task task, string operation)
        {
            if (operation == "BindingCandidateCleanup" && Interlocked.Increment(ref _candidateObserverClaims) == 1)
            {
                _candidateOriginal = Own("original observed candidate cleanup", task);
                _candidateObserved.TrySetResult();
                try
                {
                    if (_candidateObserverFailure is { } error) throw error;
                    Client.TrackFrameworkTask(task, operation);
                }
                finally { _candidateObserverExited.TrySetResult(); }
                return;
            }
            // Same original task and same production supervision method as
            // ObserveBindingMaintenanceTask, including all terminal/late maintenance owners.
            Own("original forwarded binding maintenance " + operation, task);
            Client.TrackFrameworkTask(task, operation);
        }

        internal void HoldAndFailOriginalReleaseOutput(ColdBarrier output, Exception failure)
            => _transports[0].Connection.RunOnNextOutputBufferRequest(() =>
            {
                output.HoldOnce();
                throw failure;
            });

        internal async Task<OwnedOperation> CaptureObservedCandidateCleanupAsync(bool rejected)
        {
            await _candidateObserved.Task.WaitAsync(Bound);
            await _candidateObserverExited.Task.WaitAsync(Bound);
            var owner = _candidateOriginal ?? throw new InvalidOperationException("Observer lost the actual task before throwing.");
            Ensure(!owner.Task.IsCompleted, "the original created cleanup is still held at actual Release output");
            var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
            lock (Read<Lock>(supervisor, "_gate"))
            {
                var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
                Ensure(active.ContainsKey(owner.Task) != rejected,
                    "normal path registers the exact task; rejected handoff leaves it unregistered, not a proxy registration");
            }
            return owner;
        }

        internal void AssertHeldOriginalBindingOwner(ClientConnection first, object attempt,
            SharpLinkClient.ContractRouteView prior, Exception construction, OwnedOperation cleanup)
        {
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            var after = CaptureAttempt(first);
            lock (Read<Lock>(first.Binding, "_gate"))
            {
                var actualOwners = Read<System.Collections.IEnumerable>(first.Binding, "_cleanupOwners").Cast<object>().ToArray();
                Ensure(actualOwners.Length == 1 && ReferenceEquals(
                    actualOwners[0].GetType().GetProperty("Task", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                        .GetValue(actualOwners[0]), cleanup.Task),
                    "observer argument is the exact pending task of the one actual candidate cleanup owner");
            }
            Ensure(ReferenceEquals(after.Attempt, attempt) && !after.Completed && after.Failure is null &&
                   ReferenceEquals(after.PublicationFailure, construction) && ReferenceEquals(View, prior) &&
                   !cleanup.Task.IsCompleted && OriginalBindStillPending &&
                   SnapshotOperations().Count(owner => owner.Stage == "original BindContractDemand") == 2 &&
                   first.Binding.CandidateCount == 1 && first.Binding.CleanupOwnerCount == 1,
                "observer throw must not lose original out-receipt, finish Bind/attempt, release permits, or permit a fresh cold rebind");
        }

        internal async Task<OwnedOperation> CaptureCandidateDisconnectCleanupAsync()
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                CaptureFrameworkOwners("DynamicClusterDisconnectedConnectionCleanup", "original candidate-disconnect cleanup");
                var owners = SnapshotOperations().Where(owner => owner.Stage == "original candidate-disconnect cleanup").ToArray();
                Ensure(owners.Length <= 1, "one failed physical A must have one required disconnect cleanup");
                if (owners.Length == 1) return owners[0];
                if (Stopwatch.GetElapsedTime(started) >= Bound) throw new TimeoutException("Actual disconnect cleanup was not registered.");
                await Task.Delay(1);
            }
        }

        internal async Task<SharpLinkClient.ContractRouteFailure> ExpectCandidateAndBindGraphsAsync(
            OwnedOperation cleanup, Exception construction, Exception? observer, SharpLinkException writeFailure,
            AggregateException writeGraph)
        {
            var cleanupError = await ExpectFailureAsync(cleanup, error => ReferenceEquals(error, writeFailure));
            AssertExactWriteGraph(cleanupError, writeFailure, writeGraph);
            var bindings = SnapshotOperations().Where(owner => owner.Stage == "original BindContractDemand").ToArray();
            Ensure(bindings.Length == 2, "no duplicate Bind owner was created during pending cleanup");
            var first = await Task.WhenAny(bindings.Select(owner => owner.Task)).WaitAsync(Bound);
            var binding = bindings.Single(owner => ReferenceEquals(owner.Task, first));
            var actual = await ExpectFailureAsync(binding, error =>
            {
                if (error is not SharpLinkClient.ContractRouteFailure envelope) return false;
                Ensure(envelope.Count == (observer is null ? 2 : 3) &&
                       envelope.StageAt(0) == "binding-route-publication" &&
                       envelope.ErrorAt(0) is SharpLinkClient.ContractRouteFailure { Count: 1 } publication &&
                       publication.StageAt(0) == "bind-route-build" && ReferenceEquals(publication.ErrorAt(0), construction),
                    "original Bind keeps exactly one construction occurrence at its original publication stage");
                var cleanupIndex = observer is null ? 1 : 2;
                if (observer is not null)
                    Ensure(envelope.StageAt(1) == "candidate-retirement-start" && ReferenceEquals(envelope.ErrorAt(1), observer),
                        "synchronous observer handoff failure is retained independently, before the actual cleanup graph");
                Ensure(envelope.StageAt(cleanupIndex) == "candidate-retirement" &&
                       envelope.ErrorAt(cleanupIndex) is AggregateException { InnerExceptions.Count: 1 } taskGraph &&
                       ReferenceEquals(taskGraph.InnerExceptions[0], writeFailure),
                    "Bind retains the full original cleanup Task.Exception container, not only its awaited first sibling");
                AssertExactWriteGraph(writeFailure, writeFailure, writeGraph);
                return true;
            });
            return (SharpLinkClient.ContractRouteFailure)actual;
        }

        private static void AssertExactWriteGraph(Exception actual, SharpLinkException expected, AggregateException graph)
        {
            Ensure(ReferenceEquals(actual, expected) && expected.Code == SharpLinkErrorCode.Internal &&
                   ReferenceEquals(expected.InnerException, graph) && graph.InnerExceptions.Count == 3 &&
                   ReferenceEquals(graph.InnerExceptions[0], graph.InnerExceptions[1]) &&
                   graph.InnerExceptions[2] is OperationCanceledException,
                "the original nested output error graph retains all repeated-reference occurrences; it is not classified as Dispose or cancellation");
        }

        internal async Task JoinActualBindingTerminalDrainAsync(ClientConnection connection)
        {
            var terminal = Read<Task>(connection.Binding, "_terminalDrainTask");
            var owned = SnapshotOperations().SingleOrDefault(owner =>
                owner.Stage == "original forwarded binding maintenance BindingTerminalDrain" && ReferenceEquals(owner.Task, terminal))
                ?? Own("original exact binding terminal drain", terminal);
            await ObserveAsync(owned);
            Ensure(terminal.IsCompletedSuccessfully,
                "normal physical outbound close is joined and does not invent another cleanup fault from its close cause");
        }

        internal void ReleaseCandidatePhysicalDisposal() => _candidateFactory?.Transport.Release.TrySetResult();
        internal async Task JoinSuccessfulCandidatePhysicalCleanupAsync(OwnedOperation cleanup)
        {
            await ObserveAsync(cleanup);
            var transport = _candidateFactory!.Transport;
            Ensure(transport.Original is not null && transport.InnerOriginal is not null,
                "whole-session cleanup retains its actual wrapper and inner transport Dispose Tasks");
            await ObserveAsync(transport.Original!);
            await ObserveAsync(transport.InnerOriginal!);
            Ensure(transport.Count == 1 && cleanup.Task.IsCompletedSuccessfully,
                "source output error is not mislabeled as an independent Dispose failure");
        }

        internal async Task ConfirmRemainingAndPrepareAsync()
        {
            await ConfirmAsync(1);
            foreach (var owner in SnapshotOperations().Where(owner => owner.Stage == "original BindContractDemand" && !owner.Reported))
                await ObserveAsync(owner);
            await PrepareAgainAsync();
            Ensure(View.Targets.Length == 1 && View.Targets[0].Connection.EndpointId == "route-b",
                "independent B can become the one eligible route after A's failed candidate physically retires");
        }

        internal async Task ExpectCandidateFaultStopAsync(SharpLinkClient.ContractRouteFailure binding,
            SharpLinkException? separatelySupervisedCleanup, AggregateException graph)
        {
            var stop = Own("original candidate-fault Stop", Client.StopAsync().AsTask());
            var root = await ExpectFailureAsync(stop, error =>
            {
                if (separatelySupervisedCleanup is null) return ReferenceEquals(error, binding);
                Ensure(error is AggregateException { InnerExceptions.Count: 2 },
                    "Stop must retain both separately supervised original cleanup and Bind failures, without unknown siblings");
                var stages = ((AggregateException)error).InnerExceptions;
                Ensure(stages.Count(item => ReferenceEquals(item, binding)) == 1 &&
                       stages.Count(item => ReferenceEquals(item, separatelySupervisedCleanup)) == 1,
                    "same cleanup graph appears once at its own owner and again inside Bind; never deduplicate across original tasks");
                AssertExactWriteGraph(separatelySupervisedCleanup, separatelySupervisedCleanup, graph);
                return true;
            });
            _expectedLifecycleFailure = root;
        }

        private sealed class CandidateFaultFactory(FailureFixture fixture, TestClientTransportFactory peer) : IClientTransportFactory
        {
            internal CandidateFaultTransport Transport { get; } = new(fixture, peer.Connection);
            public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            {
                Ensure(peer.ConnectCount == 0, "manual clock prevents an unsolicited reconnect dial");
                var original = fixture.Own("original wrapped candidate peer handshake", peer.ConnectAsync(cancellationToken).AsTask());
                await original.Task;
                return Transport;
            }
            public ValueTask DisposeAsync()
            {
                var original = fixture.Own("original wrapped candidate factory Dispose", peer.DisposeAsync().AsTask());
                return new(original.Task);
            }
        }

        private sealed class CandidateFaultTransport(FailureFixture fixture, TestTransportConnection peer) : ITransportConnection
        {
            private readonly Lock _gate = new();
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal OwnedOperation? Original;
            internal OwnedOperation? InnerOriginal;
            internal int Count;
            public string Id => peer.Id;
            public PipeReader Input => peer.Input;
            public PipeWriter Output => peer.Output;
            public EndPoint? LocalEndPoint => peer.LocalEndPoint;
            public EndPoint? RemoteEndPoint => peer.RemoteEndPoint;
            public ValueTask DisposeAsync()
            {
                lock (_gate)
                {
                    Original ??= fixture.Own("original candidate physical transport Dispose", DisposeCoreAsync());
                    return new(Original.Task);
                }
            }
            private async Task DisposeCoreAsync()
            {
                Interlocked.Increment(ref Count);
                Entered.TrySetResult();
                await Release.Task;
                InnerOriginal = fixture.Own("original candidate inner peer Dispose", peer.DisposeAsync().AsTask());
                await InnerOriginal.Task;
            }
        }
    }
}
