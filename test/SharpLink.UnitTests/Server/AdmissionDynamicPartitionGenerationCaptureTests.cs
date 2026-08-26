using System.Net;
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
        var oldExistingSecond = await source.Controller.AcquireAsync(
            Context("existing"), 1, false, CancellationToken.None);
        Ensure(oldExistingFirst.IsAcquired &&
               !oldExistingSecond.IsAcquired &&
               oldExistingSecond.Reason == "concurrency",
            "captured N must select its own runtime generation for an entry that existed before N+1");

        var oldFutureFirst = await source.Controller.AcquireAsync(
            Context("future"), 1, false, CancellationToken.None);
        var oldFutureSecond = await source.Controller.AcquireAsync(
            Context("future"), 1, false, CancellationToken.None);
        Ensure(oldFutureFirst.IsAcquired &&
               !oldFutureSecond.IsAcquired &&
               oldFutureSecond.Reason == "concurrency",
            "captured N must lazily create its own runtime generation for a key first seen after N+1");

        var newExistingFirst = await replacement.Controller.AcquireAsync(
            Context("existing"), 1, false, CancellationToken.None);
        var newExistingSecond = await replacement.Controller.AcquireAsync(
            Context("existing"), 1, false, CancellationToken.None);
        Ensure(newExistingFirst.IsAcquired && newExistingSecond.IsAcquired,
            "N+1 removed partition concurrency and must not inherit N's concurrency target");

        oldExistingFirst.Lease!.Dispose();
        oldFutureFirst.Lease!.Dispose();
        newExistingFirst.Lease!.Dispose();
        newExistingSecond.Lease!.Dispose();
        source.ReleaseUse();
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
