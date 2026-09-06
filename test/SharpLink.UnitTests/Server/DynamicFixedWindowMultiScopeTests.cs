using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowMultiScopeTests
{
    [Test]
    public async Task SynchronousAttemptShouldUseOneProgramSnapshotAcrossAllFixedScopes()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureFixedScopes(options, 3, 3, 3));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must pin the old immutable program snapshot");

        try
        {
            await ConsumeAsync(source);
            var sourceGlobal = Fixed(source.Controller.GlobalRateStateForTests);
            var sourceContract = Fixed(source.Controller.ContractRateStateForTests(101));
            var sourceMethod = Fixed(source.Controller.MethodRateStateForTests(101, 202));

            publicServer.UpdateAdmissionControl(options => ConfigureFixedScopes(options, 1, 1, 1));
            var target = Current(server);
            var targetGlobal = Fixed(target.Controller.GlobalRateStateForTests);
            var targetContract = Fixed(target.Controller.ContractRateStateForTests(101));
            var targetMethod = Fixed(target.Controller.MethodRateStateForTests(101, 202));

            Ensure(sourceGlobal.CounterIdentityForTests == targetGlobal.CounterIdentityForTests &&
                   sourceContract.CounterIdentityForTests == targetContract.CounterIdentityForTests &&
                   sourceMethod.CounterIdentityForTests == targetMethod.CounterIdentityForTests,
                "each logical scope must keep exactly one stable FixedWindow counter across the update");

            await EnsureRateRejectedAsync(target,
                "the newly published snapshot must apply the immediate limit-one target at every scope");

            await ConsumeAsync(source);
            Ensure(sourceGlobal.ConsumedForTests == 2 &&
                   sourceContract.ConsumedForTests == 2 &&
                   sourceMethod.ConsumedForTests == 2,
                "the pinned old snapshot may finish under its immutable limits while charging the same counters");

            await EnsureRateRejectedAsync(target,
                "old-snapshot completion must not mint quota for the new snapshot");
        }
        finally
        {
            source.ReleaseUse();
        }

        Ensure(source.IsReclaimed && Current(server).Kernel.RateStateCount == 3,
            "after the old snapshot drains only the three current FixedWindow views must remain registered");
    }

    [Test]
    public async Task QueuedContinuationMayStraddleUpdateButMustChargeEachScopeExactlyOnce()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureFixedScopes(options, globalLimit: 2, contractLimit: 1, methodLimit: 3);
            ConfigureQueue(options);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "queued old-program request must keep its source views alive");
        var kernel = source.Kernel;

        try
        {
            await ConsumeAsync(source);
            var sourceGlobal = Fixed(source.Controller.GlobalRateStateForTests);
            var sourceContract = Fixed(source.Controller.ContractRateStateForTests(101));
            var sourceMethod = Fixed(source.Controller.MethodRateStateForTests(101, 202));

            var queued = source.Controller.AcquireAsync(
                CreateContext(), 7, allowQueue: true, CancellationToken.None).AsTask();
            await WaitUntilAsync(
                () => kernel.QueuedCalls == 1 && kernel.QueuedBytes == 7 &&
                      sourceContract.WaitingCount == 1,
                "second old request must retain its Global rate grant and wait at Contract");
            Ensure(sourceGlobal.ConsumedForTests == 2 &&
                   sourceContract.ConsumedForTests == 1 &&
                   sourceMethod.ConsumedForTests == 1,
                "before update the queued request may charge only scopes it has actually passed");

            publicServer.UpdateAdmissionControl(options =>
            {
                ConfigureFixedScopes(options, globalLimit: 1, contractLimit: 2, methodLimit: 1);
                ConfigureQueue(options);
            });
            var target = Current(server);
            var targetGlobal = Fixed(target.Controller.GlobalRateStateForTests);
            var targetContract = Fixed(target.Controller.ContractRateStateForTests(101));
            var targetMethod = Fixed(target.Controller.MethodRateStateForTests(101, 202));

            var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(admitted.IsAcquired,
                "the queued continuation may straddle publication: retained Global stays charged, Contract wakes on the published increase, and old Method completes");
            admitted.Lease!.Dispose();

            Ensure(sourceGlobal.ConsumedForTests == 2 && targetGlobal.ConsumedForTests == 2 &&
                   sourceContract.ConsumedForTests == 2 && targetContract.ConsumedForTests == 2 &&
                   sourceMethod.ConsumedForTests == 2 && targetMethod.ConsumedForTests == 2,
                "straddling must still charge every shared scope counter exactly once");
            Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && sourceContract.WaitingCount == 0,
                "queued continuation must release the one authoritative outer queue reservation exactly once");

            await EnsureRateRejectedAsync(target,
                "the current snapshot must see the consumed shared Global counter behind its limit-one target");
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task WinningFixedTargetMustNotWakeQueuedWorkBeforeProgramPublication()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureGlobalFixed(options, 1);
            ConfigureQueue(options);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "queued old-program request must keep its source view alive");
        using var candidateBuilt = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();

        try
        {
            await ConsumeAsync(source);
            var queued = source.Controller.AcquireAsync(
                CreateContext(), 5, allowQueue: true, CancellationToken.None).AsTask();
            await WaitUntilAsync(
                () => source.Kernel.QueuedCalls == 1 &&
                      Fixed(source.Controller.GlobalRateStateForTests).WaitingCount == 1,
                "old rate waiter must be resident before the update candidate is built");

            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) ||
                    Fixed(candidate.Controller.GlobalRateStateForTests).PermitLimit != 3)
                {
                    return;
                }

                candidateBuilt.Set();
                if (!releaseCandidate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("dynamic FixedWindow candidate publication barrier timed out");
            };

            var updateTask = Task.Run(() => publicServer.UpdateAdmissionControl(options =>
            {
                ConfigureGlobalFixed(options, 3);
                ConfigureQueue(options);
            }));
            Ensure(candidateBuilt.Wait(TimeSpan.FromSeconds(5)),
                "winning target must reach the deterministic post-build/pre-publication barrier");
            Ensure(!queued.IsCompleted && ReferenceEquals(source, Current(server)),
                "candidate construction must not leak the larger queued limit before the program pointer changes");

            releaseCandidate.Set();
            await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            var target = Current(server);
            Ensure(!ReferenceEquals(source, target), "the new program must be visible before queued target activation completes");

            var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(admitted.IsAcquired, "published immediate increase must wake the queued attempt");
            admitted.Lease!.Dispose();
            Ensure(Fixed(target.Controller.GlobalRateStateForTests).ConsumedForTests == 2 &&
                   target.Kernel.QueuedCalls == 0 && target.Kernel.QueuedBytes == 0,
                "post-publication wake must charge once and drain outer queue accounting");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            releaseCandidate.Set();
            source.ReleaseUse();
        }
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
           throw new Exception("assert failed: expected enabled admission publication");

    private static DynamicFixedWindowRateLimiter Fixed(AdmissionRateState? state)
        => state?.FixedWindowForTests ??
           throw new Exception("assert failed: expected specialized DynamicFixedWindow rate state");

    private static void ConfigureFixedScopes(
        SharpLinkAdmissionControlOptions options,
        int globalLimit,
        int contractLimit,
        int methodLimit)
    {
        ConfigureGlobalFixed(options, globalLimit);
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = contractLimit;
            rate.Window = TimeSpan.FromHours(1);
        }));
        options.AddMethod(101, 202, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = methodLimit;
            rate.Window = TimeSpan.FromHours(1);
        }));
    }

    private static void ConfigureGlobalFixed(
        SharpLinkAdmissionControlOptions options,
        int permitLimit)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = TimeSpan.FromHours(1);
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

    private static async Task ConsumeAsync(AdmissionProgram program)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(decision.IsAcquired, "expected admission request to be accepted");
        decision.Lease!.Dispose();
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-fixed-multi-scope", null, null);

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
