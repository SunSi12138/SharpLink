using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicPartitionTransactionalTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> Selector =
        static context => context.ConnectionId;

    [Test]
    public async Task PreparedUpdateFailureBeforeFirstMutationShouldLeaveLivePartitionStateUntouched()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(ConfigureSource);

        var source = server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");
        var kernel = source.Kernel;
        var pool = source.Controller.PartitionStateForTests ??
            throw new Exception("assert failed: expected partition namespace");
        var context = new SharpLinkAdmissionContext(
            101,
            202,
            RpcMethodKind.Unary,
            "tenant-a",
            null,
            null,
            null);

        var holder = await source.Controller.AcquireAsync(
            context,
            retainedBytes: 1,
            allowQueue: false,
            CancellationToken.None);
        Ensure(holder.IsAcquired,
            "source partition concurrency permit must be held before the injected failure");

        pool.BeforeUpdateMutationForTests = static () =>
            throw new InvalidOperationException("injected partition preparation failure");

        try
        {
            var failure = CaptureFailure(() => publicServer.UpdateAdmissionControl(ConfigureTarget));
            Ensure(failure is InvalidOperationException &&
                   failure.Message == "injected partition preparation failure",
                "failure injected after target preparation must escape without publication");

            Ensure(ReferenceEquals(source, server.CurrentAdmissionProgramForTests),
                "failed prepared update must leave the exact source publication current");
            Ensure(pool.MaxPartitionsForTests == 2 &&
                   pool.RuntimeGenerationCount == 1 &&
                   kernel.LiveProgramCount == 1 &&
                   kernel.RetiredProgramCount == 0,
                "failed preparation must not install target limits, generations, or a lingering candidate program");

            var rejected = await source.Controller.AcquireAsync(
                context,
                retainedBytes: 1,
                allowQueue: false,
                CancellationToken.None);
            Ensure(!rejected.IsAcquired && rejected.Reason == "concurrency",
                "old 1-permit concurrency budget must remain authoritative after failed target preparation");
        }
        finally
        {
            pool.BeforeUpdateMutationForTests = null;
            holder.Lease!.Dispose();
        }

        Ensure(kernel.ActivePermits == 0 &&
               kernel.QueuedCalls == 0 &&
               kernel.QueuedBytes == 0,
            "injected failure path must leave admission accounting balanced after the retained holder drains");
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static void ConfigureSource(SharpLinkAdmissionControlOptions options)
        => options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 2;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseConcurrency(1);
        });

    private static void ConfigureTarget(SharpLinkAdmissionControlOptions options)
        => options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 1;
            partition.IdleTimeout = TimeSpan.FromMinutes(1);
            partition.UseConcurrency(2);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 8;
                rate.TokensPerPeriod = 8;
                rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
            });
        });

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
