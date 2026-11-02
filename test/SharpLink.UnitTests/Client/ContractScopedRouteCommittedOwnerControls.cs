using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRoutePhysicalFailureControls
{
    [Test]
    public Task ConnectPostcommitFanoutFaultKeepsTheCommittedPoolOwner() => Run(async fixture =>
    {
        fixture.InitializeUnconnectedSite();
        var marker = new InvalidOperationException("R29 original ConnectOne committed fanout");
        var fanouts = 0;
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (ReferenceEquals(site, fixture.Site) && stage == "buckets" && Interlocked.Increment(ref fanouts) == 1)
                throw marker;
        };
        // This is the actual ConnectOneAsync Task, not public ConnectInitial's exception-returning
        // coordinator, a Task.Run proxy, or an invented test reporting task.
        var originalConnect = await fixture.GetLaunchedOwnerAsync(fixture.StartActualConnectOne());
        await fixture.AcceptDirectCommittedFailureAsync(originalConnect, marker);
        fixture.CaptureFirstPhysicalOwner();
        Ensure(fixture.ContainsPhysicalOwner() && fixture.Connection.State == ClientConnectionState.Ready &&
               fixture.Connection.Session.IsConnected && fixture.Connection.CanAcceptCalls &&
               !fixture.Peer.DisposalEntered.Task.IsCompleted && fixture.Peer.DisposalCount == 0,
            "a committed Ready physical session remains pool-owned; fanout error cannot enter failed-dial disposal");
        fixture.AssertCommittedWorkersAndReadiness();
        fixture.ClearHooks();
        await fixture.PrepareFirstDirectConnectionAsync();
        Ensure(fixture.Site.CaptureContractRouteForDiagnostics().Targets.Single().Connection == fixture.Connection,
            "a later real Prepare/Bind uses the same still-owned session, not a replacement dial");
    });

    [Test]
    public Task RefreshPostcommitFaultKeepsReplacementAndReleasesOnlyItsCommitReservation() => Run(async fixture =>
    {
        await fixture.InitializeAsync();
        var fanout = new InvalidOperationException("R30 committed route fanout");
        var independent = new IOException("R30 independent after-swap owner stage");
        fixture.Reserve(); // Source's already-admitted physical owner stays charged through replacement.
        fixture.ConfigureReplacementFault(fanout, independent);
        var originalRefresh = await fixture.GetLaunchedOwnerAsync(fixture.StartActualReplacement());
        await fixture.AcceptDirectCommittedFailureAsync(originalRefresh, fanout, independent);
        fixture.AssertReplacementOwnershipAndSentinel();
        fixture.ClearHooks();
        fixture.ReleaseReplacementSentinel();
        fixture.AssertReplacementAfterSentinelRelease();
        fixture.ReleaseReservation();
        await fixture.Peer.DisposalEntered.Task.WaitAsync(Bound);
        var oldCleanup = await fixture.CaptureCleanupAsync("StaticClusterSessionRefreshRetiredConnectionCleanup");
        Ensure(!oldCleanup.Source.IsCompleted && !fixture.ContainsPhysicalOwner(),
            "source is only detached into its original physical cleanup after its admitted owner releases");
        fixture.Peer.ReleaseDisposal();
        await fixture.ObserveSuccessfulOwnerAsync(oldCleanup);
        Ensure(fixture.Peer.DisposalCount == 1 && fixture.Peer.InnerDispose is { IsCompletedSuccessfully: true },
            "original source cleanup physically exits once; committed replacement remains separately owned");
        fixture.AssertReplacementAfterSentinelRelease();
    });

    private sealed partial class PhysicalFixture
    {
        private ClientConnection? _replacement;
        private bool _replacementSentinel;

        internal void ConfigureReplacementFault(Exception fanout, Exception independent)
        {
            _factories[0].EnableOneReplacement();
            var fanouts = 0;
            Client.ContractRouteBuildForTesting = (site, stage) =>
            {
                if (ReferenceEquals(site, Site) && stage == "buckets" && Interlocked.Increment(ref fanouts) == 1)
                    throw fanout;
            };
            Client._afterSessionRefreshEligibilitySwapTestHook = () =>
            {
                _replacement = Topology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "failure-a").Connections.Single();
                Ensure(!ReferenceEquals(_replacement, Connection) && _replacement.CallAdmissionReservationCount == 1 &&
                       Connection.CallAdmissionReservationCount == 1 && Connection.HasPlannedSessionRefreshRetirement,
                    "after actual committed fanout, replacement owns exactly its commit reservation and source remains admitted");
                Ensure(_replacement.TryReserveExactCallAdmission(), "one independently owned sentinel reservation acquired");
                _replacementSentinel = true;
                Ensure(_replacement.CallAdmissionReservationCount == 2,
                    "sentinel makes an erroneous second commit release observable, rather than hidden by an idempotent zero floor");
                throw independent;
            };
        }

        internal Owner StartActualReplacement() => Own("ReplaceSession invocation launcher", Task.Factory.StartNew(() =>
        {
            var cluster = Read<object>(Client, "_cluster");
            var endpoint = Read<StaticClientRuntimeEndpointState[]>(cluster, "_endpoints").Single(item =>
                item.Configuration.Endpoint.Id == "failure-a");
            var method = cluster.GetType().GetMethod("ReplaceSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual ReplaceSessionAsync entry missing.");
            var request = new ProtocolV2SessionRefreshRequested(_factories[0].Peer.Connection.Authority.ServerInstanceId, 2);
            var original = (Task<bool>)(method.Invoke(cluster, [Connection, endpoint, request, CancellationToken.None])
                ?? throw new InvalidOperationException("Actual ReplaceSessionAsync did not return its original Task."));
            return Own("original reflected ReplaceSessionAsync", original);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal void AssertReplacementOwnershipAndSentinel()
        {
            Ensure(_replacement is not null && _replacementSentinel && _replacement.CallAdmissionReservationCount == 1,
                "catch releases exactly the original commit reservation, not zero times or twice over the sentinel");
            Ensure(ContainsPhysicalOwner() && Connection.CallAdmissionReservationCount == 1 &&
                   Connection.HasPlannedSessionRefreshRetirement && !Peer.DisposalEntered.Task.IsCompleted,
                "old admitted physical owner remains charged, not disposed by the replacement owner's error");
            AssertReplacementPoolOwner();
        }
        private void AssertReplacementPoolOwner()
        {
            var replacement = _replacement ?? throw new InvalidOperationException("No committed replacement observed.");
            var cluster = Read<object>(Client, "_cluster");
            lock (Read<Lock>(cluster, "_gate"))
                Ensure(Read<StaticClientRuntimeEndpointState[]>(cluster, "_endpoints").Any(endpoint =>
                    endpoint.Connections.Contains(replacement)), "published replacement is still in the real physical pool");
            Ensure(replacement.State == ClientConnectionState.Ready && replacement.CanAcceptCalls &&
                   replacement.Session.IsConnected && _factories[0].ReplacementTransport is { DisposalCount: 0 } peer &&
                   !peer.DisposalEntered.Task.IsCompleted && _factories[0].ConnectCount == 2,
                "postcommit catch cannot reinterpret live replacement as an uncommitted failed dial or start another replacement");
        }
        internal void ReleaseReplacementSentinel()
        {
            if (!_replacementSentinel) return;
            _replacementSentinel = false;
            (_replacement ?? throw new InvalidOperationException("Lost sentinel owner.")).ReleaseCallAdmissionReservation();
        }
        internal void AssertReplacementAfterSentinelRelease()
        {
            Ensure(!_replacementSentinel && _replacement is { CallAdmissionReservationCount: 0 },
                "fixture releases only its one independently acquired sentinel, after original commit release is proven");
            AssertReplacementPoolOwner();
        }
        internal async Task ObserveSuccessfulOwnerAsync(Owner owner) => await ObserveAsync(owner);

        internal void InitializeUnconnectedSite()
        {
            Ensure(_client is null, "direct-connect fixture initializes once");
            StaticEndpointConfiguration[] endpoints =
            [
                new(new() { Id = "failure-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19101) }, _factories[0]),
                new(new() { Id = "failure-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19102) }, _factories[1])
            ];
            _client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
            {
                builder.UseTimeProvider(_clock);
                builder.UseCluster(options =>
                {
                    options.MinReadyEndpoints = 1;
                    options.MaxEndpoints = 2;
                    options.MaxConnections = 2;
                    options.MaxConnectionsPerEndpoint = 1;
                });
            });
            _site = (SharpLinkClient.ContractCallSite)ClientInvokerTestHelper.CreateChannel(Client);
            Ensure(Topology.Endpoints.Length == 0 && _factories.All(factory => factory.ConnectCount == 0),
                "the real registered demand precedes any physical connection publication");
        }

        internal Owner StartActualConnectOne() => Own("ConnectOne invocation launcher", Task.Factory.StartNew(() =>
        {
            var cluster = Read<object>(Client, "_cluster");
            var endpoint = Read<StaticClientRuntimeEndpointState[]>(cluster, "_endpoints")[0];
            var method = cluster.GetType().GetMethod("ConnectOneAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual ConnectOneAsync entry missing.");
            var task = (Task)(method.Invoke(cluster, [endpoint, CancellationToken.None])
                ?? throw new InvalidOperationException("Actual ConnectOneAsync did not return its original Task."));
            return Own("original reflected ConnectOneAsync", task);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal async Task<Owner> GetLaunchedOwnerAsync(Owner launcher)
        {
            await ObserveAsync(launcher);
            return ((Task<Owner>)launcher.Source).Result;
        }

        internal void CaptureFirstPhysicalOwner()
            => _connection = Topology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "failure-a").Connections.Single();

        internal async Task AcceptDirectCommittedFailureAsync(Owner owner, params Exception[] expected)
        {
            owner.Expected = error =>
            {
                Ensure(error is AggregateException { InnerExceptions.Count: 1 } aggregate &&
                       aggregate.InnerExceptions[0] is SharpLinkClient.ContractRouteFailure { TopologyCommitted: true },
                    "the original private physical-owner Task must retain its committed failure envelope");
                AssertOccurrences(error, expected);
                var batch = (SharpLinkClient.ContractRouteFailure)((AggregateException)error).InnerExceptions[0];
                Ensure(batch.Count == expected.Length && batch.StageAt(0) == "site-reconcile" &&
                       (expected.Length != 2 || batch.StageAt(1) == "refresh-body"),
                    "committed fanout and independent after-swap fault retain their exact owner stages in order");
            };
            await ObserveAsync(owner);
            Ensure(owner.Source.IsFaulted, "the actual Connect/Replace owner must fault, not turn failure into success/cancellation");
        }

        internal void AssertCommittedWorkersAndReadiness()
        {
            var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
            lock (Read<Lock>(supervisor, "_gate"))
            {
                var active = Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active");
                Ensure(active.Count(pair => pair.Value.Operation == "StaticClusterHeartbeatSendLoop") == 1 &&
                       active.Count(pair => pair.Value.Operation == "StaticClusterProcessRequestLoop") == 1,
                    "the committed session's actual heartbeat and receive workers were both registered before owner fault");
            }
            var diagnostics = Client.GetDiagnosticSnapshot();
            Ensure(Client.State == SharpLinkConnectionState.Ready && diagnostics.Resources.ReadyConnections == 1 &&
                   _factories[0].ConnectCount == 1 && _factories[1].ConnectCount == 0,
                "postcommit error preserves actual readiness publication and one dial, not a replacement/failed-dial cleanup");
        }

        internal async Task PrepareFirstDirectConnectionAsync()
        {
            var prepare = Own("original direct-owner Prepare", Site.PrepareAsync(Bound).AsTask());
            var reader = Own("original direct-owner Bind reader",
                _factories[0].Peer.Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
            await ObserveAsync(reader);
            _binds[0] = ((Task<TestSentFrame>)reader.Source).Result;
            var request = Protocol3BindingControlCodec.ReadBindContract(new(_binds[0]!.Value.Payload));
            Ensure(request.ContractId == ManualClientContractFixture.ContractId &&
                   request.Hash == ManualClientContractFixture.AssemblyHash &&
                   request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
                "recovery uses the same genuine local contract and original session authority");
            await UntilAsync(() => CaptureFramework("ContractBinding", "original Bind") == 1,
                "retain original pending Bind before manually confirming the same committed session");
            await ConfirmAsync(0);
            await ObserveAsync(prepare);
            foreach (var bind in Snapshot().Where(owner => owner.Stage == "original Bind")) await ObserveAsync(bind);
        }
    }
}
