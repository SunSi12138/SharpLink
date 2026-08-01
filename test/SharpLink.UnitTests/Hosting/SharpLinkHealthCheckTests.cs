using Microsoft.Extensions.Diagnostics.HealthChecks;
using SharpLink.Hosting;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkHealthCheckTests
{
    [Test]
    [Arguments(SharpLinkHealthStatus.Ready, HealthStatus.Healthy)]
    [Arguments(SharpLinkHealthStatus.Draining, HealthStatus.Degraded)]
    [Arguments(SharpLinkHealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task LocalHealthCheckShouldMapReadiness(
        SharpLinkHealthStatus status,
        HealthStatus expected)
    {
        var check = new SharpLinkServerHealthCheck(new FixedReadiness(status));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        if (result.Status != expected)
        {
            throw new Exception(
                $"expected health status {expected}, received {result.Status}");
        }
    }

    [Test]
    public void LocalHealthCheckShouldNotAllocateACompletedTaskPerPoll()
    {
        var check = new SharpLinkServerHealthCheck(new FixedReadiness(SharpLinkHealthStatus.Ready));
        var context = new HealthCheckContext();
        for (var index = 0; index < 1_000; index++)
            _ = check.CheckHealthAsync(context).GetAwaiter().GetResult();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var healthy = 0;
        for (var index = 0; index < 100_000; index++)
        {
            if (check.CheckHealthAsync(context).GetAwaiter().GetResult().Status == HealthStatus.Healthy)
                healthy++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (healthy != 100_000)
            throw new Exception("every cached local health result must remain Healthy");
        if (allocated != 0)
            throw new Exception($"local health polling allocated {allocated} bytes");
    }

    private sealed class FixedReadiness(SharpLinkHealthStatus status) : ISharpLinkServerReadiness
    {
        public SharpLinkHealthStatus Status { get; } = status;
    }
}
