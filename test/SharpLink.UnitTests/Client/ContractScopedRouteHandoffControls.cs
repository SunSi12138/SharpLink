using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

// R13/R14/R15: actual scoped routes across existing physical admission/refresh cuts.
public sealed class ContractScopedRouteHandoffControls
{
    [Test]
    public Task ReservedBeforePlannedRefresh() => RunAsync(async fixture =>
    {
        var admission = new AdmissionProbe();
        await fixture.InitializeAsync(admission);
        var barrier = fixture.HoldNextReservation();
        var original = fixture.StartCall("pre-cut");
        var reserved = await barrier.WaitAsync();
        Ensure(ReferenceEquals(reserved, fixture.Source) && reserved.CallAdmissionReservationCount == 1 &&
               reserved.PendingCalls.ActiveCount == 0, "original source reservation is held before pending publication");
        await fixture.RefreshWhileReplacementUnboundAsync();
        Ensure(reserved.State == ClientConnectionState.Ready && reserved.Session.IsConnected &&
               reserved.CallAdmissionReservationCount == 1 && !fixture.SourceDisposalStarted,
            "planned cut must not prematurely dispose the original reserved physical owner");
        Ensure(!reserved.CanAcceptCalls && !reserved.TryReserveExactCallAdmission() &&
               reserved.CanHonorReservedCallAdmission && fixture.OriginalTarget.IsValidForReservedCall &&
               !fixture.OriginalTarget.IsEligible,
            "old exact target remains valid only for its already-owned reservation; no new source admission");
        var before = fixture.ReservationCount;
        await fixture.ExpectCallFailureAsync(fixture.StartCall("new call while replacement unbound"), IsNoBoundRoute);
        Ensure(fixture.ReservationCount == before && admission.Acquisitions == 1,
            "a later ordinary call cannot borrow the source token or wait for replacement Bind");
        barrier.Release.Set();
        await fixture.CompleteCallAsync(original, 0, reserved, 1300);
        var reports = admission.Reports;
        Ensure(reports.Length == 1 && reports[0].Token == 301 &&
               reports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.Success && reports[0].Outcome.ResponseObserved &&
               fixture.ReservationCount == 1 && ReferenceEquals(fixture.Reservations.Single(), reserved),
            "the sole original grant completes on exactly the pre-cut source, without reacquisition or redirect");
        await fixture.JoinRetiredSourceAsync();
    });

    [Test]
    public Task RefreshBeforeReserveNoBorrow() => RunAsync(async fixture =>
    {
        var admission = new AdmissionProbe();
        await fixture.InitializeAsync(admission);
        await fixture.RefreshWhileReplacementUnboundAsync();
        Ensure(!fixture.Source.CanAcceptCalls && fixture.Replacement.CanAcceptCalls && !fixture.View.HasEligibleTarget(),
            "replacement transport is really Ready while its own contract binding remains unconfirmed");
        await fixture.ExpectCallFailureAsync(fixture.StartCall("post-cut unbound"), IsNoBoundRoute);
        Ensure(fixture.ReservationCount == 0 && admission.Acquisitions == 0 && admission.Reports.Length == 0,
            "old scoped token must not follow the physical redirect, acquire a lease, or reserve replacement before its own Bind");
        // The failed original RPC has already terminated while this successful reply is
        // still held. It did not hide an implicit wait-for-Bind or a logical retry.
        await fixture.ConfirmReplacementAsync();
        await fixture.CompleteCallAsync(fixture.StartCall("own replacement token"), 2, fixture.Replacement, 1400);
        Ensure(fixture.ReservationCount == 1 && ReferenceEquals(fixture.Reservations.Single(), fixture.Replacement) &&
               admission.Acquisitions == 1 && admission.Reports.Length == 1 &&
               admission.Reports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.Success,
            "only a separately confirmed replacement token makes the subsequent independent call usable");
        await fixture.JoinRetiredSourceAsync();
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FatalAndLocalRetirementAfterReserve(bool localRetirement) => RunAsync(async fixture =>
    {
        var admission = new AdmissionProbe();
        await fixture.InitializeAsync(admission);
        var barrier = fixture.HoldNextReservation();
        var invocation = fixture.StartCall("reserved rejection");
        var original = await barrier.WaitAsync();
        Ensure(ReferenceEquals(original, fixture.Source) && original.CallAdmissionReservationCount == 1 &&
               original.PendingCalls.ActiveCount == 0 && admission.Acquisitions == 1,
            "real policy lease and exact physical reservation precede the independent rejection cut");
        if (localRetirement) fixture.Site.LocalControl.Retire();
        else original.ObserveFatalFailureForAdmission();
        Ensure(original.State == ClientConnectionState.Ready && original.Session.IsConnected &&
               original.Binding.Publication.IsEligible(fixture.OriginalTarget.Token),
            "the controlled cut is local registration or physical fatal admission, not transport teardown or token invalidation");
        barrier.Release.Set();
        await fixture.ExpectCallFailureAsync(invocation, error => localRetirement
            ? error is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition,
                DetailCode: SharpLinkErrorDetails.FailedPrecondition.LocalContractUnavailable }
            : IsNoBoundRoute(error));
        Ensure(original.CallAdmissionReservationCount == 0 && original.ActiveCallCount == 0 &&
               fixture.ReservationCount == 1 && ReferenceEquals(fixture.Reservations.Single(), original),
            "post-reservation rejection refunds the original reservation exactly once without publishing a pending call");
        var reports = admission.Reports;
        Ensure(admission.Acquisitions == 1 && reports.Length == 1 && reports[0].Token == 301 &&
               reports[0].Outcome.Kind == SharpLinkEndpointOutcomeKind.ConnectionClosed &&
               reports[0].Outcome.ErrorCode == SharpLinkErrorCode.ConnectionClosed && !reports[0].Outcome.ResponseObserved,
            "the original granted lease receives one failed exact-reservation outcome, never fake success or duplicate completion");
        await fixture.AssertNoRequestAsync(0, original);
    });

    private static bool IsNoBoundRoute(Exception error) => error is SharpLinkException
    {
        Code: SharpLinkErrorCode.Unavailable,
        DetailCode: SharpLinkErrorDetails.Unavailable.NoBoundRoute
    };

    private static async Task RunAsync(Func<ContractScopedRouteHandoffFixture, Task> body)
    {
        var fixture = new ContractScopedRouteHandoffFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private sealed class AdmissionProbe : ISharpLinkEndpointAdmissionPolicy
    {
        private readonly object _gate = new();
        private int _acquisitions;
        private readonly List<(SharpLinkEndpointOutcome Outcome, long Token)> _reports = [];
        internal int Acquisitions { get { lock (_gate) return _acquisitions; } }
        internal (SharpLinkEndpointOutcome Outcome, long Token)[] Reports { get { lock (_gate) return _reports.ToArray(); } }
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            lock (_gate) return new(true, 300 + ++_acquisitions, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            lock (_gate) _reports.Add((outcome, token));
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
