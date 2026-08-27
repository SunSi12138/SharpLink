using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicUpdateRateRetentionTests
{
    [Test]
    [Arguments(RateKind.TokenBucket)]
    [Arguments(RateKind.FixedWindow)]
    [Arguments(RateKind.SlidingWindow)]
    public async Task OldQueuedRetainedRateLeaseShouldSurviveUpdate(RateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(options, kind, contractConcurrency: 1, queueCalls: 2));
        var source = Current(server);
        var rateState = source.Controller.GlobalRateStateForTests!;
        var contractState = source.Controller.ContractConcurrencyStateForTests(101)!;
        using var blocker = contractState.AttemptAcquire(1);
        Ensure(blocker.IsAcquired,
            $"{kind}: test must occupy downstream Contract concurrency without consuming global rate");

        var queued = source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => source.Kernel.QueuedCalls == 1 && contractState.WaitingCount == 1,
            $"{kind}: generation N request must retain its rate lease while waiting downstream");

        publicServer.UpdateAdmissionControl(options => Configure(
            options,
            kind,
            contractConcurrency: 2,
            queueCalls: 4));
        var replacement = Current(server);
        Ensure(ReferenceEquals(rateState, replacement.Controller.GlobalRateStateForTests),
            $"{kind}: update must reuse the exact unchanged rate state");

        var exhausted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            $"{kind}: retained generation-N lease must still consume the shared rate quota");

        blocker.Dispose();
        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired,
            $"{kind}: old queued Request must reuse its retained rate lease after N+1 publication");
        admitted.Lease!.Dispose();
        Ensure(replacement.Controller.QueuedCalls == 0 && replacement.Controller.QueuedBytes == 0 &&
               replacement.Controller.ActivePermits == 0,
            $"{kind}: retained-lease update path must drain shared accounting exactly");
    }

    private static void Configure(
        SharpLinkAdmissionControlOptions options,
        RateKind kind,
        int contractConcurrency,
        int queueCalls)
    {
        ConfigureRate(options.Global, kind);
        options.AddContract(101, rule => rule.UseConcurrency(contractConcurrency));
        options.MaxQueuedCalls = queueCalls;
        options.MaxQueuedBytes = 4096;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

    private static void ConfigureRate(SharpLinkAdmissionRuleOptions rule, RateKind kind)
    {
        switch (kind)
        {
            case RateKind.TokenBucket:
                rule.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 1;
                    rate.TokensPerPeriod = 1;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.FixedWindow:
                rule.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 1;
                    rate.Window = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.SlidingWindow:
                rule.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 1;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 2;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
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
        => new(101, 202, RpcMethodKind.Unary, "dynamic-update-rate-retention", null, null);

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

    public enum RateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }
}
