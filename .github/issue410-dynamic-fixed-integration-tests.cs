using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowIntegrationPrototypeTests
{
    [Test]
    public async Task ImmediateUpdateReusesOneGlobalRateStateAndPreservesConsumption()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(
            options, 3, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate));

        var source = Current(server);
        var sourceRate = source.Controller.GlobalRateStateForTests!;
        var dynamic = sourceRate.DynamicFixedWindowForTests ??
            throw new Exception("assert failed: expected specialized Dynamic FixedWindow state");

        var first = await source.Controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired, "source window must admit two calls");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
        Ensure(dynamic.Consumed == 2, "two admitted calls must charge the one stable ledger");

        publicServer.UpdateAdmissionControl(options => Configure(
            options, 1, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate));

        var replacement = Current(server);
        Ensure(ReferenceEquals(sourceRate, replacement.Controller.GlobalRateStateForTests),
            "Fixed->Fixed Immediate update must reuse the exact lifecycle/rate wrapper");
        Ensure(ReferenceEquals(dynamic, replacement.Controller.GlobalRateStateForTests!.DynamicFixedWindowForTests),
            "Fixed->Fixed Immediate update must reuse the exact specialized ledger");
        Ensure(replacement.Kernel.RateStateCount == 1,
            "Immediate update must not create a hidden successor FixedWindow state");
        Ensure(sourceRate.TransitionDebtForDiagnostics == 0,
            "stable FixedWindow update must not create transition debt");
        Ensure(dynamic.Consumed == 2 && dynamic.CurrentPermitLimit == 1,
            "Immediate shrink must preserve consumption while changing the target");

        var rejected = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == "rate",
            "new request must observe the immediately shrunk stable target");

        publicServer.UpdateAdmissionControl(options => Configure(
            options, 4, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate));
        var expanded = Current(server);
        Ensure(ReferenceEquals(sourceRate, expanded.Controller.GlobalRateStateForTests),
            "Immediate increase must still keep the same ledger");
        var third = await expanded.Controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        var fourth = await expanded.Controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(third.IsAcquired && fourth.IsAcquired,
            "increase from 1 to 4 with consumed=2 must expose exactly two more permits");
        third.Lease!.Dispose();
        fourth.Lease!.Dispose();
        var exhausted = await expanded.Controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired, "stable ledger must stop at consumed=4");
    }

    [Test]
    public async Task NextBoundaryUpdateKeepsOneLedgerAndRecordsLatestPendingTarget()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(
            options, 2, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.NextWindowBoundary));

        var sourceRate = Current(server).Controller.GlobalRateStateForTests!;
        var dynamic = sourceRate.DynamicFixedWindowForTests!;
        var acquired = await Current(server).Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(acquired.IsAcquired, "source request must be admitted");
        acquired.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => Configure(
            options, 5, TimeSpan.FromMinutes(30), DynamicFixedWindowActivationMode.NextWindowBoundary));
        publicServer.UpdateAdmissionControl(options => Configure(
            options, 7, TimeSpan.FromMinutes(20), DynamicFixedWindowActivationMode.NextWindowBoundary));

        var replacement = Current(server);
        Ensure(ReferenceEquals(sourceRate, replacement.Controller.GlobalRateStateForTests),
            "NextBoundary updates must reuse one stable lifecycle wrapper");
        Ensure(replacement.Kernel.RateStateCount == 1,
            "NextBoundary updates must not accumulate rate generations");
        Ensure(dynamic.CurrentPermitLimit == 2 && dynamic.CurrentWindow == TimeSpan.FromHours(1),
            "current old window must remain on its old definition");
        Ensure(dynamic.PendingPermitLimit == 7 && dynamic.PendingWindow == TimeSpan.FromMinutes(20),
            "latest winning pending definition must replace the earlier pending target");
        Ensure(dynamic.Consumed == 1,
            "recording pending definitions must not forgive already charged consumption");
    }

    [Test]
    public async Task LosingCandidateCannotMutateStableImmediateTarget()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(
            options, 3, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate));
        var dynamic = Current(server).Controller.GlobalRateStateForTests!.DynamicFixedWindowForTests!;

        SharpLinkServer.AfterAdmissionCandidateBuiltForTests = static (_, _) =>
            throw new InvalidOperationException("prototype candidate fault");
        try
        {
            try
            {
                publicServer.UpdateAdmissionControl(options => Configure(
                    options, 1, TimeSpan.FromMinutes(10), DynamicFixedWindowActivationMode.Immediate));
                throw new Exception("assert failed: injected candidate fault did not escape");
            }
            catch (InvalidOperationException exception) when (exception.Message == "prototype candidate fault")
            {
            }
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
        }

        Ensure(dynamic.CurrentPermitLimit == 3 && dynamic.CurrentWindow == TimeSpan.FromHours(1),
            "candidate construction/failure must not mutate the live stable target");
        Ensure(!dynamic.HasPendingUpdate,
            "losing Immediate candidate must not leave a pending definition behind");
    }

    [Test]
    public async Task ImmediateIncreaseGrantsExistingServerQueuedRateWaiterOnSameLedger()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(
            options, 1, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate, queue: true));
        var source = Current(server);
        var dynamic = source.Controller.GlobalRateStateForTests!.DynamicFixedWindowForTests!;

        var first = await source.Controller.AcquireAsync(CreateContext(), 1, true, CancellationToken.None);
        Ensure(first.IsAcquired, "first request must consume the only permit");
        first.Lease!.Dispose();

        var queued = source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => source.Kernel.QueuedCalls == 1 && dynamic.WaitingCount == 1,
            "second request must own one outer queue reservation and one underlying waiter");

        publicServer.UpdateAdmissionControl(options => Configure(
            options, 2, TimeSpan.FromHours(1), DynamicFixedWindowActivationMode.Immediate, queue: true));

        var admitted = await queued;
        Ensure(admitted.IsAcquired,
            "Immediate increase must grant the old queued waiter from the same stable ledger");
        admitted.Lease!.Dispose();
        var replacement = Current(server);
        Ensure(replacement.Kernel.QueuedCalls == 0 && replacement.Kernel.QueuedBytes == 0,
            "outer queue accounting must drain exactly once after target update");
        Ensure(dynamic.WaitingCount == 0 && dynamic.Consumed == 2,
            "underlying waiter must drain exactly once and charge the same ledger");
    }

    [Test]
    public async Task DefaultFixedWindowPathRemainsLegacyStatePreservingModel()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            options.Global.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 3;
                rate.Window = TimeSpan.FromHours(1);
            }));

        var sourceRate = Current(server).Controller.GlobalRateStateForTests!;
        Ensure(!sourceRate.IsDynamicFixedWindow,
            "prototype must be opt-in and leave the #333 default path unchanged");

        publicServer.UpdateAdmissionControl(options =>
            options.Global.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 2;
                rate.Window = TimeSpan.FromMinutes(30);
            }));
        var replacementRate = Current(server).Controller.GlobalRateStateForTests!;
        Ensure(!replacementRate.IsDynamicFixedWindow && !ReferenceEquals(sourceRate, replacementRate),
            "default changed-definition update must still create the current #333 successor state");
    }

    private static void Configure(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window,
        DynamicFixedWindowActivationMode activationMode,
        bool queue = false)
    {
        options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });
        options.GlobalFixedWindowActivationModeForPrototype = activationMode;
        if (!queue)
            return;
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 4096;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-fixed-integration", null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
