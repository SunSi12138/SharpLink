using System.Net;
using System.Threading;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicUpdateReviewRegressionTests
{
    [Test]
    public async Task MultiScopeRequestShouldNotCombineLeasesAcrossTargetEpochs()
    {
        const int requestCount = 8;
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(100);
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });

        var source = Current(server);
        var sourceUse = source.TryAcquireUse();
        Ensure(sourceUse, "test must retain generation N while requests straddle publication");
        var global = source.Controller.GlobalConcurrencyStateForTests!;
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        using var allAtContract = new CountdownEvent(requestCount);
        using var releaseContract = new ManualResetEventSlim();
        var arrivals = 0;
        contract.BeforeAttemptAcquireForTests = () =>
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival > requestCount)
                return;
            allAtContract.Signal();
            releaseContract.Wait();
        };

        AdmissionDecision[] decisions = [];
        try
        {
            var requests = new Task<AdmissionDecision>[requestCount];
            for (var index = 0; index < requests.Length; index++)
            {
                requests[index] = Task.Run(async () => await source.Controller.AcquireAsync(
                    CreateContext(), 1, false, CancellationToken.None));
            }

            Ensure(allAtContract.Wait(TimeSpan.FromSeconds(5)),
                "every request must acquire Global under N and stop immediately before Contract");
            Ensure(global.ActiveCount == requestCount && contract.ActiveCount == 0,
                "barrier must reproduce the cross-slot prefix acquired under N");

            // N = Global 100 / Contract 1 -> N+1 = Global 1 / Contract 100. A per-limiter
            // epoch check allows all request prefixes to keep N's Global permits and then acquire
            // N+1's Contract permits. The request-level transaction must instead roll back every
            // cross-epoch prefix and retry the complete slot set under one stable target version.
            publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.AddContract(101, rule => rule.UseConcurrency(100));
            });
            Ensure(global.PermitLimit == 1 && contract.PermitLimit == 100,
                "generation N+1 must be fully published before the Contract barrier opens");

            contract.BeforeAttemptAcquireForTests = null;
            releaseContract.Set();
            decisions = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

            var acquired = 0;
            var rejected = 0;
            foreach (var decision in decisions)
            {
                if (decision.IsAcquired)
                    acquired++;
                else if (decision.Reason == "concurrency")
                    rejected++;
            }
            Ensure(acquired == 1 && rejected == requestCount - 1,
                "a request may linearize only to complete N or complete N+1; the cross-epoch combination must not admit all prefixes");
            Ensure(global.ActiveCount == 1 && contract.ActiveCount == 1,
                "only the single N+1 request may own the final Global/Contract capacity");
        }
        finally
        {
            contract.BeforeAttemptAcquireForTests = null;
            releaseContract.Set();
            foreach (var decision in decisions)
                decision.Lease?.Dispose();
            if (sourceUse)
                source.ReleaseUse();
        }

        Ensure(global.ActiveCount == 0 && contract.ActiveCount == 0,
            "cross-epoch regression must leave both shared concurrency states drained");
    }

    [Test]
    public async Task QueuedGrantShouldRecheckEpochUnderLimiterLock()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueuedScopes(
            options,
            globalConcurrency: 1,
            contractConcurrency: 2));

        var source = Current(server);
        var kernel = source.Kernel;
        var global = source.Controller.GlobalConcurrencyStateForTests!;
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        var holder = await source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => kernel.QueuedCalls == 1 && global.WaitingCount == 1,
            "second request must own the outer queue reservation and wait on Global");

        using var grantVersionRead = new ManualResetEventSlim();
        using var allowGrantLock = new ManualResetEventSlim();
        using var grantObservedOdd = new ManualResetEventSlim();
        using var firstResize = new ManualResetEventSlim();
        using var releaseResize = new ManualResetEventSlim();
        var blockGrantOnce = 0;
        global.AfterStableGrantVersionReadForTests = () =>
        {
            if (Interlocked.Exchange(ref blockGrantOnce, 1) != 0)
                return;
            grantVersionRead.Set();
            allowGrantLock.Wait();
        };
        kernel.ConcurrencyTargetTransitionObservedForTests = () => grantObservedOdd.Set();
        kernel.AfterConcurrencyResizeForTests = (index, total) =>
        {
            if (index != 0 || total != 2)
                return;
            firstResize.Set();
            releaseResize.Wait();
        };

        try
        {
            var releaseHolder = Task.Run(() => holder.Lease!.Dispose());
            Ensure(grantVersionRead.Wait(TimeSpan.FromSeconds(5)),
                "release must read a stable grant epoch before it enters the Global limiter lock");

            var update = Task.Run(() => publicServer.UpdateAdmissionControl(options => ConfigureQueuedScopes(
                options,
                globalConcurrency: 2,
                contractConcurrency: 1)));
            Ensure(firstResize.Wait(TimeSpan.FromSeconds(5)),
                "writer must open the epoch and stop after Global resize but before Contract resize");
            Ensure(global.PermitLimit == 2 && contract.PermitLimit == 2,
                "test must expose the physical mixed target interval");

            // The original bug read even, then another writer opened odd before this lock. The
            // second version read under the limiter lock must reject that stale authorization and
            // leave the FIFO waiter resident until the complete policy becomes stable.
            allowGrantLock.Set();
            Ensure(grantObservedOdd.Wait(TimeSpan.FromSeconds(5)),
                "grant path must notice that its previously read epoch became stale");
            Ensure(!queued.IsCompleted && global.WaitingCount == 1,
                "queued lease must not escape while the target set is physically mixed");

            releaseResize.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            await releaseHolder.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(global.PermitLimit == 2 && contract.PermitLimit == 1,
                "update must publish the complete N+1 target set");
            Ensure(global.WaitingCount == 0 && global.ActiveCount == 1,
                "post-commit flush must synchronously transfer the newly available Global permit to the oldest waiter before Update returns");

            var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(admitted.IsAcquired && contract.ActiveCount == 1,
                "the queued request may complete only after its versioned Global grant composes with N+1 Contract");
            admitted.Lease!.Dispose();
        }
        finally
        {
            allowGrantLock.Set();
            releaseResize.Set();
            global.AfterStableGrantVersionReadForTests = null;
            kernel.ConcurrencyTargetTransitionObservedForTests = null;
            kernel.AfterConcurrencyResizeForTests = null;
            holder.Lease?.Dispose();
        }

        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 &&
               global.ActiveCount == 0 && contract.ActiveCount == 0,
            "queued grant race regression must drain queue and concurrency accounting exactly");
    }

    [Test]
    public async Task DisableReenableShouldReuseMostRecentlyPublishedReaddedConcurrencyLineage()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(8);
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });
        var original = Current(server);
        var oldContract = original.Controller.ContractConcurrencyStateForTests(101)!;
        var originalUse = original.TryAcquireUse();
        Ensure(originalUse, "test must retain the old removed Contract lineage A");

        try
        {
            publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(8));
            publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(8);
                options.AddContract(101, rule => rule.UseConcurrency(1));
            });
            var readded = Current(server);
            var newContract = readded.Controller.ContractConcurrencyStateForTests(101)!;
            Ensure(!ReferenceEquals(oldContract, newContract),
                "remove then re-add must create the newer Contract lineage B");

            var readdedUse = readded.TryAcquireUse();
            Ensure(readdedUse, "test must retain lineage B across Disable");
            AdmissionDecision holder = default;
            try
            {
                holder = await readded.Controller.AcquireAsync(
                    CreateContext(), 1, false, CancellationToken.None);
                Ensure(holder.IsAcquired && newContract.ActiveCount == 1,
                    "lineage B must own its sole Contract permit before Disable");

                publicServer.DisableAdmissionControl();
                publicServer.EnableAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(8);
                    options.AddContract(101, rule => rule.UseConcurrency(1));
                });
                var reenabled = Current(server);
                var selected = reenabled.Controller.ContractConcurrencyStateForTests(101)!;
                Ensure(ReferenceEquals(newContract, selected),
                    "re-enable must bind to most recently published Contract lineage B, not historical A");

                var blocked = await reenabled.Controller.AcquireAsync(
                    CreateContext(), 1, false, CancellationToken.None);
                Ensure(!blocked.IsAcquired && blocked.Reason == "concurrency" &&
                       newContract.ActiveCount == 1,
                    "the active B holder must constrain the re-enabled generation to the configured limit of one");

                holder.Lease!.Dispose();
                holder = default;
                var recovered = await reenabled.Controller.AcquireAsync(
                    CreateContext(), 1, false, CancellationToken.None);
                Ensure(recovered.IsAcquired,
                    "re-enabled generation must recover only after the retained B holder releases");
                recovered.Lease!.Dispose();
            }
            finally
            {
                holder.Lease?.Dispose();
                if (readdedUse)
                    readded.ReleaseUse();
            }
        }
        finally
        {
            if (originalUse)
                original.ReleaseUse();
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
        => new(101, 202, RpcMethodKind.Unary, "dynamic-update-review", null, null);

    private static void ConfigureQueuedScopes(
        SharpLinkAdmissionControlOptions options,
        int globalConcurrency,
        int contractConcurrency)
    {
        options.Global.UseConcurrency(globalConcurrency);
        options.AddContract(101, rule => rule.UseConcurrency(contractConcurrency));
        options.MaxQueuedCalls = 1;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

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
