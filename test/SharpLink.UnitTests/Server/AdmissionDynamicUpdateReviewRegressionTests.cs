using System.Net;
using System.Threading;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicUpdateReviewRegressionTests
{
    [Test]
    public async Task MultiScopeResizeShouldHaveOneReaderVisibleCommitBoundary()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            options.AddContract(101, rule => rule.UseConcurrency(100));
        });
        var source = Current(server);
        var kernel = source.Kernel;
        var global = source.Controller.GlobalConcurrencyStateForTests!;
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        var holder = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(holder.IsAcquired && global.ActiveCount == 1 && contract.ActiveCount == 1,
            "generation N must occupy the old Global limit before the cross-scope resize");

        using var firstResize = new ManualResetEventSlim();
        using var releaseResize = new ManualResetEventSlim();
        using var readerObserved = new ManualResetEventSlim();
        kernel.AfterConcurrencyResizeForTests = (index, total) =>
        {
            if (index != 0 || total != 2)
                return;
            firstResize.Set();
            releaseResize.Wait();
        };
        kernel.ConcurrencyTargetTransitionObservedForTests = () => readerObserved.Set();

        try
        {
            var updateTask = Task.Run(() => publicServer.UpdateAdmissionControl(options =>
            {
                options.Global.UseConcurrency(100);
                options.AddContract(101, rule => rule.UseConcurrency(1));
            }));
            Ensure(firstResize.Wait(TimeSpan.FromSeconds(5)),
                "test must stop the writer after Global grows and before Contract shrinks");
            Ensure(global.PermitLimit == 100 && contract.PermitLimit == 100,
                "barrier must expose the intentionally mixed physical targets to the test");

            var requestTask = Task.Run(async () => await source.Controller.AcquireAsync(
                CreateContext(), 1, false, CancellationToken.None));
            Ensure(readerObserved.Wait(TimeSpan.FromSeconds(5)),
                "request must observe that the concurrency target epoch is in transition");
            Ensure(!requestTask.IsCompleted,
                "request must not enter while physical target states are mixed");

            releaseResize.Set();
            await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            var decision = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!decision.IsAcquired && decision.Reason == "concurrency",
                "after the atomic target commit the Contract target of one must reject behind the holder");
        }
        finally
        {
            releaseResize.Set();
            kernel.AfterConcurrencyResizeForTests = null;
            kernel.ConcurrencyTargetTransitionObservedForTests = null;
            holder.Lease?.Dispose();
        }
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
        => new(101, 202, RpcMethodKind.Unary, "dynamic-update-review", null, null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
