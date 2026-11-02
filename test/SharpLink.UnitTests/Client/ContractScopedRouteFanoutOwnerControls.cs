using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRoutePhysicalFailureControls
{
    [Test]
    public Task FirstSiteFanoutFailureStillCommitsSecondAndAttemptsAllRemovalOwners() => Run(async fixture =>
    {
        await fixture.InitializeAsync(dynamicTopology: true);
        await fixture.InitializeSecondRealSiteAsync();
        var firstPrior = fixture.Site.CaptureContractRouteForDiagnostics();
        var secondPrior = fixture.SecondSite.CaptureContractRouteForDiagnostics();
        var topologyPrior = fixture.Topology;
        var oldPulse = fixture.CaptureDynamicTopologySignal();
        var marker = new InvalidOperationException("R24 first real site fanout");
        List<SharpLinkClient.ContractCallSite> visited = [];
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (stage != "buckets") return;
            visited.Add(site);
            if (ReferenceEquals(site, fixture.Site)) throw marker;
        };
        fixture.HoldRemovedFactoryDisposal();
        var application = await fixture.GetLaunchedOwnerAsync(fixture.StartActualRemoval());
        await fixture.AcceptRemovalFailureAsync(application, marker);
        await fixture.RemovedPeer.DisposalEntered.Task.WaitAsync(Bound);
        await fixture.RemovedFactoryEntered.WaitAsync(Bound);
        var physical = await fixture.CaptureNamedCleanupAsync(
            "DynamicClusterTopologyRetirementCleanup", "original removed physical cleanup");
        var factoryRelease = await fixture.CaptureNamedCleanupAsync(
            "DynamicClusterRetiredTopologyRelease", "original removed factory release");
        var current = fixture.Topology;
        Ensure(current.Revision > topologyPrior.Revision && current.Endpoints.Length == 1 &&
               current.Endpoints[0].Endpoint.Id == "failure-a",
            "actual endpoint removal commits global topology once; first-site failure cannot roll it back");
        Ensure(ReferenceEquals(fixture.Site.CaptureContractRouteForDiagnostics(), firstPrior) &&
               ReferenceEquals(fixture.SecondSite.CaptureContractRouteForDiagnostics().Topology, current) &&
               !ReferenceEquals(fixture.SecondSite.CaptureContractRouteForDiagnostics(), secondPrior),
            "first site preserves its old safe view while the second real registered site commits the new topology");
        Ensure(visited.Count == 2 && ReferenceEquals(visited[0], fixture.Site) &&
               ReferenceEquals(visited[1], fixture.SecondSite),
            "the real weak-demand registry fanout continues in order after the first construction throw");
        Ensure(oldPulse.IsCompletedSuccessfully && !ReferenceEquals(oldPulse, fixture.CaptureDynamicTopologySignal()),
            "original topology-change signal is pulsed despite original Apply task fault");
        Ensure(!physical.Source.IsCompleted && !factoryRelease.Source.IsCompleted &&
               !firstPrior.Targets.Single(target => target.Connection.EndpointId == "failure-b").IsEligible,
            "real physical cleanup and real factory-release tasks were both registered while separately gated; old B target is cut");
        fixture.ClearHooks();
        fixture.RemovedPeer.ReleaseDisposal();
        await fixture.ObserveSuccessfulOwnerAsync(physical);
        Ensure(!factoryRelease.Source.IsCompleted,
            "physical disposal completion cannot substitute for the independently held factory release owner");
        fixture.ReleaseRemovedFactory();
        await fixture.ObserveSuccessfulOwnerAsync(factoryRelease);
        await fixture.JoinRemovedFactoryOriginalAsync();
        Ensure(fixture.RemovedPeer.DisposalCount == 1 && fixture.RemovedPeer.InnerDispose is { IsCompletedSuccessfully: true } &&
               fixture.RemovedFactoryDisposals == 1,
            "both independent removal owners physically finish exactly once after their own gates open");
    }, holdSecond: true);

    private sealed partial class PhysicalFixture
    {
        private SharpLinkClient.ContractCallSite? _secondSite;
        private TestSentFrame? _secondBindFrame;
        private bool _secondReplyStarted;
        internal SharpLinkClient.ContractCallSite SecondSite
            => _secondSite ?? throw new InvalidOperationException("Second real site not registered.");
        internal GatedTransport RemovedPeer => _factories[1].Transport;
        internal Task RemovedFactoryEntered => _factories[1].FactoryDisposalEntered.Task;
        internal int RemovedFactoryDisposals => Volatile.Read(ref _factories[1].FactoryDisposalCount);

        internal async Task InitializeSecondRealSiteAsync()
        {
            var authority = _factories[0].Peer.Connection.Authority;
            var receipt = new RpcRegistrationReceipt(authority.ServerInstanceId, authority.RegistryEpoch, 1, 1,
                ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash);
            _secondSite = (SharpLinkClient.ContractCallSite)Client.CreateContractChannel(
                Client.GetLocalContract<ManualClientContractFixture.IContract>(), new RpcProxyOptions
                {
                    Requirement = RpcBindingRequirement.MinimumVersion,
                    Receipt = receipt
                });
            Ensure(!ReferenceEquals(Site, SecondSite), "valid receipt requirement creates a real distinct cache-keyed site");
            var prepare = Own("original second-site Prepare", SecondSite.PrepareAsync(Bound).AsTask());
            var reader = Own("original second-site Bind reader",
                _factories[0].Peer.Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
            await ObserveAsync(reader);
            _secondBindFrame = ((Task<TestSentFrame>)reader.Source).Result;
            var request = Protocol3BindingControlCodec.ReadBindContract(new(_secondBindFrame!.Value.Payload));
            Ensure(request.ContractId == receipt.ContractId && request.Hash == receipt.RpcAssemblyHash &&
                   request.Requirement == Protocol3BindingRequirement.MinimumVersion &&
                   request.Receipt == new Protocol3BindingReceipt(authority.ServerInstanceId, authority.RegistryEpoch, 1, 1),
                "second site must issue a real distinct minimum-version Bind against A's valid authority receipt");
            await UntilAsync(() => CaptureFramework("ContractBinding", "original Bind") == 3,
                "the second site's actual pending Bind task must join the two original first-site owners");
            await ConfirmSecondSiteAsync();
            await ObserveAsync(prepare);
            foreach (var bind in Snapshot().Where(owner => owner.Stage == "original Bind")) await ObserveAsync(bind);
            var first = Site.CaptureContractRouteForDiagnostics();
            var second = SecondSite.CaptureContractRouteForDiagnostics();
            Ensure(first.Targets.Length == 2 && second.Targets.Length == 1 &&
                   ReferenceEquals(second.Targets[0].Connection, Connection) &&
                   second.Targets[0].Token == Protocol3BindingToken.Create(1, 1, authority.Limits.Slots) &&
                   first.Targets.Single(target => ReferenceEquals(target.Connection, Connection)).Token ==
                       Protocol3BindingToken.Create(0, 1, authority.Limits.Slots) && Connection.Binding.ActiveOperationCount == 0 &&
                   first.Targets.Single(target => target.Connection.EndpointId == "failure-b").Connection.Binding.ActiveOperationCount == 0,
                "two real sites have separate exact tokens; removed B is fully bound and has no pending control owner");
            var registry = Read<WeakReference<SharpLinkClient.ContractCallSite>[]>(Client, "_contractRouteDemands");
            Ensure(registry.Length == 2 && registry[0].TryGetTarget(out var registeredFirst) &&
                   ReferenceEquals(registeredFirst, Site) && registry[1].TryGetTarget(out var registeredSecond) &&
                   ReferenceEquals(registeredSecond, SecondSite),
                "fanout order comes from actual weak registration, not a synthetic site array");
        }

        private async Task ConfirmSecondSiteAsync()
        {
            Ensure(_secondBindFrame.HasValue && !_secondReplyStarted,
                "second original BindResult injection cannot be retried after unknown outcome");
            var peer = _factories[0].Peer.Connection;
            var authority = peer.Authority;
            using var payload = new PooledByteBufferWriter();
            Protocol3BindingControlCodec.WriteBindResult(payload, new Protocol3BindResult(
                Protocol3BindStatus.Success, Protocol3BindingToken.Create(1, 1, authority.Limits.Slots),
                ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash,
                1, 1, authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
            var bytes = payload.WrittenMemory.ToArray();
            _secondReplyStarted = true;
            await ObserveAsync(Own("original second-site BindResult injection", peer.InjectFrameAsync(
                ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, _secondBindFrame!.Value.Header.RequestId, bytes)));
        }
        private async Task ConfirmAdditionalSiteForCleanupAsync()
        {
            if (_secondBindFrame.HasValue && !_secondReplyStarted) await ConfirmSecondSiteAsync();
        }

        internal Task CaptureDynamicTopologySignal()
        {
            var cluster = Read<object>(Client, "_cluster");
            lock (Read<Lock>(cluster, "_gate")) return Read<TaskCompletionSource>(cluster, "_topologyChanged").Task;
        }
        internal void HoldRemovedFactoryDisposal() => _factories[1].HoldFactoryDisposal = true;
        internal void ReleaseRemovedFactory() => _factories[1].ReleaseFactoryDisposal();
        internal async Task JoinRemovedFactoryOriginalAsync()
            => await ObserveAsync(_factories[1].FactoryDisposeOwner ?? throw new InvalidOperationException("Missing original factory Dispose task."));

        internal Owner StartActualRemoval() => Own("ApplySnapshot invocation launcher", Task.Factory.StartNew(() =>
        {
            var cluster = Read<object>(Client, "_cluster");
            var method = cluster.GetType().GetMethod("ApplySnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual dynamic ApplySnapshotAsync entry missing.");
            SharpLinkEndpoint[] next =
                [new() { Id = "failure-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19101) }];
            var original = (Task<bool>)(method.Invoke(cluster, [new SharpLinkEndpointSnapshot(2, next), false])
                ?? throw new InvalidOperationException("Actual ApplySnapshotAsync did not return its original Task."));
            return Own("original reflected ApplySnapshotAsync", original);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal async Task AcceptRemovalFailureAsync(Owner owner, Exception marker)
        {
            owner.Expected = error =>
            {
                Ensure(error is AggregateException { InnerExceptions.Count: 1 } aggregate &&
                       aggregate.InnerExceptions[0] is SharpLinkClient.ContractRouteFailure,
                    "original Apply Task retains the exact infrastructure envelope after all housekeeping attempts");
                AssertOccurrences(error, marker);
                var batch = (SharpLinkClient.ContractRouteFailure)((AggregateException)error).InnerExceptions[0];
                Ensure(batch.Count == 1 && batch.StageAt(0) == "resolver-route-publication" &&
                       batch.ErrorAt(0) is SharpLinkClient.ContractRouteFailure { TopologyCommitted: true, Count: 1 } fanout &&
                       fanout.StageAt(0) == "site-reconcile" && ReferenceEquals(fanout.ErrorAt(0), marker),
                    "the first-site error stays attached to this actual committed resolver publication occurrence");
            };
            await ObserveAsync(owner);
        }
        internal async Task<Owner> CaptureNamedCleanupAsync(string operation, string stage)
        {
            await UntilAsync(() => CaptureFramework(operation, stage) == 1,
                "actual registered original cleanup owner missing: " + operation);
            return Snapshot().Single(owner => owner.Stage == stage);
        }
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
}
