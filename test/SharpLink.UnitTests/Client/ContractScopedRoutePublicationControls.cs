using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRoutePublicationControls
{
    private static readonly TimeSpan RaceBound = TimeSpan.FromSeconds(10);

    [Test]
    public Task TopologyLateWriterVersusNewBind() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var before = fixture.View;
        var first = before.Targets.Single();
        var second = fixture.GlobalTopology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-b")
            .Connections.Single();
        var topologyWriter = fixture.NewBarrier();
        var publications = 0;
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage == "topology-published") Interlocked.Increment(ref publications);
        };
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (ReferenceEquals(site, fixture.Site) && stage == "before-reconcile" &&
                Environment.CurrentManagedThreadId == fixture.ApplicationThread)
                topologyWriter.HoldOnce();
        };
        var application = fixture.StartReorder();
        await topologyWriter.Entered.WaitAsync(RaceBound);
        var latest = fixture.GlobalTopology;
        Ensure(latest.Revision > before.Topology.Revision && !ReferenceEquals(latest.IndexMap, before.Topology.IndexMap),
            "the actual sole reorder publishes new T/index-map before its delayed site writer takes S");

        await fixture.ConfirmAsync(1);
        await fixture.AwaitViewAsync(view => view.BindingRevision == before.BindingRevision + 1 &&
            view.Targets.Any(target => ReferenceEquals(target.Connection, second)));
        var newerBind = fixture.View;
        Ensure(ReferenceEquals(newerBind.Topology, latest) && newerBind.Targets.Contains(first),
            "new Bind must merge against current global T while preserving the existing exact A target");
        topologyWriter.Release();
        await fixture.FinishReorderAsync(application);
        var final = fixture.View;
        Ensure(final.BindingRevision == newerBind.BindingRevision && final.Targets.Length == 2 &&
               final.Targets.Any(target => ReferenceEquals(target.Connection, second)) &&
               ReferenceEquals(final.Topology, latest) && final.HasEligibleTarget(),
            "the resumed topology writer must not overwrite the newer B axis or restore captured targets");
        Ensure(Volatile.Read(ref publications) == 1, "convergence must not rely on a second topology event");
    });

    [Test]
    public Task BindLateWriterVersusNewTopology() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var before = fixture.View;
        var first = before.Targets.Single();
        var second = fixture.GlobalTopology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "route-b")
            .Connections.Single();
        var bindWriter = fixture.NewBarrier();
        var publications = 0;
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage == "topology-published") Interlocked.Increment(ref publications);
        };
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (ReferenceEquals(site, fixture.Site) && stage == "before-bind-lock") bindWriter.HoldOnce();
        };
        await fixture.ConfirmAsync(1);
        await bindWriter.Entered.WaitAsync(RaceBound);
        Ensure(fixture.View.BindingRevision == before.BindingRevision,
            "the delayed Bind has not committed its new target before acquiring S");
        var application = fixture.StartReorder();
        await fixture.FinishReorderAsync(application);
        var latest = fixture.GlobalTopology;
        Ensure(latest.Revision > before.Topology.Revision && ReferenceEquals(fixture.View.Topology, latest) &&
               fixture.View.BindingRevision == before.BindingRevision,
            "topology advances T while preserving the current B axis before the delayed Bind resumes");
        bindWriter.Release();
        await fixture.AwaitViewAsync(view => view.BindingRevision == before.BindingRevision + 1 &&
            view.Targets.Any(target => ReferenceEquals(target.Connection, second)));
        Ensure(ReferenceEquals(fixture.View.Topology, latest) && fixture.View.Targets.Length == 2 &&
               fixture.View.Targets.Contains(first) &&
               fixture.View.Buckets[0].Endpoint.Endpoint.Id == "route-b" && fixture.View.HasEligibleTarget(),
            "late Bind must read latest T under S, not rebuild its new B against old endpoint indices");
        Ensure(Volatile.Read(ref publications) == 1, "new Bind cannot require another topology event to repair T");
    });

    [Test]
    public Task RegisterBeforeUniqueTopologyCapture() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync(createSite: false);
        var previous = fixture.GlobalTopology;
        var registration = fixture.NewBarrier();
        SharpLinkClient.ContractCallSite? registering = null;
        var topologyVisits = 0;
        var publications = 0;
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage == "topology-published") Interlocked.Increment(ref publications);
        };
        fixture.Client.ContractRouteBuildForTesting = (site, stage) =>
        {
            if (stage == "registered")
            {
                Volatile.Write(ref registering, site);
                registration.HoldOnce();
            }
            if (stage == "before-reconcile" && ReferenceEquals(site, Volatile.Read(ref registering)) &&
                Environment.CurrentManagedThreadId == fixture.ApplicationThread)
                Interlocked.Increment(ref topologyVisits);
        };
        var creation = fixture.StartSiteCreation();
        await registration.Entered.WaitAsync(RaceBound);
        var application = fixture.StartReorder();
        await fixture.FinishReorderAsync(application);
        var siteBeforeRegistrationReconcile = Volatile.Read(ref registering)
            ?? throw new InvalidOperationException("The original weak registration was not observed.");
        Ensure(!creation.Task.IsCompleted && Volatile.Read(ref topologyVisits) == 1 &&
               fixture.GlobalTopology.Revision > previous.Revision &&
               ReferenceEquals(siteBeforeRegistrationReconcile.CaptureContractRouteForDiagnostics().Topology,
                   fixture.GlobalTopology),
            "the sole topology writer must capture the already-published weak site and reconcile it while registration remains held");
        registration.Release();
        await fixture.FinishSiteCreationAsync(creation);
        await fixture.InitializeSiteAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        await fixture.CallFirstAsync(41);
        Ensure(Volatile.Read(ref publications) == 1 && ReferenceEquals(fixture.View.Topology, fixture.GlobalTopology),
            "registration then real Bind/Prepare/RPC converge without a second topology publication");
    });

    [Test]
    public Task RegisterAfterUniqueTopologyCapture() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync(createSite: false);
        var previous = fixture.GlobalTopology;
        var publishedBeforeCapture = fixture.NewBarrier();
        var publications = 0;
        fixture.Client.ContractRoutePublicationForTesting = stage =>
        {
            if (stage != "topology-published") return;
            Interlocked.Increment(ref publications);
            publishedBeforeCapture.HoldOnce();
        };
        var application = fixture.StartReorder();
        await publishedBeforeCapture.Entered.WaitAsync(RaceBound);
        var latest = fixture.GlobalTopology;
        Ensure(latest.Revision > previous.Revision, "global topology is already authoritative before weak capture");
        publishedBeforeCapture.Release();
        await fixture.FinishReorderAsync(application);
        // No site existed during the sole writer's weak capture. Only registration's own
        // current-authority reconcile can install this T; no later topology event is allowed.
        var registrationReconciles = 0;
        fixture.Client.ContractRouteBuildForTesting = (_, stage) =>
        {
            if (stage == "before-reconcile" && Environment.CurrentManagedThreadId == fixture.SiteCreationThread)
                Interlocked.Increment(ref registrationReconciles);
        };
        var creation = fixture.StartSiteCreation();
        await fixture.FinishSiteCreationAsync(creation);
        Ensure(Volatile.Read(ref registrationReconciles) == 1 && ReferenceEquals(fixture.View.Topology, latest),
            "registration after the unique capture must synchronously reconcile the already-published current T");
        await fixture.InitializeSiteAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        await fixture.CallFirstAsync(43);
        Ensure(Volatile.Read(ref publications) == 1 && ReferenceEquals(fixture.View.Topology, latest),
            "the late registration is usable without waiting for another topology event");
    });

    private static async Task WithFixture(Func<PublicationFixture, Task> body)
    {
        var fixture = new PublicationFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }
}
