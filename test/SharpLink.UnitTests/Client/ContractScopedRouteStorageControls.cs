using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRouteFailureControls
{
    [Test]
    public Task WeakCandidateCannotRootSite() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        await fixture.ConfirmAsync(1);
        await fixture.JoinSiteOwnersForReleaseAsync();
        var retained = fixture.AuditAndDropSite();
        var started = Stopwatch.GetTimestamp();
        while (!SiteWasCollected(retained.Site))
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (Stopwatch.GetElapsedTime(started) >= FailureBound)
                throw new TimeoutException("Retained public candidate still roots the released site after all original site producers joined.");
            await Task.Delay(1);
        }
        // Keep this public value, its exact bucket and physical endpoint owner alive through
        // collection. GC is complementary to the direct provider/field audit below.
        Ensure(retained.Candidate.ReadyConnectionCount == 1 && retained.Candidate.ActiveCallCount == 0,
            "the escaping candidate remains useful without retaining the contract site");
        GC.KeepAlive(retained.Candidate);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SiteWasCollected(WeakReference<SharpLinkClient.ContractCallSite> weak)
        => !weak.TryGetTarget(out _);

    [Test]
    public Task StorageAndDuplicateDrainBounds() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var builders = 0;
        var maximumBuilders = 0;
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (!ReferenceEquals(site, fixture.Site)) return;
            if (stage == "buckets")
            {
                var current = Interlocked.Increment(ref builders);
                maximumBuilders = Math.Max(maximumBuilders, current);
                Ensure(current == 1, "one site cannot have overlapping cold builders under its existing S gate");
            }
            if (stage == "signal") Interlocked.Decrement(ref builders);
        };
        for (var version = 2; version <= 65; version++)
        {
            await fixture.FinishReorderAsync(fixture.StartOrderedSnapshot(version, reversed: version % 2 == 0));
            fixture.AssertStorageBounds();
        }
        Ensure(builders == 0 && maximumBuilders == 1, "all measured builders exited, never accumulated");
        fixture.Client.ContractRouteBuildForTesting = null;
        fixture.AssertSecondStillUnconfirmed();
        await fixture.CallFirstAsync(71);
        // Duplicate-drain runtime error-slot transfer is separately exercised by R28;
        // this slice checks the exact bounded representation rather than triggering a
        // second unrelated physical retirement in the repeated-publication scenario.
        Ensure(typeof(ClientConnection).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Count(field => field.FieldType == typeof(SharpLinkClient.ContractRouteFailure)) == 2,
            "a physical owner has exactly the two bounded cut/detach failure slots, not a history collection");
    });

    private sealed partial class FailureFixture
    {
        internal async Task JoinSiteOwnersForReleaseAsync()
        {
            CaptureFrameworkOwners("StartContractBinding", "original binding scheduler");
            foreach (var scheduler in SnapshotOperations().Where(operation => operation.Stage == "original binding scheduler"))
                await ObserveAsync(scheduler);
            CaptureFrameworkOwners("ContractBinding", "original BindContractDemand");
            foreach (var binding in SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand"))
                await ObserveAsync(binding);
            Ensure(SnapshotOperations().All(operation => operation.Task.IsCompletedSuccessfully),
                "no task or scheduler that might retain the site can be forgotten before its real successful exit");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal (SharpLinkEndpointCandidate Candidate, WeakReference<SharpLinkClient.ContractCallSite> Site) AuditAndDropSite()
        {
            var site = Site;
            var view = site.CaptureContractRouteForDiagnostics();
            var index = Array.FindIndex(view.Candidates, candidate => candidate.Endpoint.Id == "route-a");
            Ensure(index >= 0, "A candidate is present");
            var candidate = view.Candidates[index];
            var capacity = Read<Func<int>>(candidate, "_readyConnectionCountProvider");
            var active = Read<Func<int>>(candidate, "_activeCallCountProvider");
            var bucket = view.Buckets[index];
            Ensure(ReferenceEquals(capacity.Target, bucket) && capacity.Method.Name == "EligibleCount",
                "capacity must be the exact bucket method, not a site/local-contract closure");
            Ensure(ReferenceEquals(active, bucket.Endpoint.ActiveCalls) &&
                   ReferenceEquals(active.Target, bucket.Endpoint.Identity),
                "active load uses the existing physical endpoint state provider, not a new route closure");
            var bucketFields = bucket.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Ensure(bucketFields.Length == 2 && bucketFields.Any(field => field.FieldType == typeof(SharpLinkClient.ContractRouteEndpoint)) &&
                   bucketFields.Any(field => field.FieldType == typeof(SharpLinkClient.ContractCallSite.BoundTarget[])),
                "bucket has only endpoint and exact target-array edges; no site/view/metadata backpointer");
            var targetFields = typeof(SharpLinkClient.ContractCallSite.BoundTarget)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Ensure(targetFields.Length == 1 && targetFields[0].FieldType == typeof(ClientConnection),
                "bound target adds only the physical connection edge to the stable session/token base");
            var weak = new WeakReference<SharpLinkClient.ContractCallSite>(site);
            _channel = null;
            _prepare = null;
            lock (_operationsGate)
            {
                Ensure(_operations.All(operation => operation.Task.IsCompletedSuccessfully), "only audited terminal tasks can release their fixture roots");
                _operations.Clear(); // Completed async state-machine boxes can otherwise retain the site.
            }
            return (candidate, weak);
        }

        internal OwnedOperation StartOrderedSnapshot(long version, bool reversed)
            => Own("topology invocation launcher", Task.Factory.StartNew(() =>
            {
                var cluster = Read<object>(Client, "_cluster");
                var apply = cluster.GetType().GetMethod("ApplySnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Actual dynamic ApplySnapshotAsync was not found.");
                SharpLinkEndpoint a = new() { Id = "route-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19001) };
                SharpLinkEndpoint b = new() { Id = "route-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19002) };
                SharpLinkEndpoint[] endpoints = reversed ? [b, a] : [a, b];
                var original = (Task<bool>)(apply.Invoke(cluster, [new SharpLinkEndpointSnapshot(version, endpoints), false])
                    ?? throw new InvalidOperationException("Actual topology application did not return its original Task."));
                return Own("original ApplySnapshotAsync", original);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        internal void AssertStorageBounds()
        {
            var view = View;
            var topology = GlobalTopology;
            Ensure(ReferenceEquals(view.Topology, topology) && topology.Endpoints.Length == 2 &&
                   topology.Endpoints.Sum(endpoint => endpoint.Connections.Length) == 2,
                "the repeated builder retains exactly current E=2,C=2 topology");
            Ensure(view.Targets.Length == 1 && view.Buckets.Length == 2 && view.Candidates.Length == 2 &&
                   view.Buckets.Sum(bucket => bucket.Targets.Length) == 1,
                "one confirmed target is indexed once, candidates are E bounded, unbound physical session adds no target");
            var fields = typeof(SharpLinkClient.ContractCallSite).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Ensure(fields.Count(field => field.FieldType == typeof(SharpLinkClient.ContractRouteView)) == 1 &&
                   !fields.Any(field => field.FieldType == typeof(SharpLinkClient.ContractRouteView[]) ||
                       field.FieldType.IsGenericType && field.FieldType.GetGenericArguments().Contains(typeof(SharpLinkClient.ContractRouteView))),
                "site representation owns one current view and no view history array/generic collection");
            var weak = Read<WeakReference<SharpLinkClient.ContractCallSite>[]>(Client, "_contractRouteDemands");
            Ensure(weak.Length == 1 && weak[0].TryGetTarget(out var demanded) && ReferenceEquals(demanded, Site),
                "one live site retains one weak registry entry through all publications");
            Ensure(ReferenceEquals(Read<SharpLinkClient.ContractCallSite.BoundTarget[]>(Site, "_boundTargets"), view.Targets),
                "the committed exact target authority is shared, not copied into a second history");
        }
    }
}
