using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicPartitionGenerationCaptureTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> ConnectionSelector =
        static context => context.ConnectionId;

    [Test]
    public async Task CapturedProgramShouldPreservePartitionRuntimePolicyAcrossSameSelectorUpdate()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigurePolicy(options, includeConcurrency: true));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must retain the captured N program across publication");

        try
        {
            var seeded = await source.Controller.AcquireAsync(
                Context("existing"), 1, false, CancellationToken.None);
            Ensure(seeded.IsAcquired, "existing key must be materialized under N before update");
            seeded.Lease!.Dispose();

            publicServer.UpdateAdmissionControl(options => ConfigurePolicy(options, includeConcurrency: false));
            var replacement = Current(server);
            Ensure(ReferenceEquals(source.Controller.PartitionStateForTests, replacement.Controller.PartitionStateForTests),
                "same selector update must preserve the namespace pool");

            var oldExistingFirst = await source.Controller.AcquireAsync(
                Context("existing"), 1, false, CancellationToken.None);
            Ensure(oldExistingFirst.IsAcquired,
                "captured N must still acquire its pre-update existing-key runtime generation");
            using var oldExistingLease = oldExistingFirst.Lease!;
            var oldExistingSecond = await source.Controller.AcquireAsync(
                Context("existing"), 1, false, CancellationToken.None);
            Ensure(!oldExistingSecond.IsAcquired && oldExistingSecond.Reason == "concurrency",
                "captured N must select its own runtime generation for an entry that existed before N+1");

            var oldFutureFirst = await source.Controller.AcquireAsync(
                Context("future"), 1, false, CancellationToken.None);
            Ensure(oldFutureFirst.IsAcquired,
                "captured N must materialize its runtime generation for a key first seen after N+1");
            using var oldFutureLease = oldFutureFirst.Lease!;
            var oldFutureSecond = await source.Controller.AcquireAsync(
                Context("future"), 1, false, CancellationToken.None);
            Ensure(!oldFutureSecond.IsAcquired && oldFutureSecond.Reason == "concurrency",
                "captured N must lazily create its own runtime generation for a key first seen after N+1");

            var currentFirst = await replacement.Controller.AcquireAsync(
                Context("current-first"), 1, false, CancellationToken.None);
            var currentSecond = await replacement.Controller.AcquireAsync(
                Context("current-first"), 1, false, CancellationToken.None);
            Ensure(currentFirst.IsAcquired && currentSecond.IsAcquired,
                "N+1 must create a future key directly under its no-concurrency policy");
            using var currentFirstLease = currentFirst.Lease!;
            using var currentSecondLease = currentSecond.Lease!;

            var oldCurrentFirst = await source.Controller.AcquireAsync(
                Context("current-first"), 1, false, CancellationToken.None);
            Ensure(oldCurrentFirst.IsAcquired,
                "an N+1-created key must retain the still-live captured N runtime generation");
            using var oldCurrentLease = oldCurrentFirst.Lease!;
            var oldCurrentSecond = await source.Controller.AcquireAsync(
                Context("current-first"), 1, false, CancellationToken.None);
            Ensure(!oldCurrentSecond.IsAcquired && oldCurrentSecond.Reason == "concurrency",
                "N+1-first key creation must not erase the captured N partition limit");

            var newExistingFirst = await replacement.Controller.AcquireAsync(
                Context("existing"), 1, false, CancellationToken.None);
            var newExistingSecond = await replacement.Controller.AcquireAsync(
                Context("existing"), 1, false, CancellationToken.None);
            Ensure(newExistingFirst.IsAcquired && newExistingSecond.IsAcquired,
                "N+1 removed partition concurrency and must not inherit N's concurrency target");
            using var newExistingFirstLease = newExistingFirst.Lease!;
            using var newExistingSecondLease = newExistingSecond.Lease!;
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    private static void ConfigurePolicy(
        SharpLinkAdmissionControlOptions options,
        bool includeConcurrency)
    {
        options.UsePartition(ConnectionSelector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            if (includeConcurrency)
                partition.UseConcurrency(1);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 100;
                rate.TokensPerPeriod = 100;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
        });
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext Context(string connectionId)
        => new(101, 202, RpcMethodKind.Unary, connectionId, null, null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
