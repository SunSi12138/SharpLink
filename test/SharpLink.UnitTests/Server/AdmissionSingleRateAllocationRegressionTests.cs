using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionSingleRateAllocationRegressionTests
{
    [Test]
    public async Task ImmediateSingleRateShouldAvoidTransientLeaseArrays()
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1_000_000_000;
            rate.TokensPerPeriod = 1_000_000_000;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue247-rate", null, null);

        for (var index = 0; index < 2_000; index++)
        {
            var warm = await controller.AcquireAsync(
                context, 1, false, CancellationToken.None);
            warm.Lease!.Dispose();
        }

        const int iterations = 20_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var decision = await controller.AcquireAsync(
                context, 1, false, CancellationToken.None);
            decision.Lease!.Dispose();
        }
        var bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        if (bytesPerCall > 256)
            throw new InvalidOperationException(
                $"single-rate immediate admission allocated {bytesPerCall} B/call after warm-up");
    }
}
